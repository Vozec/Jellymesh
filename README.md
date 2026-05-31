<div align="center">

<img src="docs/logo.svg" alt="Jellymesh" width="160" onerror="this.style.display='none'" />

# Jellymesh

**Federate multiple Jellyfin servers into one shared library.**

Your friends' films show up in *your* Jellyfin — deduped by TMDB, streamed on demand,
with watch state synced both ways. No copying, no re-uploading, no second account.

<p>
<a href="https://github.com/Vozec/Jellymesh/actions/workflows/build.yml"><img src="https://github.com/Vozec/Jellymesh/actions/workflows/build.yml/badge.svg" alt="Build" /></a>
<img src="https://img.shields.io/badge/Jellyfin-10.10%20%7C%2010.11-00A4DC" alt="Jellyfin 10.10 / 10.11" />
<img src="https://img.shields.io/badge/license-GPL--2.0-blue" alt="GPL-2.0" />
<img src="https://img.shields.io/badge/status-pre--1.0%20RC-orange" alt="Pre-1.0 RC" />
</p>

[Install](#-install) · [How it works](#-how-it-works) · [Connecting servers](#-connecting-servers) · [Docs](#-documentation)

</div>

---

## ✨ What it does

```
        my Jellyfin                            Alice's Jellyfin
   ┌──────────────────────┐               ┌──────────────────────┐
   │  my local library    │    gossip     │  Alice's library     │
   │  + Alice's 4K films  │◄─────────────►│  + my films appear   │
   │  + Bob's anime       │   (metadata   │    in her rows       │
   │  + watch state sync  │    only)      │                      │
   └──────────────────────┘               └──────────────────────┘
            │  ▲                                      ▲
            │  └────────── push invalidation ─────────┘
            │
            └── anonymous share link ──► a friend's browser (no account)
```

The plugin runs **inside** Jellyfin. It gossips catalog metadata with peers, injects their
matching copies as extra **versions** of your local items (via `IMediaSourceProvider`), and
reverse-proxies the actual bytes on playback so **peer credentials never reach your users**.

| | |
|--:|---|
| 🔄 **Catalog sync** | gossip digest, deletion detection, delta-only pulls |
| 🎬 **Multi-version playback** | the same film on several peers shows up as version picker entries |
| 🧬 **Dedup matching** | TMDB → IMDB → TVDB → title+year fallback |
| 📡 **Push invalidation** | local add/remove debounced, retried with capped backoff |
| 👀 **Watch state** | pushed on save, pulled on sync, loop-broken both ways |
| 🔐 **Stream proxy** | peer's token stays server-side, bandwidth cap, audit log, mTLS-ready |
| 🔑 **Scoped share keys** | per-library, allowed-hours, blocked tags, rating cap, peer-bound |
| 🔗 **Anonymous links** | share one video by expiring, use-capped link — no login |
| 🤝 **Friend requests & invites** | handshake to add a peer without exchanging keys by hand |
| 🌐 **Introductions** | introduce a friend to a friend, with hop caps and cascade revoke |
| 📊 **Dashboard & diagnostics** | online peers, dedup ratio, top streams, one-click peer probes |

---

## 🧠 How it works

1. **Gossip, not copy.** Each server publishes a signed catalog *digest*. Peers pull only the
   delta when the digest changes — metadata moves, media files never do.
2. **Match & merge.** Incoming peer items are matched to your local library by provider id.
   A match becomes an extra playable *version*; an unmatched item appears as a federated row.
3. **Proxy on play.** When a user plays a peer version, your server fetches the bytes from the
   peer with *its* token and streams them through — your users only ever see your server.

Deep dives: [architecture](docs/architecture.md) · [wire protocol](docs/protocol.md) · [sync flow](docs/sync-flow.md)

---

## 🤝 Connecting servers

Federation is built on two primitives:

- **Peer** — a server you pull catalog from. You hold *their* **share key** (catalog access) and,
  for streaming, an API key on their server (auto-provisioned from the share key when allowed).
- **Share key** — a scoped secret *you* mint and hand out. It decides which libraries, hours,
  ratings and tags a holder may see. Revoke it any time.

There are four ways to connect — pick by how much trust you already have:

| Method | Who starts | Best for | Doc |
|--------|-----------|----------|-----|
| **Manual** | both admins | you already swapped a key out-of-band | [quick start](#-quick-start) |
| **Friend request** | the asker | "let me into your library" | [connecting](docs/connecting.md) |
| **Invite** | the host | "here's a key, come federate with me" | [connecting](docs/connecting.md) |
| **Introduction** | a mutual friend | friend-of-a-friend, no key sharing | [introductions](docs/introductions.md) |

```
Friend request:   you ──"may I?"──►  peer ──(admin approves)──►  mints you a key
Invite:           you ──"here you go, key included"──►  peer ──(admin accepts)──►  added
Introduction:     B ──"meet C"──►  A   A mints a key for C   (A,B,C each opt in)
```

Every inbound add is **admin-approved by default**, rate-limited, SSRF-guarded, and refuses
link-local / cloud-metadata targets. Auto-accept is opt-in per key and per node. See
[docs/connecting.md](docs/connecting.md) for the full friend / invite / introduction walkthrough.

---

## 📦 Install

**From a release**

1. Download the latest `Federation_*.zip` from [Releases](https://github.com/Vozec/Jellymesh/releases).
2. Unzip into your Jellyfin plugins folder:
   ```sh
   unzip Federation_*.zip -d <jellyfin-config>/plugins/Federation/
   sudo systemctl restart jellyfin
   ```
3. Open **Dashboard → Plugins → Federation**.

**From source**

```sh
git clone git@github.com:Vozec/Jellymesh.git
cd Jellymesh
bash build/package.sh
unzip build/output/Federation_*.zip -d <jellyfin-config>/plugins/Federation/
sudo systemctl restart jellyfin
```

> Requires Jellyfin **10.10.x** or **10.11.x** (.NET 8 / .NET 9 builds are produced per ABI).

---

## 🚀 Quick start

1. **Set your public base URL** (top of the config). Peers use it to call you back for push
   invalidation, introductions, and handshakes.
2. **Add a peer.** Paste their Jellyfin URL and the federation **share key** they gave you.
   The stream API key is auto-provisioned from that share key — no second secret to paste.
   *(HTTP Basic credentials are optional, for peers behind a reverse proxy that requires it.)*
3. **Mint a key for a friend.** Choose libraries, allowed hours (server TZ or IANA id), blocked
   tags and a rating cap, optionally bind it to their URL. Hand them the key — that's it.

Prefer not to swap keys by hand? Use a **friend request** or **invite** instead — see
[docs/connecting.md](docs/connecting.md).

---

## 🔒 Security model

- Peer-bound calls run through one hardened HTTP client: **SSRF allowlist**, **no credential-
  following redirects**, optional **mutual TLS** with per-peer client certs and private-CA pinning.
- Inbound peer additions are gated by `IsSafePeerBaseUrl` (rejects link-local / `169.254.169.254`
  / loopback-as-peer), admin approval, and per-IP rate limits.
- Stream tokens are short-lived HMAC, constant-time compared; share keys never leave the server
  in playback URLs.

---

## 📚 Documentation

| Doc | What's in it |
|-----|--------------|
| [docs/connecting.md](docs/connecting.md) | Peers, share keys, friend requests, invites, introductions |
| [docs/architecture.md](docs/architecture.md) | Services, stores, Jellyfin integration points |
| [docs/protocol.md](docs/protocol.md) | REST endpoints and wire format |
| [docs/sync-flow.md](docs/sync-flow.md) | Gossip digest → delta pull → merge timeline |
| [docs/introductions.md](docs/introductions.md) | Delegated key issuance, trust model, cascade revoke |

---

## 🛠️ Build & test

```sh
dotnet build src/Jellyfin.Plugin.Federation/Jellyfin.Plugin.Federation.csproj -c Release
bash build/test.sh
```

CI runs the same on every push: build, test with coverage, format check, package the zip, render diagrams.

---

## 📄 License

[GPL-2.0](LICENSE) — Jellyfin-compatible.
