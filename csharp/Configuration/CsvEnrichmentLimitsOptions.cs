using System.Collections.Frozen;

namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Finite, startup-validated limits for CSV enrichment requests (P05 Slice 1).
///
/// Only genuine product/resource choices and deployment facts are settable; every
/// mechanical consequence is computed once from them. Zero never means unlimited.
/// The independent values are the D1 caps the repository owner approved on
/// 2026-07-24 (see <c>.agents/plans/P05-slice0-capacity-evidence.md</c>). The batch
/// count is deliberately absent: its selection is gated on the live-AD timing matrix
/// (<c>.agents/plans/P05-csv-scale-limits.md</c>) and is not a checked-in default.
/// </summary>
public sealed class CsvEnrichmentLimitsOptions
{
    public const string SectionName = "CsvEnrichment:Limits";

    /// <summary>
    /// Distinct-match count at which a lookup is classified <c>Ambiguous</c>. Two
    /// matches are sufficient to decide ambiguity; a semantic constant, not a knob.
    /// </summary>
    public const int AmbiguityThreshold = 2;

    /// <summary>
    /// The five CSV match attributes and their discovered-schema value-length
    /// ceilings, in UTF-16 code units. P04's broader user-attribute allow-list is
    /// the retrieval/filter authorization boundary; this map is the match-key
    /// boundary. Keyed case-insensitively because the LLM plan may vary casing.
    /// </summary>
    public static readonly FrozenDictionary<string, int> MatchAttributeSchemaLengths =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = 256,
            ["userPrincipalName"] = 1024,
            ["mail"] = 256,
            ["displayName"] = 256,
            ["employeeID"] = 16,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum data rows, excluding the header row. D1: owner requirement.</summary>
    public int MaxDataRows { get; set; } = 100_000;

    /// <summary>Maximum input columns (canonical headers). D1: 64.</summary>
    public int MaxColumns { get; set; } = 64;

    /// <summary>Maximum retrieval attributes the generated plan may request. D1: 16.</summary>
    public int MaxRetrieveAttributes { get; set; } = 16;

    /// <summary>
    /// Maximum UTF-16 code units in any single supplied cell or header. D1: 1,024.
    /// Bounds one pathological cell; the aggregate size guard is <see cref="MaxRequestBodyBytes"/>.
    /// </summary>
    public int MaxFieldCodeUnits { get; set; } = 1_024;

    /// <summary>
    /// Authoritative application request-body ceiling in bytes for the active JSON
    /// transport. D1: 96 MiB, above the measured worst-case JSON body (90,700,157 B)
    /// for the approved 100k × 64 profile. Sets the Kestrel/IIS and <c>web.config</c>
    /// ceilings in Slice 2.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 100_663_296;

    /// <summary>
    /// Minimum verified LDAP receive-buffer ceiling in bytes across every selectable
    /// domain controller. Deployment fact; the one inspected policy reported
    /// 10,485,760 B. Deployment must supply and verify the minimum effective value.
    /// </summary>
    public long LdapReceiveCeilingBytes { get; set; } = 10_485_760;

    /// <summary>
    /// Maximum output rows: at most one output row per input row (reconstruction
    /// invariant). Derived, never configured.
    /// </summary>
    public int OutputRowLimit => MaxDataRows;

    /// <summary>
    /// Total rectangular input-grid cells (<see cref="MaxDataRows"/> ×
    /// <see cref="MaxColumns"/>) using checked arithmetic. Derived, never configured.
    /// </summary>
    public long MaxGridCells => checked((long)MaxDataRows * MaxColumns);
}
