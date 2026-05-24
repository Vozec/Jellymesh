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

### Notes
- Plugin DLL targets `net8.0` (Jellyfin 10.10 ABI `10.10.0.0`).
- Push-based catalog invalidation deferred — needs a stable peer identity
  scheme first.
- Pull-direction watch state sync deferred — would piggyback gossip round.
