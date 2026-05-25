using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// In-memory per-peer sync progress so the UI can render a progress bar per host while
/// FederationSyncTask is running. Tracks the current phase and percentage estimate per peer.
/// Entries older than a few minutes self-expire so a stuck round doesn't pin a UI badge.
/// </summary>
public class SyncProgressTracker
{
    public enum Phase { Pending, Pinging, Pulling, Saving, Done, Failed, Skipped }

    public class PeerStatus
    {
        public Guid PeerId { get; set; }
        public string PeerName { get; set; } = string.Empty;
        public Phase Phase { get; set; }
        public int Percent { get; set; }
        public int ItemsSeen { get; set; }
        public int ItemsTotal { get; set; }
        public string? Detail { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, PeerStatus> _peers = new();

    public DateTime? RunStartedUtc { get; private set; }
    public DateTime? RunCompletedUtc { get; private set; }

    public void BeginRound(IEnumerable<(Guid Id, string Name)> peers)
    {
        _peers.Clear();
        RunStartedUtc = DateTime.UtcNow;
        RunCompletedUtc = null;
        foreach (var p in peers)
        {
            _peers[p.Id] = new PeerStatus
            {
                PeerId = p.Id,
                PeerName = p.Name,
                Phase = Phase.Pending,
                Percent = 0,
                StartedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }
    }

    public void Update(Guid peerId, Phase phase, int percent, int seen = 0, int total = 0, string? detail = null)
    {
        var s = _peers.GetOrAdd(peerId, _ => new PeerStatus { PeerId = peerId, StartedUtc = DateTime.UtcNow });
        s.Phase = phase;
        s.Percent = Math.Clamp(percent, 0, 100);
        if (seen > 0) s.ItemsSeen = seen;
        if (total > 0) s.ItemsTotal = total;
        if (detail is not null) s.Detail = detail;
        s.UpdatedUtc = DateTime.UtcNow;
    }

    public void CompleteRound()
    {
        RunCompletedUtc = DateTime.UtcNow;
    }

    public List<PeerStatus> Snapshot()
    {
        // Drop entries older than 5 minutes once the run completed, otherwise the UI keeps
        // showing 'Done' forever and the next run can't reset cleanly.
        if (RunCompletedUtc.HasValue && DateTime.UtcNow - RunCompletedUtc.Value > TimeSpan.FromMinutes(5))
        {
            _peers.Clear();
            RunStartedUtc = null;
            RunCompletedUtc = null;
            return new List<PeerStatus>();
        }
        return _peers.Values.OrderBy(p => p.PeerName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool IsRunning => RunStartedUtc.HasValue && !RunCompletedUtc.HasValue;
}
