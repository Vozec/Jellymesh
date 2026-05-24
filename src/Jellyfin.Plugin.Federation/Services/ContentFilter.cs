using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Pure decision logic for share-key content filtering: blocked tags + parental rating
/// cap. Extracted from LocalCatalogDigest so the policy is testable independent of
/// ILibraryManager / BaseItem.
/// </summary>
public static class ContentFilter
{
    public static bool Passes(IReadOnlyCollection<string>? itemTags, string? itemOfficialRating,
        IReadOnlyCollection<string>? blockedTags, string? maxRating, bool strictUnknown)
    {
        if (blockedTags is { Count: > 0 } && itemTags is { Count: > 0 })
        {
            if (itemTags.Any(t => blockedTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        if (string.IsNullOrEmpty(maxRating)) return true;

        if (string.IsNullOrEmpty(itemOfficialRating))
            return !strictUnknown;

        var itemScore = ParentalScore(itemOfficialRating);
        var maxScore = ParentalScore(maxRating);
        if (itemScore is null) return !strictUnknown;
        if (maxScore is null) return true; // max unparseable → fail-open (admin typo shouldn't lock peers out)
        return itemScore.Value <= maxScore.Value;
    }

    public static int? ParentalScore(string rating) => rating?.ToUpperInvariant() switch
    {
        // Conservative cross-system mapping. Sufficient for "kid-safe" / "adult-only" splits;
        // not a substitute for Jellyfin's per-user content-rating settings.
        "G" or "TV-Y" or "TV-G" or "U" => 1,
        "PG" or "TV-Y7" or "TV-PG" => 5,
        "PG-13" or "TV-13" or "TV-14" or "12" => 13,
        "R" or "TV-MA" or "16" or "17" => 17,
        "NC-17" or "X" or "18" or "AO" => 18,
        _ => null
    };
}
