using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// SQLite-backed audit + dedup store for federation introductions.
///
/// Roles: 'issuer' (we minted), 'forwarder' (we relayed someone else's intro to a third
/// party), 'receiver' (we got handed a new peer's key).
///
/// Dedup: UNIQUE(our_role, for_url_canonical) WHERE status='active' - two concurrent
/// introductions for the same target collapse to one mint. See docs/introductions.md.
/// </summary>
public class IntroductionStore
{
    private readonly string _dbPath;
    private readonly ILogger<IntroductionStore> _logger;

    public IntroductionStore(IApplicationPaths appPaths, ILogger<IntroductionStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(appPaths.DataPath, "federation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "introductions.db");
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
            CREATE TABLE IF NOT EXISTS introductions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                our_role TEXT NOT NULL,                  -- issuer | forwarder | receiver
                introducer_key_id TEXT,                  -- the ShareKey that requested it
                for_url_canonical TEXT NOT NULL,
                issued_key_id TEXT,                      -- ShareKey we minted (issuer role only)
                hop_count INTEGER NOT NULL DEFAULT 1,
                status TEXT NOT NULL DEFAULT 'pending',  -- pending|active|rejected|revoked|expired
                created_utc TEXT NOT NULL,
                completed_utc TEXT,
                note TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_intro_role ON introductions(our_role);
            CREATE INDEX IF NOT EXISTS idx_intro_status ON introductions(status);
            CREATE INDEX IF NOT EXISTS idx_intro_introducer ON introductions(introducer_key_id);

            -- Dedup: only one ACTIVE introduction per (role, for_url) at a time.
            -- A concurrent second INSERT … ON CONFLICT DO NOTHING returns null →
            -- caller looks up the existing row's issued_key_id and reuses it.
            CREATE UNIQUE INDEX IF NOT EXISTS uniq_active_intro
                ON introductions(our_role, for_url_canonical)
                WHERE status = 'active';
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Atomic INSERT-or-return-existing. Returns the row id and a flag indicating
    /// whether it was newly inserted (true) or matched an existing active row (false).</summary>
    public (long Id, bool IsNew, string? ExistingIssuedKeyId) InsertActiveOrGet(
        string ourRole, string forUrlCanonical, Guid? introducerKeyId, Guid? issuedKeyId,
        int hopCount, string? note)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        var now = DateTime.UtcNow.ToString("O");

        // First try to insert. If a concurrent active row exists, ON CONFLICT skips.
        using var ins = c.CreateCommand();
        ins.CommandText = @"INSERT INTO introductions
            (our_role, introducer_key_id, for_url_canonical, issued_key_id,
             hop_count, status, created_utc, completed_utc, note)
            VALUES ($r, $ik, $f, $ek, $h, 'active', $t, $t, $n)
            ON CONFLICT DO NOTHING
            RETURNING id;";
        ins.Parameters.AddWithValue("$r", ourRole);
        ins.Parameters.AddWithValue("$ik", (object?)introducerKeyId?.ToString() ?? DBNull.Value);
        ins.Parameters.AddWithValue("$f", forUrlCanonical);
        ins.Parameters.AddWithValue("$ek", (object?)issuedKeyId?.ToString() ?? DBNull.Value);
        ins.Parameters.AddWithValue("$h", hopCount);
        ins.Parameters.AddWithValue("$t", now);
        ins.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);

        var r = ins.ExecuteScalar();
        if (r is long newId) return (newId, true, null);

        // Conflict - look up the existing active row.
        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT id, issued_key_id FROM introductions WHERE our_role = $r AND for_url_canonical = $f AND status = 'active';";
        sel.Parameters.AddWithValue("$r", ourRole);
        sel.Parameters.AddWithValue("$f", forUrlCanonical);
        using var rdr = sel.ExecuteReader();
        if (rdr.Read())
            return (rdr.GetInt64(0), false, rdr.IsDBNull(1) ? null : rdr.GetString(1));

        // Shouldn't happen - INSERT failed AND no existing row. Treat as opaque error.
        _logger.LogWarning("InsertActiveOrGet: conflict but no existing row for {Role} {Url}", ourRole, forUrlCanonical);
        return (0, false, null);
    }

    /// <summary>Insert a pending introduction (admin approval required to activate).</summary>
    public long InsertPending(string ourRole, string forUrlCanonical, Guid? introducerKeyId,
        int hopCount, string? note)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO introductions
            (our_role, introducer_key_id, for_url_canonical, hop_count, status, created_utc, note)
            VALUES ($r, $ik, $f, $h, 'pending', $t, $n)
            RETURNING id;";
        cmd.Parameters.AddWithValue("$r", ourRole);
        cmd.Parameters.AddWithValue("$ik", (object?)introducerKeyId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", forUrlCanonical);
        cmd.Parameters.AddWithValue("$h", hopCount);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public bool Activate(long id, Guid issuedKeyId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE introductions SET status='active', issued_key_id=$k, completed_utc=$t WHERE id=$id AND status='pending';";
        cmd.Parameters.AddWithValue("$k", issuedKeyId.ToString());
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool UpdateStatus(long id, string newStatus)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE introductions SET status=$s, completed_utc=$t WHERE id=$id;";
        cmd.Parameters.AddWithValue("$s", newStatus);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Counts active+recent introductions issued via a given introducer key.
    /// Used for rate limiting (per hour, per day).</summary>
    public (int Hour, int Day) CountRecentByIntroducer(Guid introducerKeyId)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT
            COUNT(*) FILTER (WHERE created_utc > $h),
            COUNT(*) FILTER (WHERE created_utc > $d)
            FROM introductions
            WHERE our_role='issuer' AND introducer_key_id=$ik AND status IN ('active','pending');";
        cmd.Parameters.AddWithValue("$ik", introducerKeyId.ToString());
        cmd.Parameters.AddWithValue("$h", DateTime.UtcNow.AddHours(-1).ToString("O"));
        cmd.Parameters.AddWithValue("$d", DateTime.UtcNow.AddDays(-1).ToString("O"));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    public IReadOnlyList<Introduction> ListByRole(string ourRole, string? status = null, int limit = 200)
    {
        var rows = new List<Introduction>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = status is null
            ? "SELECT id, our_role, introducer_key_id, for_url_canonical, issued_key_id, hop_count, status, created_utc, completed_utc, note FROM introductions WHERE our_role=$r ORDER BY created_utc DESC, id DESC LIMIT $l;"
            : "SELECT id, our_role, introducer_key_id, for_url_canonical, issued_key_id, hop_count, status, created_utc, completed_utc, note FROM introductions WHERE our_role=$r AND status=$s ORDER BY created_utc DESC, id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$r", ourRole);
        cmd.Parameters.AddWithValue("$l", limit);
        if (status is not null) cmd.Parameters.AddWithValue("$s", status);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) rows.Add(Read(rdr));
        return rows;
    }

    public Introduction? Get(long id)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, our_role, introducer_key_id, for_url_canonical, issued_key_id, hop_count, status, created_utc, completed_utc, note FROM introductions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? Read(rdr) : null;
    }

    /// <summary>Keys issued by a given introducer key. includeRevoked=true is required for
    /// cascade walks so a previously-revoked intermediate doesn't truncate descent into
    /// still-active grandchildren (review #5 finding).</summary>
    public IReadOnlyList<Introduction> ListIssuedBy(Guid introducerKeyId, bool includeRevoked = false)
    {
        var rows = new List<Introduction>();
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = includeRevoked
            ? "SELECT id, our_role, introducer_key_id, for_url_canonical, issued_key_id, hop_count, status, created_utc, completed_utc, note FROM introductions WHERE our_role='issuer' AND introducer_key_id=$ik ORDER BY created_utc DESC;"
            : "SELECT id, our_role, introducer_key_id, for_url_canonical, issued_key_id, hop_count, status, created_utc, completed_utc, note FROM introductions WHERE our_role='issuer' AND introducer_key_id=$ik AND status='active' ORDER BY created_utc DESC;";
        cmd.Parameters.AddWithValue("$ik", introducerKeyId.ToString());
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) rows.Add(Read(rdr));
        return rows;
    }

    private static Introduction Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        OurRole = r.GetString(1),
        IntroducerKeyId = r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)),
        ForUrlCanonical = r.GetString(3),
        IssuedKeyId = r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)),
        HopCount = r.GetInt32(5),
        Status = r.GetString(6),
        CreatedUtc = DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CompletedUtc = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Note = r.IsDBNull(9) ? null : r.GetString(9)
    };
}

public class Introduction
{
    public long Id { get; set; }
    public string OurRole { get; set; } = "issuer";
    public Guid? IntroducerKeyId { get; set; }
    public string ForUrlCanonical { get; set; } = string.Empty;
    public Guid? IssuedKeyId { get; set; }
    public int HopCount { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? Note { get; set; }
}
