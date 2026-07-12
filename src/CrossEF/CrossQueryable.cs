using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace CrossEF;

/// <summary>
/// An <see cref="IQueryable{T}"/> whose execution is handled by CrossEF instead of a single
/// EF Core context, allowing the expression tree to reference query roots from multiple
/// <c>DbContext</c> instances.
/// </summary>
public sealed class CrossQueryable<T> : IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    private readonly CrossQueryProvider _provider;

    public CrossQueryable(Expression expression)
        : this(new CrossQueryProvider(), expression)
    {
    }

    public CrossQueryable(CrossQueryProvider provider, Expression expression)
    {
        _provider = provider;
        Expression = expression;
    }

    public Type ElementType => typeof(T);

    public Expression Expression { get; }

    public IQueryProvider Provider => _provider;

    public IEnumerator<T> GetEnumerator()
        => CrossQueryExecutor.ExecuteSequence<T>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => Iterate(cancellationToken).GetAsyncEnumerator(cancellationToken);

    private async IAsyncEnumerable<T> Iterate([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var results = await CrossQueryExecutor.ExecuteSequenceAsync<T>(Expression, cancellationToken).ConfigureAwait(false);
        foreach (var item in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
