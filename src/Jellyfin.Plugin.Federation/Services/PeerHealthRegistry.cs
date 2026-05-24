using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services;

public class PeerHealthRegistry
{
    private readonly ConcurrentDictionary<Guid, PeerHealth> _state = new();

    public void Update(Guid peerId, bool online, TimeSpan rtt)
    {
        _state[peerId] = new PeerHealth
        {
            Online = online,
            LastCheckUtc = DateTime.UtcNow,
            LastRttMs = (int)rtt.TotalMilliseconds
        };
    }

    public PeerHealth Get(Guid peerId)
        => _state.TryGetValue(peerId, out var h) ? h : new PeerHealth { Online = false, LastCheckUtc = DateTime.MinValue };

    public bool IsOnline(Guid peerId) => Get(peerId).Online;
}

public class PeerHealth
{
    public bool Online { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public int LastRttMs { get; set; }
}
