using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Records every unsolicited inbound contact (request, invite, intro, stream, digest) with
/// the outcome (accepted, denied, blocked, throttled). Lets the admin see at a glance who
/// has been trying to reach the server. Quota counts are derived from this table.
/// </summary>
public class InboundAuditStore
{
    private readonly string _dbPath;
    private readonly ILogger<InboundAuditStore> _logger;

    public InboundAuditStore(IApplicationPaths appPaths, ILogger<InboundAuditStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "inbound_audit.db");
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
            CREATE TABLE IF NOT EXISTS inbound_attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                ip TEXT,
                peer_url TEXT,
                peer_id TEXT,
                mode TEXT NOT NULL,
                outcome TEXT NOT NULL,
                bytes INTEGER NOT NULL DEFAULT 0,
                reason TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_inbound_ts ON inbound_attempts(ts);
            CREATE INDEX IF NOT EXISTS idx_inbound_peer ON inbound_attempts(peer_id, ts);
            CREATE INDEX IF NOT EXISTS idx_inbound_mode ON inbound_attempts(mode, ts);
        ";
        cmd.ExecuteNonQuery();
    }

    public void Record(string mode, string outcome, string? ip = null, string? peerUrl = null,
        Guid? peerId = null, long bytes = 0, string? reason = null)
    {
        try
        {
            using var c = new SqliteConnection(ConnString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO inbound_attempts(ts, ip, peer_url, peer_id, mode, outcome, bytes, reason)
                VALUES($ts, $ip, $url, $pid, $m, $o, $b, $r);";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)peerUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pid", peerId.HasValue ? peerId.Value.ToString() : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$m", mode);
            cmd.Parameters.AddWithValue("$o", outcome);
            cmd.Parameters.AddWithValue("$b", bytes);
            cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audit insert failed");
        }
    }

    /// <summary>Count rows for a peer in the last <paramref name="windowHours"/> hours.</summary>
    public long CountForPeer(Guid peerId, int windowHours)
    {
        var since = DateTime.UtcNow.AddHours(-windowHours).ToString("O");
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM inbound_attempts WHERE peer_id=$pid AND ts>=$since;";
        cmd.Parameters.AddWithValue("$pid", peerId.ToString());
        cmd.Parameters.AddWithValue("$since", since);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Sum bytes served for a peer in the last <paramref name="windowHours"/> hours.</summary>
    public long BytesForPeer(Guid peerId, int windowHours)
    {
        var since = DateTime.UtcNow.AddHours(-windowHours).ToString("O");
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM inbound_attempts WHERE peer_id=$pid AND ts>=$since;";
        cmd.Parameters.AddWithValue("$pid", peerId.ToString());
        cmd.Parameters.AddWithValue("$since", since);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public List<InboundAuditRow> List(int limit = 200, string? mode = null, string? outcome = null)
    {
        var rows = new List<InboundAuditRow>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        var where = "";
        if (mode is not null) where += " AND mode=$m";
        if (outcome is not null) where += " AND outcome=$o";
        cmd.CommandText = $"SELECT id, ts, ip, peer_url, peer_id, mode, outcome, bytes, reason FROM inbound_attempts WHERE 1=1{where} ORDER BY id DESC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$lim", limit);
        if (mode is not null) cmd.Parameters.AddWithValue("$m", mode);
        if (outcome is not null) cmd.Parameters.AddWithValue("$o", outcome);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new InboundAuditRow
            {
                Id = r.GetInt64(0),
                Ts = DateTime.Parse(r.GetString(1)).ToUniversalTime(),
                Ip = r.IsDBNull(2) ? null : r.GetString(2),
                PeerUrl = r.IsDBNull(3) ? null : r.GetString(3),
                PeerId = r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)),
                Mode = r.GetString(5),
                Outcome = r.GetString(6),
                Bytes = r.GetInt64(7),
                Reason = r.IsDBNull(8) ? null : r.GetString(8)
            });
        }
        return rows;
    }

    /// <summary>Drop rows older than the given age.</summary>
    public int Purge(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow.Subtract(olderThan).ToString("O");
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM inbound_attempts WHERE ts<$cu;";
        cmd.Parameters.AddWithValue("$cu", cutoff);
        return cmd.ExecuteNonQuery();
    }
}

public class InboundAuditRow
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public string? Ip { get; set; }
    public string? PeerUrl { get; set; }
    public Guid? PeerId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string? Reason { get; set; }
}
