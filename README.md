# Jellyfin Federation Plugin

Federate multiple Jellyfin servers into a unified library. Share, dedupe, and offer multiple versions of the same media across friends' servers.

## Status

Early scaffold. Not functional yet.

## Target

- Jellyfin 10.10+ (ABI `10.10.0.0`)
- .NET 8.0

## Core features (MVP)

1. **Remote server registry** — register N peer Jellyfin servers with URL + API key + user mapping.
2. **Library sync** — scheduled task pulls remote item catalog via `/Items?Recursive=true&Fields=ProviderIds,MediaSources,MediaStreams,Path,Width,Height,Container,Bitrate,RunTimeTicks`.
3. **Local cache** — SQLite store: `RemoteItems(ServerId, RemoteItemId, TmdbId, ImdbId, TvdbId, Title, Year, Type, MediaSourceJson, LastSeen)`.
4. **Dedup matcher** — fuzzy match local↔remote via `ProviderIds` (TMDB > IMDB > TVDB > title+year fallback).
5. **MediaSource injection** — `IMediaSourceProvider` adds remote sources to local items. Jellyfin's built-in version picker handles UI ("Play 1080p / 4K HDR / Remux").
6. **Remote-only virtual library** — items present on peers but not locally appear in a dedicated `Friends Library` virtual folder (via `ILibraryPostScanTask` injecting placeholder `BaseItem`s).
7. **Streaming proxy** — local API endpoint `/Federation/Stream/{server}/{itemId}` that proxies remote HLS/transcode requests, injecting the remote `X-Emby-Token` server-side so API keys never leak to clients.

## Extended features (proposed)

### Quality of life

8. **Federated search** — single search bar searches local + all peers, results tagged with origin server badge.
9. **Watch state sync** — bidirectional `PlaybackPositionTicks` and `Played` flag sync per matched item (via `IUserDataManager` hooks + reverse API push).
10. **Unified watchlist / favorites** — favoriting an item on the federation marks it across peers if the same TMDB id exists.
11. **Continue watching merge** — `Resume` row aggregates progress from all peers, deduped by matched id (latest timestamp wins).
12. **Cross-peer recommendations** — "Your friend just added X" + "Trending on the federation this week" rows on home screen (via custom `IHomeScreenSection` if Jellyfin exposes it, otherwise REST endpoint consumed by custom web layer).

### Library management

13. **Quality-aware version sort** — when same movie has 4K HDR Remux + 1080p WEB-DL across peers, show in resolution-desc order; auto-select highest quality client can play.
14. **Per-peer library scoping** — choose which libraries to share with which peer (e.g., share Movies but not personal home videos). Maps to user-level Jellyfin library access on the remote side.
15. **Request system** — "I don't have this, ask peer to add" button → posts notification to peer's plugin UI / pushes to *arr stack if integrated.
16. **Conflict resolver** — when same `TmdbId` has mismatched metadata between peers (different posters, descriptions), pick canonical source by priority rule (newest scrape / specific peer / TMDB direct refresh).

### Subtitles & audio

17. **Subtitle federation** — even if streaming local version, pull subtitle tracks from peers' versions of the same item (handy when a peer has French subs you lack).
18. **Audio track federation** — same idea, expose remote audio tracks as selectable.

### Trust & control

19. **Bandwidth ceiling per peer** — cap upstream serving to peer (e.g., 10 Mbps so your own viewing isn't starved). Enforced via transcoding bitrate cap on outbound `/Federation/Stream`.
20. **Schedule windows** — peer allowed to stream only during defined hours (e.g., 18:00–02:00).
21. **Content filters** — block specific tags/genres/ratings per peer (e.g., hide R-rated from a peer with kid accounts).
22. **Audit log** — what each peer streamed from you, when, bytes transferred. Stored in plugin SQLite, exportable.

### Resilience

23. **Health monitor** — periodic ping each peer, dashboard shows online/offline/degraded; auto-disable remote sources when peer offline so playback doesn't fail mid-pick.
24. **Source fallback chain** — if peer A's 4K source fails to start within N seconds, auto-fallback to peer B's 1080p.
25. **Stream pre-warming** — when a user picks a remote source, pre-roll a small range request to wake the remote transcoder before client connects fully.

### Discovery

26. **Federation stats dashboard** — total federated library size, dedup ratio, top contributors, watch hours per peer.
27. **"What does my friend have that I don't"** — diff view per peer.
28. **Cross-instance Trakt/AniList sync** — single Trakt account aggregates scrobbles from federated playback regardless of which peer served the bytes.

### Advanced

29. **Mesh topology** — peer can transitively share peer's peers (A↔B, B↔C → A sees C's catalog through B as relay). Optional, off by default (trust model implications).
30. **End-to-end encrypted peer link** — WireGuard/Tailscale-style identity instead of bearer token, peer trust via pubkey rotation.
31. **WebRTC P2P streaming** — bypass server-to-server proxy, browser fetches directly from peer's Jellyfin via authenticated WebRTC data channel (lower latency, less load on local server). Future, requires client-side companion.

## Architecture

![Architecture](docs/diagrams/architecture-1.png)

See [docs/architecture.md](docs/architecture.md), [docs/protocol.md](docs/protocol.md), and [docs/sync-flow.md](docs/sync-flow.md) for full details. Regenerate diagrams with `bash docs/render-diagrams.sh`.

<details><summary>ASCII fallback</summary>

```
┌──────────────────────────────────────────────────────────────┐
│ Local Jellyfin                                               │
│                                                              │
│  ┌────────────────────────┐    ┌───────────────────────────┐ │
│  │ Federation Plugin      │    │ Jellyfin core             │ │
│  │                        │    │                           │ │
│  │  ┌──────────────────┐  │    │  ┌────────────────────┐   │ │
│  │  │ ScheduledTask    │──┼────┼─▶│ Library / Items DB │   │ │
│  │  │ (periodic sync)  │  │    │  └────────────────────┘   │ │
│  │  └──────────────────┘  │    │                           │ │
│  │           │            │    │  ┌────────────────────┐   │ │
│  │           ▼            │    │  │ MediaSourceManager │◀──┼─┼─┐
│  │  ┌──────────────────┐  │    │  └────────────────────┘   │ │ │
│  │  │ Remote cache     │  │    │                           │ │ │
│  │  │ (SQLite)         │  │    └───────────────────────────┘ │ │
│  │  └──────────────────┘  │                                  │ │
│  │           │            │                                  │ │
│  │           ▼            │                                  │ │
│  │  ┌──────────────────┐  │                                  │ │
│  │  │ Matcher          │  │                                  │ │
│  │  │ (TMDB/IMDB)      │  │                                  │ │
│  │  └──────────────────┘  │                                  │ │
│  │           │            │                                  │ │
│  │           ▼            │                                  │ │
│  │  ┌──────────────────┐  │                                  │ │
│  │  │IMediaSourceProvi.│──┼──────────────────────────────────┘ │
│  │  └──────────────────┘  │                                    │
│  │                        │                                    │
│  │  ┌──────────────────┐  │                                    │
│  │  │ Stream proxy API │◀─┼──── client HLS request             │
│  │  └──────────────────┘  │                                    │
│  │           │            │                                    │
│  └───────────┼────────────┘                                    │
└──────────────┼─────────────────────────────────────────────────┘
               │ HTTPS + X-Emby-Token
               ▼
        ┌─────────────────┐
        │ Peer Jellyfin   │
        └─────────────────┘
```

</details>

## Build

```sh
dotnet build src/Jellyfin.Plugin.Federation/Jellyfin.Plugin.Federation.csproj -c Release
```

Output DLL → drop into `<jellyfin-config>/plugins/Federation_0.1.0.0/`.

## Roadmap

- [ ] M1: scaffold + config UI + remote server CRUD
- [ ] M2: API client + scheduled sync + SQLite cache
- [ ] M3: matcher + `IMediaSourceProvider` injection
- [ ] M4: streaming proxy endpoint
- [ ] M5: remote-only virtual library
- [ ] M6: watch state sync
- [ ] M7: federated search + dashboard
- [ ] M8+: extended features

## License

GPL-2.0 (Jellyfin-compatible).
