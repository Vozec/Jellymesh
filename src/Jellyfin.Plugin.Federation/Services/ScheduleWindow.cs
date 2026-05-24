using System;

namespace Jellyfin.Plugin.Federation.Services;

public static class ScheduleWindow
{
    /// <summary>
    /// Returns true when <paramref name="now"/> falls inside [start, end). Both same-day and
    /// cross-midnight windows are supported (start &gt; end means the window wraps midnight).
    /// Either bound being null/unparseable disables the check (always true).
    /// </summary>
    public static bool IsWithin(string? startHHmm, string? endHHmm, TimeSpan now)
    {
        if (string.IsNullOrEmpty(startHHmm) || string.IsNullOrEmpty(endHHmm)) return true;
        if (!TimeSpan.TryParse(startHHmm, out var start)) return true;
        if (!TimeSpan.TryParse(endHHmm, out var end)) return true;
        if (start == end) return false; // explicit zero-length window: never allowed
        return start < end
            ? now >= start && now < end          // same-day:   09:00 → 17:00
            : now >= start || now < end;         // wraps mid:  22:00 → 06:00
    }
}
