using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Persisted health samples per peer for uptime + latency stats over rolling windows.
/// One row per health probe (HealthMonitorService cadence). Old rows pruned by retention.
/// </summary>
public class PeerHealthHistoryStore
{
    private readonly string _dbPath;
    private readonly ILogger<PeerHealthHistoryStore> _logger;

    public PeerHealthHistoryStore(IApplicationPaths appPaths, ILogger<PeerHealthHistoryStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "peer_health.db");
        InitSchema();
    }

    private string ConnString => $"Data Source={_dbPath};Default Timeout=10";

    private void InitSchema()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS peer_health_samples (
                peer_id TEXT NOT NULL,
                ts TEXT NOT NULL,
                online INTEGER NOT NULL,
                rtt_ms INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_health_peer_ts ON peer_health_samples(peer_id, ts);
        ";
        cmd.ExecuteNonQuery();
    }

    public void Append(Guid peerId, bool online, int? rttMs)
    {
        try
        {
            using var c = new SqliteConnection(ConnString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO peer_health_samples(peer_id, ts, online, rtt_ms) VALUES($p, $t, $o, $r);";
            cmd.Parameters.AddWithValue("$p", peerId.ToString());
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$o", online ? 1 : 0);
            cmd.Parameters.AddWithValue("$r", rttMs.HasValue ? (object)rttMs.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Health sample insert failed"); }
    }

    public PeerHealthStats Stats(Guid peerId, TimeSpan window)
    {
        var since = DateTime.UtcNow.Subtract(window).ToString("O");
        using var c = new SqliteConnection(ConnString);
        c.Open();

        long total, up;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(online), 0) FROM peer_health_samples WHERE peer_id=$p AND ts>=$s;";
            cmd.Parameters.AddWithValue("$p", peerId.ToString());
            cmd.Parameters.AddWithValue("$s", since);
            using var r = cmd.ExecuteReader();
            r.Read();
            total = r.GetInt64(0);
            up = r.GetInt64(1);
        }

        var rtts = new List<int>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT rtt_ms FROM peer_health_samples WHERE peer_id=$p AND ts>=$s AND rtt_ms IS NOT NULL ORDER BY rtt_ms;";
            cmd.Parameters.AddWithValue("$p", peerId.ToString());
            cmd.Parameters.AddWithValue("$s", since);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rtts.Add(r.GetInt32(0));
        }

        return new PeerHealthStats
        {
            WindowHours = (int)window.TotalHours,
            SampleCount = total,
            UpCount = up,
            UptimePercent = total > 0 ? Math.Round(100.0 * up / total, 2) : 0,
            P50RttMs = Percentile(rtts, 0.50),
            P95RttMs = Percentile(rtts, 0.95)
        };
    }

    public List<HealthSample> Samples(Guid peerId, TimeSpan window, int maxRows = 500)
    {
        var since = DateTime.UtcNow.Subtract(window).ToString("O");
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, online, rtt_ms FROM peer_health_samples WHERE peer_id=$p AND ts>=$s ORDER BY ts ASC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$p", peerId.ToString());
        cmd.Parameters.AddWithValue("$s", since);
        cmd.Parameters.AddWithValue("$lim", maxRows);
        var rows = new List<HealthSample>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new HealthSample
            {
                Ts = DateTime.Parse(r.GetString(0)).ToUniversalTime(),
                Online = r.GetInt32(1) != 0,
                RttMs = r.IsDBNull(2) ? null : r.GetInt32(2)
            });
        }
        return rows;
    }

    public int Purge(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow.Subtract(olderThan).ToString("O");
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM peer_health_samples WHERE ts<$cu;";
        cmd.Parameters.AddWithValue("$cu", cutoff);
        return cmd.ExecuteNonQuery();
    }

    private static int? Percentile(List<int> sorted, double q)
    {
        if (sorted.Count == 0) return null;
        var idx = Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1);
        return sorted[Math.Max(0, idx)];
    }
}

public class HealthSample
{
    public DateTime Ts { get; set; }
    public bool Online { get; set; }
    public int? RttMs { get; set; }
}

public class PeerHealthStats
{
    public int WindowHours { get; set; }
    public long SampleCount { get; set; }
    public long UpCount { get; set; }
    public double UptimePercent { get; set; }
    public int? P50RttMs { get; set; }
    public int? P95RttMs { get; set; }
}
