using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationStatsServiceTests
{
    [Theory]
    [InlineData(0, 0, 0.0)]      // empty cache
    [InlineData(0, 5, 0.0)]      // bogus inputs: distinct > 0 but no rows → 0
    [InlineData(100, 100, 0.0)]  // every item unique across peers
    [InlineData(100, 50, 0.5)]   // half are dups: 50 distinct items across 100 rows
    [InlineData(100, 25, 0.75)]  // each item on average appears 4x
    [InlineData(10, 1, 0.9)]     // 1 unique item, 10 copies
    public void ComputeDedupRatio_typical_ratios(int rows, int distinct, double expected)
    {
        Assert.Equal(expected, FederationStatsService.ComputeDedupRatio(rows, distinct));
    }

    [Fact]
    public void Distinct_zero_when_rows_present_is_clamped_to_full_dup()
    {
        // SQL invariant should make this impossible (COUNT DISTINCT > 0 when rows present),
        // but defensive against a corrupted store: clamp to 1.0 instead of returning 1.0
        // via division by zero / NaN.
        Assert.Equal(1.0, FederationStatsService.ComputeDedupRatio(50, 0));
    }

    [Fact]
    public void Distinct_greater_than_rows_returns_zero()
    {
        // SQL impossibility too — but if somehow distinct=200 over rows=100, treat as
        // "ratio undefined" and clamp to 0 (no overlap detected).
        Assert.Equal(0.0, FederationStatsService.ComputeDedupRatio(100, 200));
    }

    [Fact]
    public void Result_is_rounded_to_four_decimal_places()
    {
        // 1 - 333/1000 = 0.667 → rounds to 0.667 (exact at 3dp, fine at 4dp)
        Assert.Equal(0.667, FederationStatsService.ComputeDedupRatio(1000, 333));
        // 1 - 1/3 = 0.6666… → rounds to 0.6667
        Assert.Equal(0.6667, FederationStatsService.ComputeDedupRatio(3, 1));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-100, -50)]
    public void Negative_inputs_treated_as_empty_cache(int rows, int distinct)
    {
        Assert.Equal(0.0, FederationStatsService.ComputeDedupRatio(rows, distinct));
    }
}
