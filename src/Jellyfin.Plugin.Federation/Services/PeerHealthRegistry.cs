using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services;

public class PeerHealthRegistry
{
    private readonly ConcurrentDictionary<Guid, PeerHealth> _state = new();
    private long _signatureCounter;

    public event EventHandler<PeerHealthChangedEventArgs>? HealthChanged;

    public void Update(Guid peerId, bool online, TimeSpan rtt)
    {
        var prev = _state.TryGetValue(peerId, out var existing) ? existing.Online : (bool?)null;

        _state[peerId] = new PeerHealth
        {
            Online = online,
            LastCheckUtc = DateTime.UtcNow,
            LastRttMs = (int)rtt.TotalMilliseconds
        };

        if (prev != online)
        {
            System.Threading.Interlocked.Increment(ref _signatureCounter);
            HealthChanged?.Invoke(this, new PeerHealthChangedEventArgs(peerId, online));
        }
    }

    public PeerHealth Get(Guid peerId)
        => _state.TryGetValue(peerId, out var h) ? h : new PeerHealth { Online = false, LastCheckUtc = DateTime.MinValue };

    /// <summary>
    /// True when the peer's last probe succeeded, OR when we've never probed it yet
    /// (optimistic - first probe failure flips this). Avoids hiding federated sources
    /// during the 0-to-30-second window after server start before HealthMonitorService's
    /// first round completes.
    /// </summary>
    public bool IsOnline(Guid peerId)
        => _state.TryGetValue(peerId, out var h) ? h.Online : true;

    /// <summary>Short string that changes whenever any peer flips online state. Embed in cache keys.</summary>
    public string Signature()
    {
        var pairs = _state.OrderBy(k => k.Key)
            .Select(kvp => $"{kvp.Key:N}:{(kvp.Value.Online ? 1 : 0)}")
            .ToArray();
        var joined = string.Join("|", pairs) + "#" + _signatureCounter.ToString(CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 6);
    }
}

public class PeerHealthChangedEventArgs : EventArgs
{
    public PeerHealthChangedEventArgs(Guid peerId, bool online) { PeerId = peerId; Online = online; }
    public Guid PeerId { get; }
    public bool Online { get; }
}

public class PeerHealth
{
    public bool Online { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public int LastRttMs { get; set; }
}
