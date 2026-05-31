# Connecting servers — peers, friends & invites

This is the friendly guide to getting two (or more) Jellyfin servers talking.
For endpoint signatures see [protocol.md](./protocol.md); for the friend-of-a-friend
flow see [introductions.md](./introductions.md).

---

## The two building blocks

**Peer**
A server you pull catalog metadata from. For each peer you store:
- their **base URL**,
- the **share key** *they* gave you (grants catalog access, scoped by them),
- a **stream API key** on their server — auto-provisioned from the share key when they allow it,
  so you don't have to create and paste a second token.

**Share key**
A scoped secret *you* mint and hand out. It controls exactly what a holder may see:

| Scope | What it limits |
|-------|----------------|
| Libraries | which of your libraries are visible |
| Allowed hours | a daily time window (server TZ or IANA id) |
| Blocked tags | hide items carrying these tags |
| Rating cap | maximum official rating (e.g. `PG-13`) |
| Bound peer URL | the key only works when presented by that one server (anti-theft) |

Mint as many as you like, one per friend, and **revoke** any of them instantly from the dashboard.
Revoking also deletes the stream API key it provisioned.

---

## Four ways to connect

Pick the one that matches how much trust already exists.

### 1. Manual (you already swapped a key)

The simplest case — you and the other admin exchanged a share key over Signal/email.

```
1. They mint a share key, send it to you.
2. You: Add peer → paste URL + key.   Done — their library starts syncing.
3. (Optional) You mint a key back so the link is two-way.
```

### 2. Friend request — *"please let me into your library"*

You want access to a server you don't have a key for yet. You ask; their admin approves.

```
  YOU                                   PEER (host)
   │  RequestAccess (your URL, nonce)    │
   ├────────────────────────────────────►│  shows up as a pending request
   │                                      │  ── admin clicks Approve ──┐
   │            AccessGranted (key)       │                            │
   │◄─────────────────────────────────────  mints a scoped key for you ┘
   │  peer added automatically            │
```

- The host sees your request in **Pending → Access requests** and approves or denies it.
- On approval their server mints a key bound to *your* URL and calls you back; the peer is added
  on your side automatically.
- If you both set **mutual**, you each end up with a key for the other in one exchange.
- Gated by: an optional invite token, an allowlist, or "accept open requests" — plus a per-IP
  rate limit so the endpoint can't be spammed.

### 3. Invite — *"here's a key, come federate with me"*

The host starts. They pre-mint a key and push it to a target server.

```
  HOST                                   TARGET
   │  InviteOffer (key included, nonce)   │
   ├────────────────────────────────────►│  shows up as a pending invite
   │                                      │  ── admin clicks Accept ──┐
   │           InviteAccepted             │                           │
   │◄─────────────────────────────────────  adds the host as a peer ──┘
```

- The target sees it under **Pending → Invites** and accepts or declines.
- Accepting adds the host as a peer using the included key; if **mutual**, the target also mints
  a key back in the same step.

### 4. Introduction — *"meet my friend"*

A mutual friend connects two servers that don't know each other, **without** anyone forwarding a
raw key by hand. Three roles: **issuer** (mints), **introducer** (the middleman), **receiver**
(the new friend).

```
   B introduces C to A:
   B ──"please mint a key for C"──►  A        (A = issuer, B = introducer, C = receiver)
   A mints a scoped key, B forwards the offer to C, C's admin approves.
```

Trust is opt-in at every hop: the introducer's key must carry `CanRequestIntroductions`, the
issuer chooses `Reject / Request / AutoAccept` per key, hops can be capped, and revoking an
introduction can **cascade** to every key minted downstream. Full details and the trust matrix
live in [introductions.md](./introductions.md).

---

## Safety rails (all methods)

- **Admin approval by default.** Every inbound add lands in a pending queue. Auto-accept is opt-in
  per key and per node.
- **SSRF-guarded.** A proposed peer URL is rejected if it points at link-local, loopback-as-peer,
  or cloud-metadata (`169.254.169.254`) addresses, before it's ever stored or called.
- **No credential leaks on redirect.** Peer calls don't follow 3xx redirects, so your share key /
  Basic creds can't be bounced to an attacker-controlled host.
- **Rate limited.** Open inbound endpoints are throttled per IP.
- **Blocklist.** Block a URL and all future requests/introductions for it are refused regardless
  of who vouches for it.

---

## Quick reference

| I want to… | Do this |
|------------|---------|
| Add a peer I have a key for | **Add peer** → URL + share key |
| Get into a server I don't have a key for | **Request access** (method 2) |
| Bring a server in with a key I issue | **Send invite** (method 3) |
| Connect two friends who don't know each other | **Introduce** (method 4) |
| Stop sharing with someone | **Revoke** their share key |
| Permanently refuse a server | **Block** its URL |
