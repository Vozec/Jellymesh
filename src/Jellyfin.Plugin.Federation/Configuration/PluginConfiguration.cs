using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Federation.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<RemoteServer> RemoteServers { get; set; } = new();

    public List<ShareKey> Shares { get; set; } = new();

    public int SyncIntervalMinutes { get; set; } = 60;

    public bool EnableDedup { get; set; } = true;

    public MatchStrategy MatchPriority { get; set; } = MatchStrategy.TmdbThenImdbThenTitle;

    public bool ShowRemoteOnlyItems { get; set; } = true;

    public bool EnableWatchStateSync { get; set; } = false;

    public int RemoteOfflineTimeoutSeconds { get; set; } = 5;

    public long OutboundBitrateCapBps { get; set; } = 0;

    /// <summary>
    /// Our publicly reachable base URL (https://my-jellyfin.example.com). Sent to peers
    /// on push-invalidation calls so they can identify us in their RemoteServers list.
    /// If empty, push-invalidation is disabled (peers fall back to gossip-pull only).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Debounce window for push-invalidation. We collect library mutations for N seconds
    /// before firing one batch to peers, to avoid one POST per ItemAdded during a scan.
    /// </summary>
    public int PushDebounceSeconds { get; set; } = 30;

    // === Introductions (delegated key issuance, v0.10) ===

    /// <summary>Reciprocity mode. See docs/introductions.md.</summary>
    public ReciprocityMode Reciprocity { get; set; } = ReciprocityMode.Off;

    /// <summary>Scope of auto-minted reciprocal keys when Reciprocity=AutoAcceptReciprocal.</summary>
    public ShareKeyTemplate ReciprocityTemplate { get; set; } = new();

    /// <summary>Reject introductions whose hop_count exceeds this. null = no cap.</summary>
    public int? IntroductionHopCap { get; set; }

    /// <summary>Per-introducer-key rate limits. Defaults: 5/h, 50/d.</summary>
    public int IntroductionRatePerHour { get; set; } = 5;
    public int IntroductionRatePerDay { get; set; } = 50;
}

public enum ReciprocityMode { Off, AutoRequest, AutoAcceptReciprocal }

public enum IntroductionMintMode { Reject, Request, AutoAccept }

/// <summary>Subset of ShareKey fields used as a scope template (for ReciprocityTemplate).</summary>
public class ShareKeyTemplate
{
    public List<string> LibraryIds { get; set; } = new(); // empty = all libs
    public List<string> BlockedTags { get; set; } = new();
    public string? MaxOfficialRating { get; set; }
    public bool StrictUnknownRating { get; set; } = false;
}

public class ShareKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Random opaque token sent by the peer in the X-Federation-Share header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Human label, e.g. "Movies → Alice".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Jellyfin library (TopParent) ids that this key may see. Empty = all libraries.</summary>
    public List<string> LibraryIds { get; set; } = new();

    /// <summary>HH:mm (server local TZ). When both set, peer access is denied outside this window.
    /// Cross-midnight ranges supported (e.g. 18:00–02:00 means evening + early night).</summary>
    public string? AllowedHoursStart { get; set; }

    public string? AllowedHoursEnd { get; set; }

    /// <summary>IANA tz id ("Europe/Paris") interpreted as the wall-clock reference for
    /// AllowedHoursStart/End. Empty = host local TZ (which is UTC in most Docker setups —
    /// admins who type "18:00" expecting local should set this explicitly).</summary>
    public string? ScheduleTimeZoneId { get; set; }

    /// <summary>Items carrying any of these tags are hidden from the share.</summary>
    public List<string> BlockedTags { get; set; } = new();

    /// <summary>Max official rating shown. Items with a recognised rating strictly above
    /// are hidden. Format: Jellyfin rating string (e.g. "PG-13", "TV-MA"). Null = no filter.</summary>
    public string? MaxOfficialRating { get; set; }

    /// <summary>When true and MaxOfficialRating is set, hide any item whose rating string
    /// can't be parsed (regional ratings like "16+", "FSK-12", "BBFC-18"). Default false
    /// matches Jellyfin's own leniency — but for childproofing this should be true.</summary>
    public bool StrictUnknownRating { get; set; } = false;

    /// <summary>Optional. When set, peer push-invalidation / request callbacks that present
    /// this share key MUST come from this URL (canonicalized scheme://host:port). The
    /// payload's claimed FromBaseUrl is ignored for attribution. Closes the spoofing case
    /// where any share-key holder could claim to be a different peer in our config.
    /// Empty = trust payload.FromBaseUrl (legacy behaviour).</summary>
    public string? BoundPeerUrl { get; set; }

    // === Introductions (v0.10) ===

    /// <summary>"This key may call /Federation/Introduce on me" — opt-in per key,
    /// can be toggled post-creation. Default false.</summary>
    public bool CanRequestIntroductions { get; set; } = false;

    /// <summary>How we respond when this key requests a mint: Reject / Request (admin
    /// approves) / AutoAccept. Default Request.</summary>
    public IntroductionMintMode MintMode { get; set; } = IntroductionMintMode.Request;

    /// <summary>When this key was minted via an introduction: the canonical URL it was
    /// minted for. Null for manually-created keys.</summary>
    public string? IssuedForUrl { get; set; }

    /// <summary>When this key was minted via an introduction: which introducer-key
    /// requested it. Null for manually-created keys. Used for cascade revoke.</summary>
    public Guid? IntroducedByKeyId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool Enabled { get; set; } = true;
}

public class RemoteServer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Jellyfin user-level API key for streaming. Used as X-Emby-Token on /Items, /Videos, /Users/*.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Share key the peer issued for us. Sent as X-Federation-Share on /Federation/Share/* calls.</summary>
    public string FederationShareKey { get; set; } = string.Empty;

    public string? RemoteUserId { get; set; }

    /// <summary>
    /// Optional local user id. When set, on each sync round we pull this peer's
    /// played/in-progress items and merge their UserData into this local user (TMDB-matched
    /// items only). Empty = pull-direction watch sync disabled for this peer.
    /// </summary>
    public string? LocalUserIdForSync { get; set; }

    public bool Enabled { get; set; } = true;

    public List<string> AllowedLibraryIds { get; set; } = new();
}

public enum MatchStrategy
{
    TmdbOnly,
    TmdbThenImdb,
    TmdbThenImdbThenTitle
}
