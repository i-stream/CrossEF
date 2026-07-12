using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;

namespace CrossEF;

/// <summary>
/// Query provider that plans and executes LINQ expression trees spanning multiple
/// EF Core <c>DbContext</c> instances.
/// </summary>
public sealed class CrossQueryProvider : IAsyncQueryProvider
{
    private static readonly MethodInfo ExecuteScalarAsyncMethod =
        typeof(CrossQueryExecutor).GetMethod(nameof(CrossQueryExecutor.ExecuteScalarAsync),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = CrossQueryExecutor.GetSequenceElementType(expression.Type);
        return (IQueryable)Activator.CreateInstance(
            typeof(CrossQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new CrossQueryable<TElement>(this, expression);

    public object? Execute(Expression expression)
        => CrossQueryExecutor.ExecuteSync(expression);

    public TResult Execute<TResult>(Expression expression)
    {
        var result = CrossQueryExecutor.ExecuteSync(expression);
        return result is null ? default! : (TResult)result;
    }

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        // EF Core's async operators (CountAsync, FirstAsync, SumAsync, ...) call this with
        // TResult == Task<TActual>.
        if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Task<>))
        {
            var actualType = typeof(TResult).GetGenericArguments()[0];
            var method = ExecuteScalarAsyncMethod.MakeGenericMethod(actualType);
            return (TResult)method.Invoke(null, [expression, cancellationToken])!;
        }

        throw new NotSupportedException(
            $"CrossEF cannot execute an async operation with result type '{typeof(TResult)}'.");
    }
}
