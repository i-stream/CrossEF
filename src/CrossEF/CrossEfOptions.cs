namespace CrossEF;

/// <summary>Global tuning knobs for CrossEF query execution.</summary>
public static class CrossEfOptions
{
    /// <summary>
    /// Maximum number of join keys sent in a single semi-join query (<c>WHERE key IN (...)</c>).
    /// Larger key sets are fetched in batches of this size, which keeps queries below provider
    /// limits such as SQL Server's expression services limit.
    /// </summary>
    public static int MaxSemiJoinKeysPerQuery { get; set; } = 2000;
}
