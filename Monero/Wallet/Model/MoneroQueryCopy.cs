using Monero.Common;

namespace Monero.Wallet.Model;

/// <summary>
/// The bound of the depth of a copy of a query. A query holds queries of its own, and those hold the query of their transaction, so a
/// query that holds itself would be copied forever, until the stack ends. The bound is the one of the JSON of a query, which monero-c
/// doesn't read nested deeper than 64 levels.
/// </summary>
internal static class MoneroQueryCopy
{
    /// <summary>The deepest chain of queries that a copy makes, as deep as the JSON of a query can nest.</summary>
    public const int MaxDepth = 64;

    /// <summary>Throw when a copy of a query went deeper than <see cref="MaxDepth"/>, which a query that holds itself would do forever.</summary>
    public static void CheckDepth(int depth)
    {
        if (depth >= MaxDepth) throw new MoneroError($"The query holds itself, or it is nested more than {MaxDepth} queries deep");
    }
}
