using System;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Orchestrates the issuer side of an introduction: resolve introducer-key trust, run
/// loop-prevention checks (self, already-peer, hop-cap, rate-limit), dedup via the store,
/// mint the new ShareKey with scope inherited from the introducer, persist.
///
/// Pure logic - config is passed in as a snapshot so unit tests don't need Plugin.Instance.
/// </summary>
public class IntroductionService
{
    private readonly IntroductionStore _store;
    private readonly ILogger<IntroductionService> _logger;

    public IntroductionService(IntroductionStore store, ILogger<IntroductionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public MintResult TryMint(PluginConfiguration config, ShareKey introducerKey, string forUrl, int hopCount, string? note)
    {
        // 0. Key must have intro permission.
        if (!introducerKey.CanRequestIntroductions)
            return MintResult.Denied("no-permission", "this key is not allowed to request introductions");

        // 1. Canonicalize target URL.
        var forCanon = PeerUrl.Canonicalize(forUrl);
        if (forCanon is null)
            return MintResult.Denied("bad-url", "ForUrl must include http:// or https:// scheme");

        // 2. Self-exclusion.
        var ourCanon = PeerUrl.Canonicalize(config.PublicBaseUrl);
        if (ourCanon is not null && string.Equals(forCanon, ourCanon, StringComparison.Ordinal))
            return MintResult.Denied("self", "cannot introduce ourselves");

        // 3. Already-peer (canonical match against any configured RemoteServer).
        if (config.RemoteServers.Any(s => PeerUrl.SameHost(s.BaseUrl, forCanon)))
            return MintResult.Denied("already-peer", "target is already a configured peer");

        // 4. Hop-cap.
        if (config.IntroductionHopCap.HasValue && hopCount > config.IntroductionHopCap.Value)
            return MintResult.Denied("hop-cap", $"hop_count {hopCount} exceeds cap {config.IntroductionHopCap.Value}");

        // 5. Rate-limit per introducer key.
        var (hourCount, dayCount) = _store.CountRecentByIntroducer(introducerKey.Id);
        if (hourCount >= config.IntroductionRatePerHour)
            return MintResult.Denied("rate-limit-hour", $"introducer hit {config.IntroductionRatePerHour}/hour limit");
        if (dayCount >= config.IntroductionRatePerDay)
            return MintResult.Denied("rate-limit-day", $"introducer hit {config.IntroductionRatePerDay}/day limit");

        // 6. Mint mode decision.
        switch (introducerKey.MintMode)
        {
            case IntroductionMintMode.Reject:
                _store.InsertPending("issuer", forCanon, introducerKey.Id, hopCount, note);
                _store.UpdateStatus(_store.ListByRole("issuer", "pending").First(p => p.ForUrlCanonical == forCanon).Id, "rejected");
                return MintResult.Denied("rejected", "introducer key is in Reject mode");

            case IntroductionMintMode.Request:
                var pendingId = _store.InsertPending("issuer", forCanon, introducerKey.Id, hopCount, note);
                return new MintResult { Status = "pending", IntroductionId = pendingId, Reason = "queued for admin approval" };

            case IntroductionMintMode.AutoAccept:
                return MintAndPersist(config, introducerKey, forCanon, hopCount, note);

            default:
                return MintResult.Denied("unknown-mode", $"unknown MintMode {introducerKey.MintMode}");
        }
    }

    /// <summary>Admin approves a pending introduction → mints + activates.</summary>
    public MintResult ApprovePending(PluginConfiguration config, long introductionId)
    {
        var intro = _store.Get(introductionId);
        if (intro is null || intro.Status != "pending")
            return MintResult.Denied("not-pending", "introduction does not exist or is not pending");
        if (intro.OurRole != "issuer")
            return MintResult.Denied("wrong-role", "only issuer-role introductions are minted on approval");

        var introducerKey = config.Shares.FirstOrDefault(k => k.Id == intro.IntroducerKeyId);
        if (introducerKey is null)
            return MintResult.Denied("no-introducer", "introducer key no longer exists");

        return MintAndPersist(config, introducerKey, intro.ForUrlCanonical, intro.HopCount, intro.Note, intro.Id);
    }

    private MintResult MintAndPersist(PluginConfiguration config, ShareKey introducerKey, string forCanon, int hopCount, string? note, long? existingPendingId = null)
    {
        // Mint FIRST (in-memory only - no DB writes) so the InsertActiveOrGet call below
        // can atomically claim the dedup slot WITH the issued_key_id already set. This
        // avoids the race where a concurrent second call sees issued_key_id=null and
        // re-mints.
        var newKey = new ShareKey
        {
            ApiKey = GenerateApiKey(),
            Label = $"Introduced as {forCanon} by {introducerKey.Label}",
            LibraryIds = introducerKey.LibraryIds.ToList(),
            BlockedTags = introducerKey.BlockedTags.ToList(),
            MaxOfficialRating = introducerKey.MaxOfficialRating,
            StrictUnknownRating = introducerKey.StrictUnknownRating,
            AllowedHoursStart = introducerKey.AllowedHoursStart,
            AllowedHoursEnd = introducerKey.AllowedHoursEnd,
            ScheduleTimeZoneId = introducerKey.ScheduleTimeZoneId,
            BoundPeerUrl = forCanon,
            IssuedForUrl = forCanon,
            IntroducedByKeyId = introducerKey.Id,
            CanRequestIntroductions = false,                   // no auto-chain
            MintMode = IntroductionMintMode.Reject,             // no auto-chain
            Enabled = true
        };

        // Approval path: the pending row already exists; activating it claims the unique
        // slot. Skip the InsertActiveOrGet (which would conflict with the now-activated row).
        if (existingPendingId.HasValue)
        {
            config.Shares.Add(newKey);
            Plugin.Instance?.SaveConfiguration();
            _store.Activate(existingPendingId.Value, newKey.Id);
            _logger.LogInformation("Approved introduction {Id} → minted key {KeyId} for {Url}",
                existingPendingId.Value, newKey.Id, forCanon);
            return new MintResult
            {
                Status = "minted-after-pending",
                ApiKey = newKey.ApiKey,
                OurBaseUrl = PeerUrl.Canonicalize(config.PublicBaseUrl),
                IntroductionId = existingPendingId.Value,
                MintedKeyId = newKey.Id
            };
        }

        // Fresh-mint path: atomic claim or detect existing.
        var (introId, isNew, existingKeyIdStr) = _store.InsertActiveOrGet(
            "issuer", forCanon, introducerKey.Id,
            issuedKeyId: newKey.Id, hopCount, note);

        if (!isNew)
        {
            // Lost the race - another concurrent mint already claimed the slot. Discard
            // our freshly-minted key (it was in-memory only, never persisted), look up the
            // existing key, return it. Both introducers thus see the same ApiKey value.
            if (!string.IsNullOrEmpty(existingKeyIdStr) && Guid.TryParse(existingKeyIdStr, out var existingKeyId))
            {
                var existingKey = config.Shares.FirstOrDefault(k => k.Id == existingKeyId);
                if (existingKey is not null)
                {
                    _logger.LogInformation("Introduction dedup hit for {Url}: returning existing key {Id}", forCanon, existingKeyId);
                    return new MintResult
                    {
                        Status = "existing",
                        ApiKey = existingKey.ApiKey,
                        OurBaseUrl = PeerUrl.Canonicalize(config.PublicBaseUrl),
                        IntroductionId = introId
                    };
                }
            }
            // Existing intro row but the corresponding share key is gone (deleted between
            // mint and read). Fall through to register our key - repairs the broken link.
        }

        // Persist the new key.
        config.Shares.Add(newKey);
        Plugin.Instance?.SaveConfiguration();

        _logger.LogInformation("Minted introduction key {KeyId} for {Url} via introducer {IntroKeyId}",
            newKey.Id, forCanon, introducerKey.Id);

        return new MintResult
        {
            Status = "minted",
            ApiKey = newKey.ApiKey,
            OurBaseUrl = PeerUrl.Canonicalize(config.PublicBaseUrl),
            IntroductionId = introId,
            MintedKeyId = newKey.Id
        };
    }

    private static string GenerateApiKey()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}

public class MintResult
{
    public string Status { get; set; } = "denied"; // minted|existing|pending|rejected|denied|...
    public string? ApiKey { get; set; }
    public string? OurBaseUrl { get; set; }
    public long? IntroductionId { get; set; }
    public Guid? MintedKeyId { get; set; }
    public string? Reason { get; set; }

    public static MintResult Denied(string status, string reason) => new() { Status = status, Reason = reason };

    public bool IsSuccess => Status is "minted" or "existing" or "minted-after-pending";
}
