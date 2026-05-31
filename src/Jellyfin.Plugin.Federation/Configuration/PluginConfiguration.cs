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
    /// Base64 HMAC key used to sign short-lived federated media-stream tokens (the 'fst'
    /// query param on /Videos/fed_X and /Federation/Stream URLs). Generated lazily on first
    /// use and persisted so the tokens survive restarts. Rotating it invalidates in-flight
    /// playback tokens.
    /// </summary>
    public string MediaSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Extra hostnames the outbound SSRF guard permits beyond configured peers + our own
    /// PublicBaseUrl + loopback. Normally empty: the allowlist auto-derives from peers.
    /// </summary>
    public List<string> OutboundHostAllowlist { get; set; } = new();

    /// <summary>
    /// When true (default), a peer that presents a valid federation share key can call
    /// /Federation/ProvisionMediaKey to be handed a Jellyfin API key for stream proxying, so
    /// the federating admin only has to exchange ONE secret (the share key) instead of also
    /// creating a user API key by hand. Set false to require the media API key be configured
    /// manually (the share-key holder then cannot auto-obtain a full Jellyfin token).
    /// </summary>
    public bool AutoProvisionMediaKey { get; set; } = true;

    /// <summary>
    /// Our publicly reachable base URL (https://my-jellyfin.example.com). Sent to peers
    /// on push-invalidation calls so they can identify us in their RemoteServers list.
    /// If empty, push-invalidation is disabled (peers fall back to gossip-pull only).
    /// Typically differs from the server's bind URL (LAN IP / container hostname) so this
    /// is configured explicitly rather than auto-detected from the HTTP request.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>HTTP Basic credentials a peer must include when calling OUR PublicBaseUrl
    /// (when our Jellyfin sits behind a reverse proxy that requires Basic auth). Advertised
    /// in handshake payloads (access request / invite) so peers can add us as a RemoteServer
    /// without out-of-band coordination. Leave empty if our public URL is not Basic-gated.</summary>
    public string PublicBaseUrlBasicAuthUser { get; set; } = string.Empty;
    public string PublicBaseUrlBasicAuthPass { get; set; } = string.Empty;

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

    // === Direct peer handshake (request access + invite, v0.11) ===

    /// <summary>Accept anonymous /Federation/AccessRequest calls from any origin (still
    /// admin-approval-gated before any key is minted). Combine with InviteTokens / Allowlist
    /// for stricter gating.</summary>
    public bool AcceptOpenAccessRequests { get; set; } = false;

    /// <summary>Canonical URLs allowed to submit access requests. Empty + AcceptOpenAccessRequests=false + no valid InviteToken = all requests rejected.</summary>
    public List<string> AccessRequestAllowlist { get; set; } = new();

    /// <summary>One-time invite tokens. A request carrying a matching token is accepted (and the token is consumed).</summary>
    public List<AccessInviteToken> AccessRequestInviteTokens { get; set; } = new();

    /// <summary>Per-IP rate limit on the anonymous AccessRequest endpoint, per hour.</summary>
    public int AccessRequestPerIpHourLimit { get; set; } = 5;

    /// <summary>UI language for the plugin dashboard. "en" (default) or "fr".</summary>
    public string DashboardLanguage { get; set; } = "en";

    // === Master switches: unsolicited inbound contact ===
    // Each of these endpoints accepts unsolicited calls from peers. Disabling the switch
    // makes the endpoint refuse all traffic regardless of any per-key or per-token rule
    // ("we are not currently accepting new contacts"). Default: on (backwards-compatible).

    /// <summary>Accept inbound /Federation/AccessRequest at all. Off = endpoint returns 403
    /// regardless of AcceptOpenAccessRequests, allowlist, or invite tokens.</summary>
    public bool AcceptInboundAccessRequests { get; set; } = true;

    /// <summary>Accept inbound /Federation/InviteOffer at all. Off = no peer can push an
    /// invite key to us.</summary>
    public bool AcceptInboundInvites { get; set; } = true;

    /// <summary>Accept inbound /Federation/Introduce and /Federation/Introduced at all.
    /// Off = no peer can request or forward introductions to us (existing per-key MintMode
    /// rules still apply when on).</summary>
    public bool AcceptInboundIntroductions { get; set; } = true;

    /// <summary>Let other peers query /Federation/PeerDirectory to discover our list of
    /// configured peers (name + URL only, no keys). Allows admin on the other side to pick
    /// who to be introduced to instead of typing a URL blindly.</summary>
    public bool PublishPeerDirectory { get; set; } = true;

    /// <summary>Canonical URLs blocked from any inbound contact. Applied to AccessRequest
    /// (direct), InviteOffer (direct), and Introduced (forwarded via intermediary). A blocked
    /// URL cannot reach us even via a trusted introducer.</summary>
    public List<string> BlockedPeerUrls { get; set; } = new();

    /// <summary>Per-peer per-library config (visibility / homepage / merge target). Key:
    /// "{peerGuid}/{libraryId}". Empty entries are assumed enabled+visible.</summary>
    public List<PeerLibrarySetting> PeerLibrarySettings { get; set; } = new();

    /// <summary>How peer libraries surface on the home page.</summary>
    public PeerHomeLayout PeerHomeLayout { get; set; } = PeerHomeLayout.SectionPerPeer;

    /// <summary>Days to keep completed/denied access-request rows + audit attempts +
    /// health samples. Older rows are pruned by the cleanup task.</summary>
    public int RetentionDays { get; set; } = 30;

    // === Webhook outbound ===

    /// <summary>Optional outbound webhook URL. When set, POSTed for events in WebhookEvents.
    /// Generic JSON body {event, summary, ts, payload}. Empty = no webhook.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>Events to deliver. Comma-separated subset of:
    /// access-request, invite-offer, peer-offline, peer-online.</summary>
    public string WebhookEvents { get; set; } = "access-request,invite-offer,peer-offline";

    /// <summary>When true, use Discord's webhook content format (single "content" field).</summary>
    public bool WebhookDiscordFormat { get; set; } = false;
}

public class AccessInviteToken
{
    public string Token { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DateTime? ExpiresUtc { get; set; }
    public int? MaxUses { get; set; }
    public int UsedCount { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
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
    /// AllowedHoursStart/End. Empty = host local TZ (which is UTC in most Docker setups -
    /// admins who type "18:00" expecting local should set this explicitly).</summary>
    public string? ScheduleTimeZoneId { get; set; }

    /// <summary>Items carrying any of these tags are hidden from the share.</summary>
    public List<string> BlockedTags { get; set; } = new();

    /// <summary>Max official rating shown. Items with a recognised rating strictly above
    /// are hidden. Format: Jellyfin rating string (e.g. "PG-13", "TV-MA"). Null = no filter.</summary>
    public string? MaxOfficialRating { get; set; }

    /// <summary>When true and MaxOfficialRating is set, hide any item whose rating string
    /// can't be parsed (regional ratings like "16+", "FSK-12", "BBFC-18"). Default false
    /// matches Jellyfin's own leniency - but for childproofing this should be true.</summary>
    public bool StrictUnknownRating { get; set; } = false;

    /// <summary>Optional. When set, peer push-invalidation / request callbacks that present
    /// this share key MUST come from this URL (canonicalized scheme://host:port). The
    /// payload's claimed FromBaseUrl is ignored for attribution. Closes the spoofing case
    /// where any share-key holder could claim to be a different peer in our config.
    /// Empty = trust payload.FromBaseUrl (legacy behaviour).</summary>
    public string? BoundPeerUrl { get; set; }

    // === Introductions (v0.10) ===

    /// <summary>"This key may call /Federation/Introduce on me" - opt-in per key,
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

    /// <summary>Optional HTTP Basic auth credentials, when the peer's Jellyfin is behind a
    /// reverse proxy that requires Basic auth (common LAN setup). Sent as
    /// Authorization: Basic base64(user:pass) on every outbound call to this peer, in
    /// addition to X-Emby-Token or X-Federation-Share. Leave empty if the peer is
    /// reachable without Basic auth.</summary>
    public string BasicAuthUser { get; set; } = string.Empty;
    public string BasicAuthPass { get; set; } = string.Empty;

    // === Mutual TLS (client certificate auth to this peer) ===
    // Independent from Basic auth: Basic is an Authorization header, mTLS is a client
    // certificate presented during the TLS handshake. Both can be set at once. Only used for
    // https:// peers. Leave empty to disable.

    /// <summary>PEM-encoded client certificate presented to this peer for mutual TLS.</summary>
    public string ClientCertPem { get; set; } = string.Empty;

    /// <summary>PEM-encoded private key for ClientCertPem. If empty, the key is read from ClientCertPem.</summary>
    public string ClientCertKeyPem { get; set; } = string.Empty;

    /// <summary>Password for an encrypted client private key (optional).</summary>
    public string ClientCertPassword { get; set; } = string.Empty;

    /// <summary>PEM-encoded CA certificate(s) used to validate this peer's TLS server cert when
    /// it isn't signed by a publicly-trusted CA (self-signed / private CA). Optional.</summary>
    public string CaCertPem { get; set; } = string.Empty;

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

    /// <summary>Free-form tags used to group peers (family, friends, public...). Empty by default.</summary>
    public List<string> Tags { get; set; } = new();

    // === Soft-block / per-peer quotas ===

    /// <summary>Max inbound HTTP requests per hour from this peer (any /Federation/* endpoint).
    /// 0 = unlimited. When exceeded, return 429 with Retry-After.</summary>
    public int InboundReqPerHourLimit { get; set; } = 0;

    /// <summary>Max bytes served outbound to this peer per day (sum of /Federation/Stream).
    /// 0 = unlimited. When exceeded, return 429.</summary>
    public long OutboundBytesPerDayLimit { get; set; } = 0;
}

public enum PeerHomeLayout
{
    /// <summary>One section per peer (default).</summary>
    SectionPerPeer,
    /// <summary>One combined 'From your friends' section pooling all peer libs.</summary>
    OneSectionAllPeers,
    /// <summary>Hide federated content from the home page entirely.</summary>
    Off
}

public class PeerLibrarySetting
{
    public Guid PeerId { get; set; }
    public string LibraryId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool HideFromHomepage { get; set; }
    /// <summary>If set, items from this peer library are also injected into the local library
    /// with this id when browsing it. Empty = no merge.</summary>
    public string? MergeWithLocalLibraryId { get; set; }
}

public enum MatchStrategy
{
    TmdbOnly,
    TmdbThenImdb,
    TmdbThenImdbThenTitle
}
