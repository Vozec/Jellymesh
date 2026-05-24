using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RetryScheduleTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void NextDelay_returns_expected_step(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetrySchedule.NextDelay(attempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public void NextDelay_returns_null_outside_range(int attempt)
    {
        Assert.Null(RetrySchedule.NextDelay(attempt));
    }

    [Fact]
    public void Backoff_grows_strictly_monotonic()
    {
        TimeSpan? prev = null;
        for (var i = 1; i <= RetrySchedule.MaxAttempts; i++)
        {
            var cur = RetrySchedule.NextDelay(i);
            Assert.NotNull(cur);
            if (prev is not null) Assert.True(cur > prev, $"attempt {i} not larger than previous");
            prev = cur;
        }
    }

    [Fact]
    public void MaxAttempts_matches_schedule_length()
    {
        // If someone adds a step without bumping MaxAttempts (or vice versa), this catches it.
        Assert.Null(RetrySchedule.NextDelay(RetrySchedule.MaxAttempts + 1));
        Assert.NotNull(RetrySchedule.NextDelay(RetrySchedule.MaxAttempts));
    }
}
