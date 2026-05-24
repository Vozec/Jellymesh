# Changelog

All notable changes to this project will be documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Semantic versioning.

## [Unreleased]

### Added
- M1–M4 scaffold: plugin entry, config UI, remote server CRUD, scheduled
  sync, SQLite cache, IMediaSourceProvider injecting peer sources on
  matched local items, stream proxy controller with auth-rewriting reverse
  proxy.
- M5: Friends Library `IChannel` surfacing peer-only items, deduped
  against local TMDB ids, paginated.
- M6: WatchStateSyncService pushing playback position + played flag to
  peers via reverse API on `UserDataSaved`.
- M7: Federated search endpoint fanning out across peers, 10 s per-peer
  timeout, results tagged with origin.
- HealthMonitorService + PeerHealthRegistry: 30 s ping rotation; offline
  peers hidden from Friends Library and source injection immediately;
  signature-based cache invalidation flips listings within one tick of a
  status change.
- Bandwidth cap (`OutboundBitrateCapBps`) via `ThrottledStream` on the
  outbound proxy.
- Per-stream audit log (`stream_audit` table) + `/Federation/Audit/Recent`.
- Gossip digest sync: `/Federation/Catalog/Digest` SHA-256 over sorted
  (id, lastModifiedTicks) pairs; sync task skips pull when peer digest
  unchanged (anti-spam); deletion detection via id-set diff.
- Manual sync trigger: `POST /Federation/Sync/Trigger`.
- Per-library share keys: separate from Jellyfin user token, scopes
  `/Federation/Share/Catalog/*` to a subset of libraries. UI to
  generate/revoke. Random 32-byte hex.
- xUnit test project (5 tests) covering ThrottledStream timing,
  RemoteItemStore upsert/find/audit lifecycle. Roll-forward to net10 on
  dev box via env var; CI runs on .NET 8.
- GitHub Actions workflow: build /warnaserror + test + package zip; second
  job renders mermaid diagrams to PNG and verifies count matches source.
- Docs: architecture, protocol, sync-flow with rendered Mermaid PNGs;
  `docs/render-diagrams.sh` auto-bootstraps chrome-headless-shell.

### Fixed (code-review pass)
- **Critical**: federated MediaSourceId is no longer rewritten with the
  `fed_<guid>_` prefix when forwarded to the peer — both
  `FederatedMediaSourceProvider` and `FriendsLibraryChannel` keep the
  original id in `?sourceId=`. Wrong source / 4xx on multi-source items.
- **Critical**: `RemoteItemStore.DeleteItemsByIds` now associates the
  command with the open SqliteTransaction — was throwing
  `InvalidOperationException` on first delete sync round.
- **Critical**: `RemoteJellyfinClient.FetchItemsAsync` paginates
  (StartIndex/Limit=500 against TotalRecordCount) instead of one-shot
  Limit=10000 — prevents the delete-detection pass from wiping items
  beyond #10000 on peers with large libraries.
- **Critical**: peer `MediaSource.Protocol` no longer force-overwritten
  to Http (was stripping Hls/File semantics from the rest of
  MediaSourceInfo).
- **Security**: `ResolveShareKey` uses
  `CryptographicOperations.FixedTimeEquals` (was short-circuiting `==` —
  bearer-key prefix leaked via response timing on `[AllowAnonymous]`
  endpoint).
- **Security**: channel `ImageUrl` no longer embeds the peer
  `X-Emby-Token` as a query string — new `/Federation/Image/...` reverse-
  proxies images with the key server-side.
- Reverse proxy filters hop-by-hop headers (Connection, Keep-Alive,
  Upgrade, TE, Trailer, Transfer-Encoding, Proxy-*) and drops upstream
  Content-Length when throttling is active.
- `BeginAudit` failure no longer breaks the stream (wrapped in try/catch
  with `auditId = -1` fallback).
- HttpResponseMessage now disposed via `using` on the stream proxy.
- Stream wrapper: when capped, ThrottledStream owns the inner stream
  (no separate `await using var src` → no double-dispose).
- `PeerHealthRegistry.IsOnline` returns true for unprobed peers — no
  longer hides federated sources during the 0–30 s post-start window.
- `FederationSyncTask` progress counter no longer double-increments on
  early-skip paths.
- Post-pull digest re-fetch — cached hash reflects what we actually
  stored, not the pre-pull snapshot. Prevents pull-thrash.
- `WatchStateSyncService` snapshots peer list before handing to
  background task (was racing config-save List mutation) and now
  propagates `Played=false` (un-marking watched federates).
- `ThrottledStream.Read` (sync) drops throttling — was sync-over-async
  with `Task.Delay` pinning thread-pool threads and ignoring
  cancellation.
- `FriendsLibraryChannel.DataVersion`/`GetCacheKey` no longer embed the
  UTC hour — channel cache no longer thrown away every hour.
- `FetchDigestAsync`, `FetchItemsAsync`, `ResolveRemoteItemIdAsync`: all
  JsonDocument and response Stream usages `using`'d.

### Added (continued)
- **Anonymous video share links**: per-video, expiring, use-capped public
  URLs (`/Federation/Public/{token}`). Viewer is a minimal HTML page with
  a `<video>` tag; stream endpoint serves the raw file with Range
  support. Token is 24-byte base64url. `PublicShareStore.TryConsume`
  atomically validates and increments use count in one transaction.

### Added (round 3)
- Push-based catalog invalidation (`PushInvalidationService`): debounced
  fire-and-forget POST to peers' `/Federation/Invalidate` on ItemAdded /
  ItemRemoved. Receiver matches sender by URL → drops cached digest →
  next sync round re-pulls.
- Pull-direction watch state sync: each round merges peer's played /
  in-progress / favorite / play-count into a configured local user, with
  loop-break via `UserDataSaveReason.Import`.
- Federation stats endpoint + dashboard UI block (peers online/enabled/
  total, cache + dedup ratio, total streams + bytes, per-peer table,
  top-5 streamed items, polls every 30s).
- ShareKey schedule windows (HH:mm in admin-chosen IANA TZ, cross-
  midnight supported, zero-length = never) + content filters
  (blocked tags + max official rating with optional strict-unknown mode).
- Public-share-links UI in config page: list with status badges,
  create form, copy URL, revoke.

### Fixed (code-review #2)
- Admin endpoints now require `Policies.RequiresElevation` — any user
  could mint public-share URLs / list other admins' tokens / mint peer
  keys with the old class-level `[Authorize]` only.
- `PublicShareStore.TryConsume` is now a single `UPDATE … WHERE
  used_count < max_uses … RETURNING item_id` atomic statement +
  busy_timeout 10s on the connection — fixes the DEFERRED-tx race
  where concurrent callers each passed the cap check before either
  committed, and the `database is locked` throws under contention.
- `PublicStream` no longer consumes a use per HTTP Range request —
  consumption moved to viewer-page load (`PublicViewer`), stream
  endpoint validates token existence + expiry only. Seeking no longer
  exhausts MaxUses.
- `PushInvalidationService` dropped ItemUpdated subscription — metadata
  refreshes no longer flood peers with invalidations.
- `PushInvalidationService` CAS now checks return value — no more
  double-fire when an event lands between read and clear.
- `ScheduleWindow` honours an admin-configured IANA TZ
  (`ShareKey.ScheduleTimeZoneId`) — was silently UTC inside Docker.
- `MaxOfficialRating` filter has `StrictUnknownRating` option for
  childproofing — unknown / regional ratings no longer fall open
  silently when this is true.
- `LocalCatalogDigest` content-filter signatures honour the strict flag
  in both Compute() and List().
- `PullWatchStateAsync` merge no longer rewrites `LastPlayedDate`
  backward when peer reports Played=true with an older timestamp.
  Played carries OR semantics (never demotes); LastPlayedDate is
  monotonic max.
- `Stream` and `ProxyImage` validate `itemId` as a Guid + URL-escape
  before substituting into upstream URL — prevents `..` / `/`
  traversal in the proxied path.
- `FetchUserDataAsync` paginated against TotalRecordCount + buffer-based
  yield to keep dispose in finally — fixes the truncation bug
  re-introduced from the original FetchItemsAsync (which was already
  fixed in pass #1) and the resp/doc leak on consumer abort.
- `FederationStatsService.DedupRatio` math fixed — denominator now uses
  TMDB-bearing rows only via `CountTmdbRowsAndDistinct`. Previously
  inflated by items lacking TMDB ids.
- `FederationStatsService.Build` snapshots `config.RemoteServers` via
  `ToArray()` before iteration — prevents `InvalidOperationException`
  on concurrent admin config-save.
- Stored XSS holes in admin UI closed: `esc()` helper applied to
  `ItemName`, `Label`, peer `Name`, share token interpolations.

### Notes
- Plugin DLL targets `net8.0` (Jellyfin 10.10 ABI `10.10.0.0`).
- Public share viewer only supports direct-play codecs from the browser
  (`<video>` tag). Transcoding-on-anonymous-link deferred.
- Push retry on transient failure deferred — gossip-pull is the
  fallback path and runs on the scheduled interval.
