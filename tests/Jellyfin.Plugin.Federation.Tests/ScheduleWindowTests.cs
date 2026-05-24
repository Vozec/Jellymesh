using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class ScheduleWindowTests
{
    [Theory]
    [InlineData(null, "17:00", 12)]      // missing start → open
    [InlineData("09:00", null, 12)]      // missing end → open
    [InlineData("", "", 12)]             // both empty → open
    [InlineData("not-a-time", "17:00", 12)] // unparseable → open (fail-open, doesn't lock admin out)
    public void Missing_or_invalid_bounds_always_allow(string? start, string? end, int hour)
    {
        Assert.True(ScheduleWindow.IsWithin(start, end, TimeSpan.FromHours(hour)));
    }

    [Theory]
    [InlineData("09:00", "17:00", 9, true)]   // boundary start: inclusive
    [InlineData("09:00", "17:00", 8, false)]  // before
    [InlineData("09:00", "17:00", 12, true)]  // mid
    [InlineData("09:00", "17:00", 17, false)] // boundary end: exclusive
    [InlineData("09:00", "17:00", 23, false)] // after
    public void Same_day_window(string start, string end, int hour, bool expected)
    {
        Assert.Equal(expected, ScheduleWindow.IsWithin(start, end, TimeSpan.FromHours(hour)));
    }

    [Theory]
    [InlineData("22:00", "06:00", 23, true)]  // evening side
    [InlineData("22:00", "06:00", 2, true)]   // early morning side
    [InlineData("22:00", "06:00", 6, false)]  // boundary end exclusive
    [InlineData("22:00", "06:00", 10, false)] // day gap
    [InlineData("22:00", "06:00", 22, true)]  // boundary start inclusive
    public void Cross_midnight_window(string start, string end, int hour, bool expected)
    {
        Assert.Equal(expected, ScheduleWindow.IsWithin(start, end, TimeSpan.FromHours(hour)));
    }

    [Fact]
    public void Zero_length_window_is_never_allowed()
    {
        // start == end is ambiguous (all day or never?). Pick "never" - admin who sets
        // 12:00–12:00 clearly didn't mean a 24h window (use null for that).
        Assert.False(ScheduleWindow.IsWithin("12:00", "12:00", TimeSpan.FromHours(12)));
        Assert.False(ScheduleWindow.IsWithin("12:00", "12:00", TimeSpan.FromHours(0)));
    }
}
