using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

public class RemoteItemStore
{
    private readonly string _dbPath;
    private readonly ILogger<RemoteItemStore> _logger;

    public RemoteItemStore(IApplicationPaths appPaths, ILogger<RemoteItemStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "remote_items.db");
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
            CREATE TABLE IF NOT EXISTS remote_items (
                server_id TEXT NOT NULL,
                remote_item_id TEXT NOT NULL,
                type TEXT,
                name TEXT,
                year INTEGER,
                tmdb TEXT,
                imdb TEXT,
                tvdb TEXT,
                provider_ids_json TEXT,
                runtime_ticks INTEGER,
                width INTEGER,
                height INTEGER,
                container TEXT,
                bitrate INTEGER,
                media_source_json TEXT,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (server_id, remote_item_id)
            );
            CREATE INDEX IF NOT EXISTS idx_tmdb ON remote_items(tmdb);
            CREATE INDEX IF NOT EXISTS idx_imdb ON remote_items(imdb);
            CREATE INDEX IF NOT EXISTS idx_name_year ON remote_items(name, year);

            CREATE TABLE IF NOT EXISTS stream_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                peer_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                ended_utc TEXT,
                bytes_served INTEGER NOT NULL DEFAULT 0,
                user_id TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_audit_peer ON stream_audit(peer_id);
            CREATE INDEX IF NOT EXISTS idx_audit_time ON stream_audit(started_utc);

            CREATE TABLE IF NOT EXISTS peer_digests (
                peer_id TEXT PRIMARY KEY,
                item_count INTEGER NOT NULL,
                catalog_hash TEXT NOT NULL,
                latest_modified_utc TEXT,
                last_synced_utc TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void Upsert(RemoteItem item)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO remote_items (server_id, remote_item_id, type, name, year, tmdb, imdb, tvdb, provider_ids_json,
                runtime_ticks, width, height, container, bitrate, media_source_json, last_seen_utc)
            VALUES ($sid, $rid, $type, $name, $year, $tmdb, $imdb, $tvdb, $pids,
                $rt, $w, $h, $cnt, $br, $ms, $ts)
            ON CONFLICT(server_id, remote_item_id) DO UPDATE SET
                type=excluded.type, name=excluded.name, year=excluded.year,
                tmdb=excluded.tmdb, imdb=excluded.imdb, tvdb=excluded.tvdb,
                provider_ids_json=excluded.provider_ids_json,
                runtime_ticks=excluded.runtime_ticks, width=excluded.width, height=excluded.height,
                container=excluded.container, bitrate=excluded.bitrate,
                media_source_json=excluded.media_source_json, last_seen_utc=excluded.last_seen_utc;";
        cmd.Parameters.AddWithValue("$sid", item.ServerId.ToString());
        cmd.Parameters.AddWithValue("$rid", item.RemoteItemId);
        cmd.Parameters.AddWithValue("$type", item.Type);
        cmd.Parameters.AddWithValue("$name", item.Name);
        cmd.Parameters.AddWithValue("$year", (object?)item.ProductionYear ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tmdb", item.ProviderIds.GetValueOrDefault("Tmdb") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$imdb", item.ProviderIds.GetValueOrDefault("Imdb") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$tvdb", item.ProviderIds.GetValueOrDefault("Tvdb") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$pids", JsonSerializer.Serialize(item.ProviderIds));
        cmd.Parameters.AddWithValue("$rt", (object?)item.RunTimeTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)item.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)item.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cnt", (object?)item.Container ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$br", (object?)item.Bitrate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", (object?)item.MediaSourceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", item.LastSeenUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<RemoteItem> FindMatches(string? tmdb, string? imdb, string? title, int? year)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();

        string where;
        if (!string.IsNullOrEmpty(tmdb))
        {
            where = "tmdb = $v";
            cmd.Parameters.AddWithValue("$v", tmdb);
        }
        else if (!string.IsNullOrEmpty(imdb))
        {
            where = "imdb = $v";
            cmd.Parameters.AddWithValue("$v", imdb);
        }
        else if (!string.IsNullOrEmpty(title) && year.HasValue)
        {
            where = "name = $n AND year = $y";
            cmd.Parameters.AddWithValue("$n", title);
            cmd.Parameters.AddWithValue("$y", year.Value);
        }
        else
        {
            yield break;
        }

        cmd.CommandText = $"SELECT server_id, remote_item_id, type, name, year, media_source_json, container, width, height, bitrate, runtime_ticks FROM remote_items WHERE {where};";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new RemoteItem
            {
                ServerId = Guid.Parse(r.GetString(0)),
                RemoteItemId = r.GetString(1),
                Type = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                Name = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                ProductionYear = r.IsDBNull(4) ? null : r.GetInt32(4),
                MediaSourceJson = r.IsDBNull(5) ? null : r.GetString(5),
                Container = r.IsDBNull(6) ? null : r.GetString(6),
                Width = r.IsDBNull(7) ? null : r.GetInt32(7),
                Height = r.IsDBNull(8) ? null : r.GetInt32(8),
                Bitrate = r.IsDBNull(9) ? null : r.GetInt64(9),
                RunTimeTicks = r.IsDBNull(10) ? null : r.GetInt64(10)
            };
        }
    }

    public IEnumerable<Models.RemoteItem> GetAllItems()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT server_id, remote_item_id, type, name, year, provider_ids_json,
            media_source_json, container, width, height, bitrate, runtime_ticks FROM remote_items;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var item = new Models.RemoteItem
            {
                ServerId = Guid.Parse(r.GetString(0)),
                RemoteItemId = r.GetString(1),
                Type = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                Name = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                ProductionYear = r.IsDBNull(4) ? null : r.GetInt32(4),
                MediaSourceJson = r.IsDBNull(6) ? null : r.GetString(6),
                Container = r.IsDBNull(7) ? null : r.GetString(7),
                Width = r.IsDBNull(8) ? null : r.GetInt32(8),
                Height = r.IsDBNull(9) ? null : r.GetInt32(9),
                Bitrate = r.IsDBNull(10) ? null : r.GetInt64(10),
                RunTimeTicks = r.IsDBNull(11) ? null : r.GetInt64(11)
            };
            if (!r.IsDBNull(5))
            {
                try
                {
                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5));
                    if (dict is not null) item.ProviderIds = dict;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "ProviderIds JSON decode failed; treating as empty"); }
            }
            yield return item;
        }
    }

    public Models.RemoteItem? GetItem(Guid serverId, string remoteItemId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT server_id, remote_item_id, type, name, year, provider_ids_json,
            media_source_json, container, width, height, bitrate, runtime_ticks FROM remote_items
            WHERE server_id=$s AND remote_item_id=$i LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", serverId.ToString());
        cmd.Parameters.AddWithValue("$i", remoteItemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var item = new Models.RemoteItem
        {
            ServerId = Guid.Parse(r.GetString(0)),
            RemoteItemId = r.GetString(1),
            Type = r.IsDBNull(2) ? string.Empty : r.GetString(2),
            Name = r.IsDBNull(3) ? string.Empty : r.GetString(3),
            ProductionYear = r.IsDBNull(4) ? null : r.GetInt32(4),
            MediaSourceJson = r.IsDBNull(6) ? null : r.GetString(6),
            Container = r.IsDBNull(7) ? null : r.GetString(7),
            Width = r.IsDBNull(8) ? null : r.GetInt32(8),
            Height = r.IsDBNull(9) ? null : r.GetInt32(9),
            Bitrate = r.IsDBNull(10) ? null : r.GetInt64(10),
            RunTimeTicks = r.IsDBNull(11) ? null : r.GetInt64(11)
        };
        if (!r.IsDBNull(5))
        {
            try
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5));
                if (dict is not null) item.ProviderIds = dict;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "ProviderIds JSON decode failed; treating as empty"); }
        }
        return item;
    }

    public long BeginAudit(Guid peerId, string itemId, string? userId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO stream_audit (peer_id, item_id, started_utc, user_id) VALUES ($p, $i, $t, $u); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", peerId.ToString());
        cmd.Parameters.AddWithValue("$i", itemId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$u", (object?)userId ?? DBNull.Value);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void CompleteAudit(long auditId, long bytesServed)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"UPDATE stream_audit SET ended_utc = $t, bytes_served = $b WHERE id = $i;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$b", bytesServed);
        cmd.Parameters.AddWithValue("$i", auditId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(Guid PeerId, int ItemCount)> CountItemsPerPeer()
    {
        var result = new List<(Guid, int)>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT server_id, COUNT(*) FROM remote_items GROUP BY server_id;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add((Guid.Parse(r.GetString(0)), r.GetInt32(1)));
        return result;
    }

    public (long TotalBytes, int StreamCount) PeerStreamTotals(Guid peerId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes_served), 0), COUNT(*) FROM stream_audit WHERE peer_id = $p;";
        cmd.Parameters.AddWithValue("$p", peerId.ToString());
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt32(1)) : (0L, 0);
    }

    public IReadOnlyList<(Guid PeerId, string ItemId, int PlayCount, long Bytes)> TopStreamedItems(int limit = 10)
    {
        var rows = new List<(Guid, string, int, long)>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT peer_id, item_id, COUNT(*), COALESCE(SUM(bytes_served), 0)
            FROM stream_audit GROUP BY peer_id, item_id ORDER BY 3 DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt32(2), r.GetInt64(3)));
        return rows;
    }

    public int CountDistinctTmdbAcrossPeers()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT tmdb) FROM remote_items WHERE tmdb IS NOT NULL AND tmdb <> '';";
        var v = cmd.ExecuteScalar();
        return v is long l ? (int)l : (v is int i ? i : 0);
    }

    /// <summary>Returns (rows_with_tmdb, distinct_tmdb_values) - used to compute dedup ratio
    /// on the TMDB-bearing subset only, so items without TMDB don't inflate the denominator.</summary>
    public (int TmdbRows, int DistinctTmdb) CountTmdbRowsAndDistinct()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COUNT(DISTINCT tmdb)
            FROM remote_items WHERE tmdb IS NOT NULL AND tmdb <> '';";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    public IEnumerable<(Guid PeerId, string ItemId, DateTime Started, DateTime? Ended, long Bytes)> RecentAudits(int limit = 100)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT peer_id, item_id, started_utc, ended_utc, bytes_served FROM stream_audit ORDER BY id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return (
                Guid.Parse(r.GetString(0)),
                r.GetString(1),
                DateTime.Parse(r.GetString(2)),
                r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3)),
                r.GetInt64(4)
            );
        }
    }

    public string? GetCachedDigest(Guid peerId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT catalog_hash FROM peer_digests WHERE peer_id = $p;";
        cmd.Parameters.AddWithValue("$p", peerId.ToString());
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : (string)r;
    }

    public void SaveDigest(Guid peerId, int count, string hash, DateTime? latestModified)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO peer_digests (peer_id, item_count, catalog_hash, latest_modified_utc, last_synced_utc)
            VALUES ($p, $cnt, $h, $lm, $ts)
            ON CONFLICT(peer_id) DO UPDATE SET item_count=excluded.item_count, catalog_hash=excluded.catalog_hash,
                latest_modified_utc=excluded.latest_modified_utc, last_synced_utc=excluded.last_synced_utc;";
        cmd.Parameters.AddWithValue("$p", peerId.ToString());
        cmd.Parameters.AddWithValue("$cnt", count);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$lm", (object?)latestModified?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void InvalidateDigest(Guid peerId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM peer_digests WHERE peer_id = $p;";
        cmd.Parameters.AddWithValue("$p", peerId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void DeleteItemsByIds(Guid peerId, IEnumerable<string> remoteItemIds)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx; // Microsoft.Data.Sqlite enforces this when a tx is open on the connection.
        cmd.CommandText = "DELETE FROM remote_items WHERE server_id = $sid AND remote_item_id = $rid;";
        var sidP = cmd.CreateParameter(); sidP.ParameterName = "$sid"; sidP.Value = peerId.ToString(); cmd.Parameters.Add(sidP);
        var ridP = cmd.CreateParameter(); ridP.ParameterName = "$rid"; cmd.Parameters.Add(ridP);
        foreach (var id in remoteItemIds)
        {
            ridP.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public HashSet<string> GetItemIdsForPeer(Guid peerId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT remote_item_id FROM remote_items WHERE server_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", peerId.ToString());
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void PurgeStale(Guid serverId, DateTime olderThanUtc)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM remote_items WHERE server_id = $sid AND last_seen_utc < $ts;";
        cmd.Parameters.AddWithValue("$sid", serverId.ToString());
        cmd.Parameters.AddWithValue("$ts", olderThanUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
