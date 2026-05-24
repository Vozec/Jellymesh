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

    private string ConnString => $"Data Source={_dbPath}";

    private void InitSchema()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
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
                catch { /* ignore */ }
            }
            yield return item;
        }
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
