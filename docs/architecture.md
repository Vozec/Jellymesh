# Architecture

## High-level

```mermaid
graph TB
    subgraph LOCAL[Local Jellyfin instance]
        direction TB
        JF_CORE[Jellyfin core<br/>ItemRepository / MediaSourceManager / LibraryManager]
        subgraph PLUGIN[Federation Plugin]
            SYNC[FederationSyncTask<br/>periodic gossip + delta]
            HEALTH[HealthMonitorService<br/>peer ping every 30s]
            WATCH[WatchStateSyncService<br/>UserDataSaved hook]
            CHANNEL[FriendsLibraryChannel<br/>IChannel impl]
            MSP[FederatedMediaSourceProvider<br/>IMediaSourceProvider]
            API[FederationController<br/>REST endpoints]
            STORE[(RemoteItemStore<br/>SQLite cache)]
            DIGEST[LocalCatalogDigest<br/>SHA-256 over library]
        end
        JF_CORE -- queries --> DIGEST
        JF_CORE -- registers --> CHANNEL
        JF_CORE -- registers --> MSP
        WATCH -. subscribe .-> JF_CORE
        SYNC --> STORE
        SYNC --> HEALTH
        CHANNEL --> STORE
        CHANNEL --> HEALTH
        MSP --> STORE
        MSP --> HEALTH
        API --> STORE
        API --> DIGEST
    end

    PEER1[Peer Jellyfin A<br/>same plugin]
    PEER2[Peer Jellyfin B<br/>same plugin]

    SYNC <-- digest + items --> PEER1
    SYNC <-- digest + items --> PEER2
    API -- /Stream proxy --> PEER1
    API -- /Stream proxy --> PEER2
    WATCH -- POST UserData --> PEER1
    WATCH -- POST UserData --> PEER2
```

## Key components

| Component | Responsibility |
|-----------|----------------|
| `FederationSyncTask` | Periodic gossip handshake → conditional delta pull → deletion detection |
| `HealthMonitorService` | 30 s ping rotation, raises `HealthChanged` on flip |
| `PeerHealthRegistry` | In-memory state + signature hash for cache invalidation |
| `LocalCatalogDigest` | Computes SHA-256 over local items, optionally scoped to a library set |
| `RemoteItemStore` | SQLite cache: `remote_items`, `peer_digests`, `stream_audit` |
| `FederatedMediaSourceProvider` | Injects peer sources into matched local items (TMDB/IMDB id lookup) |
| `FriendsLibraryChannel` | `IChannel` surfacing peer-only items in a virtual library row |
| `WatchStateSyncService` | Pushes local progress/played to peers on `UserDataSaved` |
| `FederationController` | REST: stream proxy, search, audit, digest, items, shares |
| `ThrottledStream` | Read-side bandwidth cap on outbound proxied streams |

See also: [protocol.md](./protocol.md) for wire format, [sync-flow.md](./sync-flow.md) for sequence.
