namespace AdQuery.Orchestrator.Models;

/// <summary>
/// A handful of deterministic scalars describing a grouped result's shape (F04 Slice 2,
/// f04-or-3). <see cref="HeadlineResult"/> keeps only the ten largest buckets, so it
/// cannot distinguish a near-unique distribution from a concentrated one: the
/// extensionAttribute1 case (26,612 of ~27k values appearing once) looks identical to a
/// clean top-ten unless these counts travel with it.
///
/// Every field is an integer derived from the settled aggregation. No directory value is
/// added beyond the buckets the headline already carries, so the DATA-D1 bound is unchanged.
/// </summary>
public sealed class DistributionSummary
{
    /// <summary>Total matching rows — the sum over every bucket, not only the top ten.</summary>
    public int TotalRows { get; init; }

    /// <summary>Number of distinct buckets in the whole distribution.</summary>
    public int DistinctBuckets { get; init; }

    /// <summary>Buckets holding exactly one row. Near-uniqueness is this over <see cref="DistinctBuckets"/>.</summary>
    public int SingletonBuckets { get; init; }

    /// <summary>Rows whose grouped value was absent or blank (the <c>(empty)</c> bucket).</summary>
    public int BlankRows { get; init; }
}
