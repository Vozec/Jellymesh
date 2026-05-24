using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

public class PublicShareStore
{
    private readonly string _dbPath;
    private readonly ILogger<PublicShareStore> _logger;

    public PublicShareStore(IApplicationPaths appPaths, ILogger<PublicShareStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "public_shares.db");
        InitSchema();
    }

    private string ConnString => $"Data Source={_dbPath}";

    private void InitSchema()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS public_shares (
                token TEXT PRIMARY KEY,
                item_id TEXT NOT NULL,
                expires_utc TEXT,
                max_uses INTEGER,
                used_count INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                creator TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_public_item ON public_shares(item_id);
        ";
        cmd.ExecuteNonQuery();
    }

    public string Create(string itemId, DateTime? expiresUtc, int? maxUses, string? creator)
    {
        var token = GenerateToken();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO public_shares (token, item_id, expires_utc, max_uses, used_count, created_utc, creator)
            VALUES ($t, $i, $e, $m, 0, $c, $cr);";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.Parameters.AddWithValue("$i", itemId);
        cmd.Parameters.AddWithValue("$e", (object?)expiresUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)maxUses ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$cr", (object?)creator ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return token;
    }

    /// <summary>Atomically validate + increment used_count. Returns the item id if allowed, null if denied.</summary>
    public string? TryConsume(string token)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var tx = c.BeginTransaction();

        using var sel = c.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT item_id, expires_utc, max_uses, used_count FROM public_shares WHERE token = $t;";
        sel.Parameters.AddWithValue("$t", token);
        using var r = sel.ExecuteReader();
        if (!r.Read()) { tx.Commit(); return null; }

        var itemId = r.GetString(0);
        var expiresStr = r.IsDBNull(1) ? null : r.GetString(1);
        var maxUses = r.IsDBNull(2) ? (int?)null : r.GetInt32(2);
        var usedCount = r.GetInt32(3);
        r.Close();

        // Use RoundtripKind so the stored "O"-formatted timestamp keeps Kind=Utc on parse,
        // otherwise it round-trips through Local and the comparison to UtcNow drifts by the
        // host's TZ offset (off-by-hours expiry bugs).
        if (!string.IsNullOrEmpty(expiresStr) && DateTime.Parse(expiresStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) < DateTime.UtcNow) { tx.Commit(); return null; }
        if (maxUses.HasValue && usedCount >= maxUses.Value) { tx.Commit(); return null; }

        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE public_shares SET used_count = used_count + 1 WHERE token = $t;";
        upd.Parameters.AddWithValue("$t", token);
        upd.ExecuteNonQuery();

        tx.Commit();
        return itemId;
    }

    public ShareInfo? GetInfo(string token)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT item_id, expires_utc, max_uses, used_count, created_utc FROM public_shares WHERE token = $t;";
        cmd.Parameters.AddWithValue("$t", token);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ShareInfo
        {
            Token = token,
            ItemId = r.GetString(0),
            ExpiresUtc = r.IsDBNull(1) ? null : DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            MaxUses = r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
            UsedCount = r.GetInt32(3),
            CreatedUtc = DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public System.Collections.Generic.IEnumerable<ShareInfo> ListAll(int limit = 200)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT token, item_id, expires_utc, max_uses, used_count, created_utc
            FROM public_shares ORDER BY created_utc DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new ShareInfo
            {
                Token = r.GetString(0),
                ItemId = r.GetString(1),
                ExpiresUtc = r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                MaxUses = r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                UsedCount = r.GetInt32(4),
                CreatedUtc = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
        }
    }

    public void Revoke(string token)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM public_shares WHERE token = $t;";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.ExecuteNonQuery();
    }

    private static string GenerateToken()
    {
        // 24 bytes → 32 base64url chars; URL-safe (no /+= padding).
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

public class ShareInfo
{
    public string Token { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public DateTime? ExpiresUtc { get; set; }
    public int? MaxUses { get; set; }
    public int UsedCount { get; set; }
    public DateTime CreatedUtc { get; set; }
}
