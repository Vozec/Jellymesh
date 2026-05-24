using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Two-way "please add this film" wishlist between federated peers.
///
/// Inbound: peer POSTs to us → row stored with direction='in'. Admin sees
/// it in the config UI, accepts / declines / dismisses.
/// Outbound: admin asks us to send → row stored with direction='out' + we
/// POST to peer. Their plugin stores it on their side.
///
/// Status values: 'pending', 'accepted', 'declined', 'dismissed'.
/// </summary>
public class RequestStore
{
    private readonly string _dbPath;
    private readonly ILogger<RequestStore> _logger;

    public RequestStore(IApplicationPaths appPaths, ILogger<RequestStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "requests.db");
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
            CREATE TABLE IF NOT EXISTS federation_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                direction TEXT NOT NULL,
                peer_id TEXT,
                peer_url TEXT,
                tmdb_id TEXT,
                imdb_id TEXT,
                title TEXT,
                year INTEGER,
                note TEXT,
                requested_utc TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                last_status_change_utc TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_req_dir ON federation_requests(direction);
            CREATE INDEX IF NOT EXISTS idx_req_status ON federation_requests(status);
            -- prevent the same peer from spamming us with the same item twice while pending.
            -- COALESCE wraps nullable identity columns because SQLite treats NULLs as distinct
            -- in UNIQUE indexes — without it, (peer=NULL, tmdb=NULL, imdb=NULL, title=NULL)
            -- is treated as distinct from itself and duplicates leak through. Title is in the
            -- key so title-only requests (no TMDB/IMDB ids) about different films don't all
            -- collapse to one slot per peer.
            CREATE UNIQUE INDEX IF NOT EXISTS uniq_inbound_pending
                ON federation_requests(direction,
                    COALESCE(peer_url, ''),
                    COALESCE(tmdb_id, ''),
                    COALESCE(imdb_id, ''),
                    COALESCE(title, ''))
                WHERE status = 'pending';
        ";
        cmd.ExecuteNonQuery();
    }

    public long? Insert(FederationRequest req)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO federation_requests
            (direction, peer_id, peer_url, tmdb_id, imdb_id, title, year, note, requested_utc, status, last_status_change_utc)
            VALUES ($d, $pid, $purl, $tmdb, $imdb, $t, $y, $n, $r, $st, $r)
            ON CONFLICT DO NOTHING
            RETURNING id;";
        cmd.Parameters.AddWithValue("$d", req.Direction);
        cmd.Parameters.AddWithValue("$pid", (object?)req.PeerId?.ToString() ?? DBNull.Value);
        // Normalize peer_url so trailing-slash / case differences don't bypass the unique index.
        var normalizedUrl = string.IsNullOrEmpty(req.PeerUrl) ? null : req.PeerUrl.TrimEnd('/').ToLowerInvariant();
        cmd.Parameters.AddWithValue("$purl", (object?)normalizedUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tmdb", (object?)req.TmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imdb", (object?)req.ImdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)req.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$y", (object?)req.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", (object?)req.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", DateTime.UtcNow.ToString("O"));
        // Honor req.Status — outbound rows can be inserted as 'send-failed' so admin sees
        // delivery failures. Default 'pending' set in the FederationRequest record.
        cmd.Parameters.AddWithValue("$st", req.Status);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : (long)r;
    }

    public IReadOnlyList<FederationRequest> List(string direction, string? status = null, int limit = 200)
    {
        var rows = new List<FederationRequest>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        // Secondary ORDER BY id ensures total order under same-tick timestamps (Windows
        // DateTime.UtcNow resolution can be ~15 ms — multiple Inserts share the same string).
        cmd.CommandText = status is null
            ? "SELECT id, direction, peer_id, peer_url, tmdb_id, imdb_id, title, year, note, requested_utc, status, last_status_change_utc FROM federation_requests WHERE direction = $d ORDER BY requested_utc DESC, id DESC LIMIT $l;"
            : "SELECT id, direction, peer_id, peer_url, tmdb_id, imdb_id, title, year, note, requested_utc, status, last_status_change_utc FROM federation_requests WHERE direction = $d AND status = $s ORDER BY requested_utc DESC, id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$d", direction);
        cmd.Parameters.AddWithValue("$l", limit);
        if (status is not null) cmd.Parameters.AddWithValue("$s", status);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new FederationRequest
            {
                Id = r.GetInt64(0),
                Direction = r.GetString(1),
                PeerId = r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)),
                PeerUrl = r.IsDBNull(3) ? null : r.GetString(3),
                TmdbId = r.IsDBNull(4) ? null : r.GetString(4),
                ImdbId = r.IsDBNull(5) ? null : r.GetString(5),
                Title = r.IsDBNull(6) ? null : r.GetString(6),
                Year = r.IsDBNull(7) ? null : r.GetInt32(7),
                Note = r.IsDBNull(8) ? null : r.GetString(8),
                RequestedUtc = DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Status = r.GetString(10),
                LastStatusChangeUtc = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }
        return rows;
    }

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    { "pending", "accepted", "declined", "dismissed", "send-failed" };

    public bool UpdateStatus(long id, string newStatus)
    {
        // Whitelist enforced at the store layer too — every caller routes through here, so any
        // future internal caller (sync task, migration, test) can't poison the column.
        if (!ValidStatuses.Contains(newStatus))
            throw new ArgumentException($"Unknown status '{newStatus}'", nameof(newStatus));

        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE federation_requests SET status = $s, last_status_change_utc = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$s", newStatus);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public static bool IsValidStatus(string status) => ValidStatuses.Contains(status);
}

public class FederationRequest
{
    public long Id { get; set; }
    public string Direction { get; set; } = "in"; // "in" or "out"
    public Guid? PeerId { get; set; }
    public string? PeerUrl { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? Note { get; set; }
    public DateTime RequestedUtc { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime? LastStatusChangeUtc { get; set; }
}
