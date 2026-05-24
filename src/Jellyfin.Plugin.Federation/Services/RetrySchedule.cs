using System;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Exponential backoff schedule for the push-invalidation retry loop. Pure static so it's
/// testable independent of timers / HTTP / DI.
/// </summary>
public static class RetrySchedule
{
    public const int MaxAttempts = 5;

    // 30s → 60s → 120s → 240s → 480s. Anything beyond MaxAttempts returns null = give up.
    private static readonly int[] Steps = { 30, 60, 120, 240, 480 };

    /// <summary>
    /// Returns the wait time before the Nth attempt (1-indexed: attemptNumber=1 is the first
    /// retry after the initial fire, NOT the initial fire itself). Returns null when
    /// attemptNumber > MaxAttempts - caller should give up and let gossip-pull handle it.
    /// </summary>
    public static TimeSpan? NextDelay(int attemptNumber)
    {
        if (attemptNumber < 1 || attemptNumber > MaxAttempts) return null;
        return TimeSpan.FromSeconds(Steps[attemptNumber - 1]);
    }
}
