using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace CrossEF;

/// <summary>
/// Plans and executes expression trees that reference query roots from multiple EF Core
/// <c>DbContext</c> instances.
///
/// Pipeline:
///  1. Normalize: closure references to queryables (e.g. <c>ctx.Customers</c>) are evaluated
///     into constants; CrossEF wrappers are unwrapped.
///  2. Collect roots: every EF query root is found and grouped by its query provider
///     (one provider per DbContext instance).
///  3. Plan:
///     - 0 providers  -> pure in-memory query, just compile and run.
///     - 1 provider   -> the whole query is handed to EF unchanged (single SQL statement).
///     - 2+ providers -> the tree is split into maximal single-provider fragments; each fragment
///       executes on its own context (filters written inside the fragment stay in SQL). For the
///       common Join pattern, the outer side is executed first and the inner side is narrowed
///       with a WHERE key IN (...) semi-join before being fetched. The remaining operators run
///       in memory via LINQ to Objects.
/// </summary>
internal static class CrossQueryExecutor
{
    private static readonly MethodInfo MaterializeCoreMethod =
        typeof(CrossQueryExecutor).GetMethod(nameof(MaterializeCoreAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo SemiJoinCoreMethod =
        typeof(CrossQueryExecutor).GetMethod(nameof(SemiJoinCoreAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    // ---------------------------------------------------------------------
    // Entry points
    // ---------------------------------------------------------------------

    internal static List<T> ExecuteSequence<T>(Expression expression)
    {
        var plan = CreatePlanAsync(expression, useAsync: false, CancellationToken.None).GetAwaiter().GetResult();
        if (plan.PassThroughProvider is not null)
            return plan.PassThroughProvider.CreateQuery<T>(plan.Expression).ToList();

        return ((IEnumerable<T>)EvaluateTree(plan.Expression)!).ToList();
    }

    internal static async Task<List<T>> ExecuteSequenceAsync<T>(Expression expression, CancellationToken cancellationToken)
    {
        var plan = await CreatePlanAsync(expression, useAsync: true, cancellationToken).ConfigureAwait(false);
        if (plan.PassThroughProvider is not null)
            return await plan.PassThroughProvider.CreateQuery<T>(plan.Expression).ToListAsync(cancellationToken).ConfigureAwait(false);

        return ((IEnumerable<T>)EvaluateTree(plan.Expression)!).ToList();
    }

    internal static object? ExecuteSync(Expression expression)
    {
        var plan = CreatePlanAsync(expression, useAsync: false, CancellationToken.None).GetAwaiter().GetResult();
        if (plan.PassThroughProvider is not null)
            return plan.PassThroughProvider.Execute(plan.Expression);

        return EvaluateTree(plan.Expression);
    }

    internal static async Task<T> ExecuteScalarAsync<T>(Expression expression, CancellationToken cancellationToken)
    {
        var plan = await CreatePlanAsync(expression, useAsync: true, cancellationToken).ConfigureAwait(false);
        if (plan.PassThroughProvider is not null)
            return await plan.PassThroughProvider.ExecuteAsync<Task<T>>(plan.Expression, cancellationToken).ConfigureAwait(false);

        var value = EvaluateTree(plan.Expression);
        return value is null ? default! : (T)value;
    }

    // ---------------------------------------------------------------------
    // Planning
    // ---------------------------------------------------------------------

    private sealed record Plan(IAsyncQueryProvider? PassThroughProvider, Expression Expression);

    private sealed record Fragment(Expression Node, IAsyncQueryProvider Provider, Type ElementType)
    {
        /// <summary>The expression to hand to the owning EF provider for execution.</summary>
        public Expression ExecutableExpression => SpliceEfQueryableConstants(Node);
    }

    private sealed record SemiJoinInfo(
        Expression OuterExpression,
        List<Fragment> OuterFragments,
        Fragment InnerFragment,
        LambdaExpression OuterKeySelector,
        LambdaExpression InnerKeySelector,
        Type OuterType,
        Type InnerType,
        Type KeyType);

    private static async Task<Plan> CreatePlanAsync(Expression expression, bool useAsync, CancellationToken cancellationToken)
    {
        var normalized = new RootNormalizingVisitor().Visit(expression)!;
        var roots = CollectRoots(normalized);
        var providers = roots.Select(r => r.Provider).Distinct().ToList();

        if (providers.Count == 0)
            return new Plan(null, normalized);

        if (providers.Count == 1)
        {
            // Single DbContext: EF can handle the whole query as one SQL statement. Queryable
            // constants must be spliced back into their expression trees first, because EF's
            // pipeline does not inline pre-evaluated IQueryable constants.
            return new Plan(providers[0], SpliceEfQueryableConstants(normalized));
        }

        // Cross-context query: push single-side predicates written above a join down into the
        // side they filter, so they execute as SQL instead of forcing a full table fetch.
        normalized = new SingleSidePredicatePushdownVisitor().Visit(normalized)!;

        // Then narrow any join side that the projection never reads down to its key column.
        normalized = new SingleSideProjectionPushdownVisitor().Visit(normalized)!;

        var fragments = FindFragments(normalized);
        var substitutions = new Dictionary<Expression, Expression>();

        var semiJoin = TryPlanSemiJoin(normalized, fragments);
        if (semiJoin is not null)
        {
            foreach (var fragment in semiJoin.OuterFragments)
                substitutions[fragment.Node] = await MaterializeFragmentAsync(fragment, useAsync, cancellationToken).ConfigureAwait(false);

            var outerSequence = (IEnumerable)EvaluateTree(new ReplacingVisitor(substitutions).Visit(semiJoin.OuterExpression)!)!;

            substitutions[semiJoin.InnerFragment.Node] =
                await SemiJoinMaterializeAsync(semiJoin, outerSequence, useAsync, cancellationToken).ConfigureAwait(false);
        }

        foreach (var fragment in fragments)
        {
            if (!substitutions.ContainsKey(fragment.Node))
                substitutions[fragment.Node] = await MaterializeFragmentAsync(fragment, useAsync, cancellationToken).ConfigureAwait(false);
        }

        var rewritten = new ReplacingVisitor(substitutions).Visit(normalized)!;
        return new Plan(null, rewritten);
    }

    /// <summary>
    /// Detects the pattern <c>[Where/Select/OrderBy/... on top of] Join(outer, inner, ...)</c>
    /// where outer and inner belong to different contexts, so that the inner side can be fetched
    /// with a <c>WHERE key IN (outer keys)</c> filter instead of a full table scan.
    /// </summary>
    private static SemiJoinInfo? TryPlanSemiJoin(Expression root, List<Fragment> fragments)
    {
        var node = root;
        while (node is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            if (call.Method.Name == nameof(Queryable.Join) && call.Arguments.Count == 5)
            {
                var outerExpression = call.Arguments[0];
                var innerExpression = call.Arguments[1];

                var innerFragment = fragments.FirstOrDefault(f => ReferenceEquals(f.Node, innerExpression));
                if (innerFragment is null)
                    return null;

                var outerFragments = fragments.Where(f => ContainsNode(outerExpression, f.Node)).ToList();
                if (outerFragments.Count == 0
                    || outerFragments.Any(f => ReferenceEquals(f.Provider, innerFragment.Provider)))
                    return null;

                var outerKey = Unquote(call.Arguments[2]);
                var innerKey = Unquote(call.Arguments[3]);
                if (outerKey is null || innerKey is null)
                    return null;

                if (!IsServerFilterableKey(outerKey.ReturnType))
                    return null;

                var generics = call.Method.GetGenericArguments(); // TOuter, TInner, TKey, TResult
                return new SemiJoinInfo(
                    outerExpression, outerFragments, innerFragment,
                    outerKey, innerKey,
                    generics[0], generics[1], generics[2]);
            }

            node = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
            if (node is null)
                return null;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Materialization
    // ---------------------------------------------------------------------

    private static Task<ConstantExpression> MaterializeFragmentAsync(Fragment fragment, bool useAsync, CancellationToken cancellationToken)
        => (Task<ConstantExpression>)MaterializeCoreMethod
            .MakeGenericMethod(fragment.ElementType)
            .Invoke(null, [fragment.Provider, fragment.ExecutableExpression, useAsync, cancellationToken])!;

    private static async Task<ConstantExpression> MaterializeCoreAsync<T>(
        IAsyncQueryProvider provider, Expression expression, bool useAsync, CancellationToken cancellationToken)
    {
        var query = provider.CreateQuery<T>(expression);
        var list = useAsync
            ? await query.ToListAsync(cancellationToken).ConfigureAwait(false)
            : query.ToList();
        return Expression.Constant(list.AsQueryable(), typeof(IQueryable<T>));
    }

    private static Task<ConstantExpression> SemiJoinMaterializeAsync(
        SemiJoinInfo info, IEnumerable outerSequence, bool useAsync, CancellationToken cancellationToken)
        => (Task<ConstantExpression>)SemiJoinCoreMethod
            .MakeGenericMethod(info.OuterType, info.InnerType, info.KeyType)
            .Invoke(null,
            [
                outerSequence, info.OuterKeySelector,
                info.InnerFragment.Provider, info.InnerFragment.ExecutableExpression, info.InnerKeySelector,
                useAsync, cancellationToken
            ])!;

    private static async Task<ConstantExpression> SemiJoinCoreAsync<TOuter, TInner, TKey>(
        IEnumerable outerSequence,
        LambdaExpression outerKeySelector,
        IAsyncQueryProvider innerProvider,
        Expression innerExpression,
        LambdaExpression innerKeySelector,
        bool useAsync,
        CancellationToken cancellationToken)
    {
        var outerKey = (Func<TOuter, TKey>)outerKeySelector.Compile();
        var keys = ((IEnumerable<TOuter>)outerSequence).Select(outerKey).Distinct().ToArray();

        var list = new List<TInner>();
        if (keys.Length == 0)
            return Expression.Constant(list.AsQueryable(), typeof(IQueryable<TInner>));

        var query = innerProvider.CreateQuery<TInner>(innerExpression);
        var parameter = innerKeySelector.Parameters[0];

        // Batch the IN list so huge key sets stay below provider limits (e.g. SQL Server's
        // expression services limit) instead of failing on one enormous query.
        var batchSize = Math.Max(1, CrossEfOptions.MaxSemiJoinKeysPerQuery);
        foreach (var batch in keys.Chunk(batchSize))
        {
            var containsCall = Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TKey)],
                Expression.Constant(batch), innerKeySelector.Body);
            var predicate = Expression.Lambda<Func<TInner, bool>>(containsCall, parameter);

            var filtered = query.Where(predicate);
            list.AddRange(useAsync
                ? await filtered.ToListAsync(cancellationToken).ConfigureAwait(false)
                : filtered.ToList());
        }

        return Expression.Constant(list.AsQueryable(), typeof(IQueryable<TInner>));
    }

    // ---------------------------------------------------------------------
    // Tree analysis
    // ---------------------------------------------------------------------

    private sealed record QueryRoot(Expression Node, IAsyncQueryProvider Provider);

    private static List<QueryRoot> CollectRoots(Expression expression)
    {
        var collector = new RootCollector();
        collector.Visit(expression);
        return collector.Roots;
    }

    private sealed class RootCollector : ExpressionVisitor
    {
        public List<QueryRoot> Roots { get; } = [];

        public override Expression? Visit(Expression? node)
        {
            switch (node)
            {
                case EntityQueryRootExpression entityRoot:
                    Roots.Add(new QueryRoot(entityRoot, entityRoot.QueryProvider
                        ?? throw new InvalidOperationException(
                            "CrossEF found an EF query root without an attached query provider. " +
                            "Query roots must come from a live DbContext (e.g. context.Customers).")));
                    return node;

                case ConstantExpression { Value: IQueryable queryable }
                    when queryable.Provider is IAsyncQueryProvider asyncProvider and not CrossQueryProvider:
                    Roots.Add(new QueryRoot(node, asyncProvider));
                    return node;

                default:
                    return base.Visit(node);
            }
        }
    }

    /// <summary>
    /// Finds the maximal subtrees that reference exactly one EF provider, are queryable-typed and
    /// self-contained (no parameters bound outside the subtree). Each such fragment can be executed
    /// by its own context, keeping its operators translated to SQL.
    /// </summary>
    private static List<Fragment> FindFragments(Expression root)
    {
        var fragments = new List<Fragment>();
        Recurse(root);
        return fragments;

        void Recurse(Expression node)
        {
            var roots = CollectRoots(node);
            if (roots.Count == 0)
                return;

            var providers = roots.Select(r => r.Provider).Distinct().ToList();
            if (providers.Count == 1
                && typeof(IQueryable).IsAssignableFrom(node.Type)
                && !HasUnboundParameters(node))
            {
                fragments.Add(new Fragment(node, providers[0], GetSequenceElementType(node.Type)));
                return;
            }

            foreach (var child in GetChildren(node))
                Recurse(child);
        }
    }

    private static bool HasUnboundParameters(Expression node)
    {
        var probe = new UnboundParameterProbe();
        probe.Visit(node);
        return probe.HasUnbound;
    }

    private sealed class UnboundParameterProbe : ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _declared = [];

        public bool HasUnbound { get; private set; }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            foreach (var parameter in node.Parameters)
                _declared.Add(parameter);
            return base.VisitLambda(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!_declared.Contains(node))
                HasUnbound = true;
            return node;
        }
    }

    private static List<Expression> GetChildren(Expression node)
    {
        var collector = new ChildCollector();
        collector.Visit(node);
        return collector.Children;
    }

    private sealed class ChildCollector : ExpressionVisitor
    {
        private bool _rootSeen;

        public List<Expression> Children { get; } = [];

        public override Expression? Visit(Expression? node)
        {
            if (node is null)
                return null;
            if (!_rootSeen)
            {
                _rootSeen = true;
                return base.Visit(node);
            }

            Children.Add(node);
            return node; // do not descend further
        }
    }

    private static bool ContainsNode(Expression tree, Expression target)
    {
        var finder = new NodeFinder(target);
        finder.Visit(tree);
        return finder.Found;
    }

    private sealed class NodeFinder(Expression target) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public override Expression? Visit(Expression? node)
        {
            if (Found || node is null)
                return node;
            if (ReferenceEquals(node, target))
            {
                Found = true;
                return node;
            }

            return base.Visit(node);
        }
    }

    // ---------------------------------------------------------------------
    // Normalization
    // ---------------------------------------------------------------------

    /// <summary>
    /// Evaluates closure references to queryables (e.g. <c>ctx.Customers</c> or a pre-built query
    /// captured in a variable) into <see cref="ConstantExpression"/>s, and inlines CrossEF
    /// queryables so nested cross queries compose.
    /// </summary>
    private sealed class RootNormalizingVisitor : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is null)
                return null;

            if (node is ConstantExpression { Value: IQueryable wrapped } && wrapped.Provider is CrossQueryProvider)
                return Visit(wrapped.Expression);

            if (node is EntityQueryRootExpression)
                return node;

            if (node is MemberExpression or MethodCallExpression
                && typeof(IQueryable).IsAssignableFrom(node.Type)
                && IsIndependentlyEvaluatable(node)
                && TryEvaluate(node, out var value))
            {
                if (value is IQueryable queryable)
                {
                    if (queryable.Provider is CrossQueryProvider)
                        return Visit(queryable.Expression);
                    return Expression.Constant(queryable, node.Type);
                }
            }

            return base.Visit(node);
        }

        private static bool TryEvaluate(Expression node, out object? value)
        {
            try
            {
                value = Expression.Lambda(node).Compile().DynamicInvoke();
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static bool IsIndependentlyEvaluatable(Expression node)
        {
            var probe = new EvaluatabilityProbe();
            probe.Visit(node);
            return probe.IsEvaluatable;
        }

        /// <summary>
        /// A subtree is safe to evaluate eagerly when it contains no lambdas, no parameters, no EF
        /// query roots and no CrossEF queryables — i.e. it is a plain closure/member chain such as
        /// <c>ctx.Customers</c> or <c>capturedQueryVariable</c>.
        /// </summary>
        private sealed class EvaluatabilityProbe : ExpressionVisitor
        {
            public bool IsEvaluatable { get; private set; } = true;

            public override Expression? Visit(Expression? node)
            {
                if (!IsEvaluatable || node is null)
                    return node;

                if (node is ParameterExpression or LambdaExpression or EntityQueryRootExpression
                    || (node is ConstantExpression { Value: IQueryable q } && q.Provider is CrossQueryProvider))
                {
                    IsEvaluatable = false;
                    return node;
                }

                return base.Visit(node);
            }
        }
    }

    private sealed class ReplacingVisitor(Dictionary<Expression, Expression> replacements) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
            => node is not null && replacements.TryGetValue(node, out var replacement)
                ? replacement
                : base.Visit(node);
    }

    // ---------------------------------------------------------------------
    // Predicate pushdown
    // ---------------------------------------------------------------------

    /// <summary>
    /// Rewrites <c>Join(outer, inner, ...).Where(ti =&gt; predicate)</c> — the shape produced by a
    /// query-syntax <c>where</c> written after a <c>join</c> — into
    /// <c>Join(outer.Where(...), inner, ...)</c> or <c>Join(outer, inner.Where(...), ...)</c>
    /// whenever the predicate references only one side of the join. The pushed predicate then
    /// executes as SQL on the owning context instead of filtering in memory after a full fetch.
    /// Valid for inner joins: a single-side filter commutes with the join.
    /// </summary>
    private sealed class SingleSidePredicatePushdownVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);

            if (visited.Method.DeclaringType != typeof(Queryable)
                || visited.Method.Name != nameof(Queryable.Where)
                || visited.Arguments.Count != 2
                || visited.Arguments[0] is not MethodCallExpression join
                || join.Method.DeclaringType != typeof(Queryable)
                || join.Method.Name != nameof(Queryable.Join)
                || join.Arguments.Count != 5)
            {
                return visited;
            }

            var predicate = Unquote(visited.Arguments[1]);
            var resultSelector = Unquote(join.Arguments[4]);
            if (predicate is null || predicate.Parameters.Count != 1 || resultSelector is null)
                return visited;

            // The join's result selector must be a member-wise projection of its two parameters
            // (the transparent identifier the compiler builds for query syntax), so that member
            // accesses on the Where parameter can be traced back to one side of the join.
            if (resultSelector.Body is not NewExpression { Members: not null } projection)
                return visited;

            var outerParameter = resultSelector.Parameters[0];
            var innerParameter = resultSelector.Parameters[1];
            var memberMap = MapTransparentMembers(resultSelector);
            if (memberMap is null)
                return visited;

            var rewritten = new TransparentMemberRewriter(predicate.Parameters[0], memberMap).Visit(predicate.Body)!;
            if (ReferencesParameter(rewritten, predicate.Parameters[0]))
                return visited; // predicate uses members we could not map — leave it in memory

            var usesOuter = ReferencesParameter(rewritten, outerParameter);
            var usesInner = ReferencesParameter(rewritten, innerParameter);
            if (usesOuter == usesInner)
                return visited; // touches both sides (or neither) — must stay above the join

            return usesOuter
                ? join.Update(join.Object, [
                    MakeWhere(join.Arguments[0], Expression.Lambda(rewritten, outerParameter)),
                    join.Arguments[1], join.Arguments[2], join.Arguments[3], join.Arguments[4]])
                : join.Update(join.Object, [
                    join.Arguments[0],
                    MakeWhere(join.Arguments[1], Expression.Lambda(rewritten, innerParameter)),
                    join.Arguments[2], join.Arguments[3], join.Arguments[4]]);
        }

        private static MethodCallExpression MakeWhere(Expression source, LambdaExpression predicate)
            => Expression.Call(
                typeof(Queryable), nameof(Queryable.Where), [predicate.Parameters[0].Type],
                source, Expression.Quote(predicate));
    }

    /// <summary>
    /// Narrows the unused side of a cross-context join down to its join key when the projection
    /// after the join references only the other side, so the database ships a single key column
    /// instead of full entity rows. Handles both query shapes:
    /// <c>Join(outer, inner, ok, ik, (o, i) =&gt; f(oneSide))</c> (query syntax with no clause
    /// between the join and the select) and <c>Join(...).Select(ti =&gt; f(oneSide))</c>
    /// (transparent identifier). Join multiplicity is preserved: the narrowed side still yields
    /// one key per row, duplicates included.
    /// </summary>
    private sealed class SingleSideProjectionPushdownVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType != typeof(Queryable))
                return visited;

            if (visited.Method.Name == nameof(Queryable.Join) && visited.Arguments.Count == 5)
                return TryNarrowJoin(visited) ?? visited;

            if (visited.Method.Name == nameof(Queryable.Select)
                && visited.Arguments.Count == 2
                && visited.Arguments[0] is MethodCallExpression join
                && join.Method.DeclaringType == typeof(Queryable)
                && join.Method.Name == nameof(Queryable.Join)
                && join.Arguments.Count == 5)
            {
                return TryNarrowSelectOverJoin(visited, join) ?? visited;
            }

            return visited;
        }

        /// <summary>Join whose own result selector references only one side.</summary>
        private static Expression? TryNarrowJoin(MethodCallExpression join)
        {
            var resultSelector = Unquote(join.Arguments[4]);
            if (resultSelector is null || resultSelector.Parameters.Count != 2)
                return null;

            var usesOuter = ReferencesParameter(resultSelector.Body, resultSelector.Parameters[0]);
            var usesInner = ReferencesParameter(resultSelector.Body, resultSelector.Parameters[1]);
            if (usesOuter && usesInner)
                return null;

            return NarrowJoin(join, resultSelector.Body,
                resultSelector.Parameters[0], resultSelector.Parameters[1], usesInner);
        }

        /// <summary>Select over a transparent-identifier join referencing only one side.</summary>
        private static Expression? TryNarrowSelectOverJoin(MethodCallExpression select, MethodCallExpression join)
        {
            var selector = Unquote(select.Arguments[1]);
            var resultSelector = Unquote(join.Arguments[4]);
            if (selector is null || selector.Parameters.Count != 1 || resultSelector is null)
                return null;

            var memberMap = MapTransparentMembers(resultSelector);
            if (memberMap is null)
                return null;

            var rewritten = new TransparentMemberRewriter(selector.Parameters[0], memberMap).Visit(selector.Body)!;
            if (ReferencesParameter(rewritten, selector.Parameters[0]))
                return null; // projection uses members we could not map back to one side

            var usesOuter = ReferencesParameter(rewritten, resultSelector.Parameters[0]);
            var usesInner = ReferencesParameter(rewritten, resultSelector.Parameters[1]);
            if (usesOuter && usesInner)
                return null;

            return NarrowJoin(join, rewritten,
                resultSelector.Parameters[0], resultSelector.Parameters[1], usesInner);
        }

        /// <summary>
        /// Rebuilds the join with the unused side replaced by <c>side.Select(keySelector)</c>
        /// and the projection applied as the join's result selector.
        /// </summary>
        private static Expression? NarrowJoin(
            MethodCallExpression join,
            Expression projectionBody,
            ParameterExpression outerParameter,
            ParameterExpression innerParameter,
            bool usesInner)
        {
            if (!SidesUseDistinctProviders(join))
                return null; // same context: EF translates the whole join itself, leave it alone

            var generics = join.Method.GetGenericArguments(); // TOuter, TInner, TKey, TResult
            var keyType = generics[2];
            var keyParameter = Expression.Parameter(keyType, "joinKey");
            var identity = Expression.Quote(Expression.Lambda(keyParameter, keyParameter));

            if (!usesInner)
            {
                var narrowedInner = Expression.Call(
                    typeof(Queryable), nameof(Queryable.Select), [generics[1], keyType],
                    join.Arguments[1], join.Arguments[3]);
                var newResultSelector = Expression.Lambda(projectionBody, outerParameter, keyParameter);
                return Expression.Call(
                    typeof(Queryable), nameof(Queryable.Join),
                    [generics[0], keyType, keyType, projectionBody.Type],
                    join.Arguments[0], narrowedInner, join.Arguments[2], identity,
                    Expression.Quote(newResultSelector));
            }

            var narrowedOuter = Expression.Call(
                typeof(Queryable), nameof(Queryable.Select), [generics[0], keyType],
                join.Arguments[0], join.Arguments[2]);
            var outerResultSelector = Expression.Lambda(projectionBody, keyParameter, innerParameter);
            return Expression.Call(
                typeof(Queryable), nameof(Queryable.Join),
                [keyType, generics[1], keyType, projectionBody.Type],
                narrowedOuter, join.Arguments[1], identity, join.Arguments[3],
                Expression.Quote(outerResultSelector));
        }

        private static bool SidesUseDistinctProviders(MethodCallExpression join)
        {
            var outerProviders = CollectRoots(join.Arguments[0]).Select(r => r.Provider).Distinct().ToList();
            var innerProviders = CollectRoots(join.Arguments[1]).Select(r => r.Provider).Distinct().ToList();
            return outerProviders.Count > 0
                && innerProviders.Count > 0
                && !outerProviders.Intersect(innerProviders).Any();
        }
    }

    // ---------------------------------------------------------------------
    // Shared pushdown helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Maps the members of a transparent-identifier projection <c>(o, i) =&gt; new { o, i }</c>
    /// back to the lambda parameters they capture, or returns null when the result selector is
    /// not a member-wise projection of its parameters.
    /// </summary>
    private static Dictionary<MemberInfo, ParameterExpression>? MapTransparentMembers(LambdaExpression resultSelector)
    {
        if (resultSelector.Parameters.Count != 2
            || resultSelector.Body is not NewExpression { Members: not null } projection)
        {
            return null;
        }

        var map = new Dictionary<MemberInfo, ParameterExpression>();
        for (var i = 0; i < projection.Members.Count; i++)
        {
            if (projection.Arguments[i] is ParameterExpression parameter
                && resultSelector.Parameters.Contains(parameter))
            {
                map[NormalizeMember(projection.Members[i])] = parameter;
            }
        }

        return map.Count == 0 ? null : map;
    }

    private static MemberInfo NormalizeMember(MemberInfo member)
    {
        // Anonymous-type projections may surface the property getter instead of the property.
        if (member is MethodInfo method && method.DeclaringType is not null)
        {
            foreach (var property in method.DeclaringType.GetProperties())
            {
                if (property.GetMethod == method)
                    return property;
            }
        }

        return member;
    }

    private static bool ReferencesParameter(Expression tree, ParameterExpression parameter)
    {
        var probe = new ParameterReferenceProbe(parameter);
        probe.Visit(tree);
        return probe.Found;
    }

    private sealed class ParameterReferenceProbe(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
                Found = true;
            return node;
        }
    }

    private sealed class TransparentMemberRewriter(
        ParameterExpression transparentParameter,
        Dictionary<MemberInfo, ParameterExpression> memberMap) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == transparentParameter
                && memberMap.TryGetValue(NormalizeMember(node.Member), out var replacement))
            {
                return replacement;
            }

            return base.VisitMember(node);
        }
    }

    /// <summary>
    /// Replaces constants holding EF queryables with the queryable's own expression tree
    /// (e.g. a <c>DbSet</c> constant becomes its <see cref="EntityQueryRootExpression"/>),
    /// producing a tree EF Core's query pipeline can translate.
    /// </summary>
    private static Expression SpliceEfQueryableConstants(Expression expression)
        => new EfConstantSplicingVisitor().Visit(expression)!;

    private sealed class EfConstantSplicingVisitor : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is ConstantExpression { Value: IQueryable queryable }
                && queryable.Provider is IAsyncQueryProvider and not CrossQueryProvider
                && !ReferenceEquals(queryable.Expression, node))
            {
                return Visit(queryable.Expression);
            }

            return base.Visit(node);
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static object? EvaluateTree(Expression expression)
    {
        if (expression is ConstantExpression constant)
            return constant.Value;

        try
        {
            return Expression.Lambda(expression).Compile().DynamicInvoke();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static LambdaExpression? Unquote(Expression expression) => expression switch
    {
        UnaryExpression { NodeType: ExpressionType.Quote } quote => quote.Operand as LambdaExpression,
        LambdaExpression lambda => lambda,
        _ => null,
    };

    private static bool IsServerFilterableKey(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly);
    }

    internal static Type GetSequenceElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
            return type.GetGenericArguments()[0];

        var queryableInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>));
        return queryableInterface?.GetGenericArguments()[0]
            ?? throw new InvalidOperationException($"Cannot determine the element type of '{type}'.");
    }
}
