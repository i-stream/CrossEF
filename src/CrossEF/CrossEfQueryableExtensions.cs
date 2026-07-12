using System.Linq.Expressions;

namespace CrossEF;

public static class CrossEfQueryableExtensions
{
    /// <summary>
    /// Marks a query as a CrossEF query, so that operators composed on top of it (joins,
    /// projections, filters, ...) may reference entities from other <c>DbContext</c> instances.
    /// Everything written before this call stays on the owning context and is translated to SQL
    /// as usual, so apply your per-context filters first.
    /// </summary>
    public static IQueryable<T> AsCrossQuery<T>(this IQueryable<T> source)
        => source as CrossQueryable<T> ?? new CrossQueryable<T>(Expression.Constant(source, typeof(IQueryable<T>)));

    /// <summary>
    /// Executes a query that may span multiple <c>DbContext</c> instances and returns the results
    /// as a list. Unlike <c>ToListAsync</c>, this works on a plain EF query without the
    /// <see cref="AsCrossQuery{T}"/> marker.
    /// </summary>
    public static Task<List<T>> ToCrossListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
        => CrossQueryExecutor.ExecuteSequenceAsync<T>(source.Expression, cancellationToken);

    /// <summary>
    /// Synchronous counterpart of <see cref="ToCrossListAsync{T}"/>.
    /// </summary>
    public static List<T> ToCrossList<T>(this IQueryable<T> source)
        => CrossQueryExecutor.ExecuteSequence<T>(source.Expression);
}
