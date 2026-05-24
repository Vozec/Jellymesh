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
        if (!introducerKey.CanRequestIntroductions)
            return MintResult.Denied("no-permission", "this key is not allowed to request introductions");

        var forCanon = PeerUrl.Canonicalize(forUrl);
        if (forCanon is null)
            return MintResult.Denied("bad-url", "ForUrl must include http:// or https:// scheme");

        var ourCanon = PeerUrl.Canonicalize(config.PublicBaseUrl);
        if (ourCanon is not null && string.Equals(forCanon, ourCanon, StringComparison.Ordinal))
            return MintResult.Denied("self", "cannot introduce ourselves");

        if (config.RemoteServers.Any(s => PeerUrl.SameHost(s.BaseUrl, forCanon)))
            return MintResult.Denied("already-peer", "target is already a configured peer");

        if (config.IntroductionHopCap.HasValue && hopCount > config.IntroductionHopCap.Value)
            return MintResult.Denied("hop-cap", $"hop_count {hopCount} exceeds cap {config.IntroductionHopCap.Value}");

        var (hourCount, dayCount) = _store.CountRecentByIntroducer(introducerKey.Id);
        if (hourCount >= config.IntroductionRatePerHour)
            return MintResult.Denied("rate-limit-hour", $"introducer hit {config.IntroductionRatePerHour}/hour limit");
        if (dayCount >= config.IntroductionRatePerDay)
            return MintResult.Denied("rate-limit-day", $"introducer hit {config.IntroductionRatePerDay}/day limit");

        switch (introducerKey.MintMode)
        {
            case IntroductionMintMode.Reject:
                var rejectedId = _store.InsertPending("issuer", forCanon, introducerKey.Id, hopCount, note);
                if (rejectedId > 0)
                {
                    try { _store.UpdateStatus(rejectedId, "rejected"); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Couldn't flip rejection audit row to rejected"); }
                }
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
        // Mint in-memory first so the dedup row below carries the issued_key_id at insert
        // time. A concurrent second mint that loses the InsertActiveOrGet race immediately
        // sees the populated id and returns it, instead of racing on a null-id row.
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
            // No auto-chain: minted keys can't themselves request further introductions.
            CanRequestIntroductions = false,
            MintMode = IntroductionMintMode.Reject,
            Enabled = true
        };

        // Approval path activates the pre-existing pending row. Skip InsertActiveOrGet,
        // which would conflict with the now-activated row on UNIQUE(role, for_url).
        if (existingPendingId.HasValue)
        {
            lock (Plugin.ConfigWriteLock)
            {
                config.Shares.Add(newKey);
                Plugin.Instance?.SaveConfiguration();
            }
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

        var (introId, isNew, existingKeyIdStr) = _store.InsertActiveOrGet(
            "issuer", forCanon, introducerKey.Id,
            issuedKeyId: newKey.Id, hopCount, note);

        if (!isNew)
        {
            // Concurrent mint claimed the slot first. Return its key so both introducers
            // see the same ApiKey value. Our freshly-minted key is in-memory only, discard.
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
            // Audit row points to a deleted key. Fall through and register the new key to
            // repair the link.
        }

        lock (Plugin.ConfigWriteLock)
        {
            config.Shares.Add(newKey);
            Plugin.Instance?.SaveConfiguration();
        }

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
    /// <summary>One of: minted, existing, minted-after-pending, pending, rejected, denied,
    /// no-permission, bad-url, self, already-peer, hop-cap, rate-limit-hour, rate-limit-day,
    /// unknown-mode, not-pending, wrong-role, no-introducer.</summary>
    public string Status { get; set; } = "denied";
    public string? ApiKey { get; set; }
    public string? OurBaseUrl { get; set; }
    public long? IntroductionId { get; set; }
    public Guid? MintedKeyId { get; set; }
    public string? Reason { get; set; }

    public static MintResult Denied(string status, string reason) => new() { Status = status, Reason = reason };

    public bool IsSuccess => Status is "minted" or "existing" or "minted-after-pending";
}
