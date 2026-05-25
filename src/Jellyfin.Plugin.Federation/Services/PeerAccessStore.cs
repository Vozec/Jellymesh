using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Direct peer handshake (request access + invite).
///
/// Modes:
///   - 'request': we (or peer) asked for access; admin on the receiving side approves.
///   - 'invite' : we (or peer) pushed a pre-minted key; the receiver accepts.
///
/// Direction is from this server's POV: 'in' = somebody contacted us, 'out' = we contacted.
/// Status: 'pending', 'approved', 'denied', 'completed' (mutual handshake done), 'failed'.
/// </summary>
public class PeerAccessStore
{
    private readonly string _dbPath;
    private readonly ILogger<PeerAccessStore> _logger;

    public PeerAccessStore(IApplicationPaths appPaths, ILogger<PeerAccessStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "peer_access.db");
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
            CREATE TABLE IF NOT EXISTS peer_access_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                direction TEXT NOT NULL,
                mode TEXT NOT NULL,
                target_url TEXT NOT NULL,
                target_name TEXT,
                nonce TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                mutual INTEGER NOT NULL DEFAULT 0,
                our_key_id TEXT,
                their_api_key TEXT,
                message TEXT,
                created_utc TEXT NOT NULL,
                completed_utc TEXT,
                client_ip TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uniq_nonce ON peer_access_requests(nonce);
            CREATE INDEX IF NOT EXISTS idx_dir_status ON peer_access_requests(direction, status);
            CREATE TABLE IF NOT EXISTS access_request_rate (
                ip TEXT NOT NULL,
                bucket_hour TEXT NOT NULL,
                hits INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (ip, bucket_hour)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public static string GenerateNonce()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public long Insert(PeerAccessRow row)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO peer_access_requests
            (direction, mode, target_url, target_name, nonce, status, mutual, our_key_id, their_api_key, message, created_utc, client_ip)
            VALUES ($d,$m,$u,$n,$nc,$s,$mu,$ok,$tk,$msg,$cu,$ip)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$d", row.Direction);
        cmd.Parameters.AddWithValue("$m", row.Mode);
        cmd.Parameters.AddWithValue("$u", row.TargetUrl);
        cmd.Parameters.AddWithValue("$n", (object?)row.TargetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nc", row.Nonce);
        cmd.Parameters.AddWithValue("$s", row.Status);
        cmd.Parameters.AddWithValue("$mu", row.Mutual ? 1 : 0);
        cmd.Parameters.AddWithValue("$ok", (object?)row.OurKeyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tk", (object?)row.TheirApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", (object?)row.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cu", row.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$ip", (object?)row.ClientIp ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public PeerAccessRow? Get(long id)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM peer_access_requests WHERE id=$i LIMIT 1;";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapRow(r) : null;
    }

    public PeerAccessRow? GetByNonce(string nonce)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM peer_access_requests WHERE nonce=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", nonce);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapRow(r) : null;
    }

    public List<PeerAccessRow> List(string direction, string? status = null)
    {
        var rows = new List<PeerAccessRow>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = status is null
            ? "SELECT * FROM peer_access_requests WHERE direction=$d ORDER BY created_utc DESC;"
            : "SELECT * FROM peer_access_requests WHERE direction=$d AND status=$s ORDER BY created_utc DESC;";
        cmd.Parameters.AddWithValue("$d", direction);
        if (status is not null) cmd.Parameters.AddWithValue("$s", status);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(MapRow(r));
        return rows;
    }

    public bool UpdateStatus(long id, string status, string? ourKeyId = null, string? theirApiKey = null, bool completedNow = false)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE peer_access_requests
            SET status=$s,
                our_key_id=COALESCE($ok, our_key_id),
                their_api_key=COALESCE($tk, their_api_key),
                completed_utc=CASE WHEN $cu THEN $now ELSE completed_utc END
            WHERE id=$i;";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$ok", (object?)ourKeyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tk", (object?)theirApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cu", completedNow ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$i", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Records one hit for this IP in the current hour bucket and returns the
    /// total hits for that bucket. Caller compares against the configured cap.</summary>
    public long HitRateBucket(string ip)
    {
        var bucket = DateTime.UtcNow.ToString("yyyyMMddHH");
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO access_request_rate(ip, bucket_hour, hits) VALUES($ip, $b, 1)
            ON CONFLICT(ip, bucket_hour) DO UPDATE SET hits = hits + 1
            RETURNING hits;";
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.Parameters.AddWithValue("$b", bucket);
        return (long)cmd.ExecuteScalar()!;
    }

    private static PeerAccessRow MapRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Direction = r.GetString(r.GetOrdinal("direction")),
        Mode = r.GetString(r.GetOrdinal("mode")),
        TargetUrl = r.GetString(r.GetOrdinal("target_url")),
        TargetName = r.IsDBNull(r.GetOrdinal("target_name")) ? null : r.GetString(r.GetOrdinal("target_name")),
        Nonce = r.GetString(r.GetOrdinal("nonce")),
        Status = r.GetString(r.GetOrdinal("status")),
        Mutual = r.GetInt32(r.GetOrdinal("mutual")) != 0,
        OurKeyId = r.IsDBNull(r.GetOrdinal("our_key_id")) ? null : r.GetString(r.GetOrdinal("our_key_id")),
        TheirApiKey = r.IsDBNull(r.GetOrdinal("their_api_key")) ? null : r.GetString(r.GetOrdinal("their_api_key")),
        Message = r.IsDBNull(r.GetOrdinal("message")) ? null : r.GetString(r.GetOrdinal("message")),
        CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc"))).ToUniversalTime(),
        CompletedUtc = r.IsDBNull(r.GetOrdinal("completed_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("completed_utc"))).ToUniversalTime(),
        ClientIp = r.IsDBNull(r.GetOrdinal("client_ip")) ? null : r.GetString(r.GetOrdinal("client_ip"))
    };
}

public class PeerAccessRow
{
    public long Id { get; set; }
    public string Direction { get; set; } = string.Empty; // 'in' | 'out'
    public string Mode { get; set; } = string.Empty;      // 'request' | 'invite'
    public string TargetUrl { get; set; } = string.Empty;
    public string? TargetName { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";       // pending | approved | denied | completed | failed
    public bool Mutual { get; set; }
    public string? OurKeyId { get; set; }
    public string? TheirApiKey { get; set; }
    public string? Message { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public string? ClientIp { get; set; }
}
