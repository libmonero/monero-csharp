using System.Text.Json.Serialization;
using Monero.Common;

namespace Monero.Wallet.Model;

/// <summary>
/// Configures a query for the outputs of a wallet: all of them are returned, except the ones that don't meet the criteria that are set.
/// <para>
/// The JSON is the one that monero-c reads. The query of the transaction of the outputs is in <see cref="TxQuery"/>, as <c>txQuery</c>, and
/// it must not be a transaction query that has this query as one of its queries of outputs, since each would write the other.
/// </para>
/// </summary>
public class MoneroOutputQuery : MoneroOutputWallet
{
    /// <summary>
    /// Initialize a new output query, which matches every output.
    /// </summary>
    public MoneroOutputQuery()
    {
        // nothing to build
    }

    /// <summary>
    /// Construct this query as a deep copy of the given query. The query of the transaction is copied too.
    /// </summary>
    /// <param name="query">the query to copy.</param>
    /// <exception cref="MoneroError">the query holds itself, through the query of its transactions.</exception>
    public MoneroOutputQuery(MoneroOutputQuery query) : this(query, 0)
    {
    }

    private MoneroOutputQuery(MoneroOutputQuery query, int depth) : base(query)
    {
        MoneroQueryCopy.CheckDepth(depth);

        MinAmount = query.MinAmount;
        MaxAmount = query.MaxAmount;

        if (query.SubaddressIndices != null)
        {
            SubaddressIndices = [.. query.SubaddressIndices];
        }

        TxQuery = query.TxQuery?.Copy(depth + 1);
    }

    /// <summary>
    /// Copy this query.
    /// </summary>
    /// <returns>a deep copy of the query, with no transaction.</returns>
    /// <exception cref="MoneroError">the query holds itself, through the query of its transactions.</exception>
    public override MoneroOutputQuery Copy()
    {
        return new MoneroOutputQuery(this, 0);
    }

    /// <summary>Copy this query, as one of the queries of another one that is being copied at the given depth.</summary>
    internal MoneroOutputQuery Copy(int depth)
    {
        return new MoneroOutputQuery(this, depth);
    }

    /// <summary>
    /// Indices of the subaddresses that have the outputs to match.
    /// </summary>
    [JsonPropertyName("subaddressIndices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<uint>? SubaddressIndices { get; set; }

    /// <summary>
    /// Minimum amount of the outputs to match, in atomic units.
    /// </summary>
    [JsonPropertyName("minAmount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MinAmount { get; set; }

    /// <summary>
    /// Maximum amount of the outputs to match, in atomic units.
    /// </summary>
    [JsonPropertyName("maxAmount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? MaxAmount { get; set; }

    /// <summary>
    /// Query that the transactions of the outputs have to meet.
    /// </summary>
    [JsonPropertyName("txQuery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoneroTxQuery? TxQuery { get; set; }
}
