using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api;

[ApiController]
[Authorize]
[Route("Federation")]
public class FederationController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Services.RemoteItemStore _store;
    private readonly ILogger<FederationController> _logger;

    public FederationController(IHttpClientFactory httpClientFactory, Services.RemoteItemStore store, ILogger<FederationController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    [HttpGet("Stream/{serverId}/{itemId}")]
    public async Task<IActionResult> Stream(string serverId, string itemId, [FromQuery] string? sourceId, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return NotFound();
        // Item ids on Jellyfin are GUIDs. Reject anything else so `..` or `/` can't traverse
        // the peer's URL space under our auth context.
        if (!Guid.TryParseExact(itemId, "N", out _) && !Guid.TryParse(itemId, out _)) return BadRequest();

        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        Services.RemoteJellyfinClient.AddBasicAuth(http, server);
        var upstream = $"{server.BaseUrl.TrimEnd('/')}/Videos/{Uri.EscapeDataString(itemId)}/stream?static=true";
        if (!string.IsNullOrEmpty(sourceId))
            upstream += $"&MediaSourceId={Uri.EscapeDataString(sourceId)}";

        var req = new HttpRequestMessage(HttpMethod.Get, upstream);
        req.Headers.Add("X-Emby-Token", server.ApiKey);

        if (Request.Headers.TryGetValue("Range", out var range))
            req.Headers.TryAddWithoutValidation("Range", (string)range!);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        Response.StatusCode = (int)resp.StatusCode;
        CopySafeHeaders(resp.Headers, Response.Headers);
        CopySafeHeaders(resp.Content.Headers, Response.Headers);
        // When we'll throttle and re-meter the body, the upstream Content-Length no longer
        // matches what we'll write. Let Kestrel set Content-Length (or chunk) based on actual bytes.
        if (config.OutboundBitrateCapBps > 0) Response.Headers.Remove("content-length");

        // Audit row is best-effort - if SQLite is locked we still serve the stream.
        var auditId = -1L;
        try { auditId = _store.BeginAudit(server.Id, itemId, User?.Identity?.Name); }
        catch (Exception ex) { _logger.LogWarning(ex, "BeginAudit failed for {Peer} {Item}; serving stream without audit row", server.Id, itemId); }

        var bytesServed = 0L;
        try
        {
            var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // ThrottledStream owns disposing src (its Dispose forwards). Don't await using src
            // separately or src is disposed twice.
            var cap = config.OutboundBitrateCapBps;
            await using Stream throttled = cap > 0 ? new Services.ThrottledStream(src, cap / 8) : src;

            var buf = new byte[81920];
            int n;
            while ((n = await throttled.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await Response.Body.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                bytesServed += n;
            }
        }
        finally
        {
            if (auditId > 0)
            {
                try { _store.CompleteAudit(auditId, bytesServed); }
                catch (Exception ex) { _logger.LogWarning(ex, "CompleteAudit failed for {AuditId}", auditId); }
            }
        }
        return new EmptyResult();
    }

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "trailers", "transfer-encoding", "upgrade",
        "server", // we shouldn't impersonate upstream's server header
    };

    private static void CopySafeHeaders(System.Net.Http.Headers.HttpHeaders src, IHeaderDictionary dst)
    {
        foreach (var h in src)
        {
            if (HopByHopHeaders.Contains(h.Key)) continue;
            dst[h.Key] = h.Value.ToArray();
        }
    }

    // === Request system ===

    [AllowAnonymous]
    [HttpPost("Request")]
    public IActionResult ReceiveRequest([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] IncomingRequestPayload payload,
        [FromServices] Services.RequestStore requests,
        [FromServices] Services.InboundAuditStore audit,
        [FromServices] Services.QuotaService quota)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (payload is null) return BadRequest("missing body");

        // Trim and reject whitespace-only - IsNullOrWhiteSpace catches "   " that IsNullOrEmpty
        // would let through, preventing a peer from consuming pending-uniq slots with garbage.
        var tmdb = string.IsNullOrWhiteSpace(payload.TmdbId) ? null : payload.TmdbId.Trim();
        var imdb = string.IsNullOrWhiteSpace(payload.ImdbId) ? null : payload.ImdbId.Trim();
        var title = string.IsNullOrWhiteSpace(payload.Title) ? null : payload.Title.Trim();
        var note = string.IsNullOrWhiteSpace(payload.Note) ? null : payload.Note.Trim();
        if (tmdb is null && imdb is null && title is null)
            return BadRequest("need at least one of TmdbId, ImdbId, Title");

        // Cap field lengths to prevent a (possibly compromised) share-key holder from filling
        // the requests DB with multi-MB strings.
        if (tmdb is { Length: > 64 } || imdb is { Length: > 64 }) return BadRequest("id too long");
        if (title is { Length: > 512 }) return BadRequest("title too long");
        if (note is { Length: > 2048 }) return BadRequest("note too long");

        // Identity preference order: ShareKey.BoundPeerUrl (anti-spoof) > payload.FromBaseUrl
        // (legacy / unbound keys). Bound keys make payload.FromBaseUrl a UI hint only.
        var attributedUrl = !string.IsNullOrEmpty(key.BoundPeerUrl) ? key.BoundPeerUrl : payload.FromBaseUrl;

        var config = Plugin.Instance?.Configuration;
        Configuration.RemoteServer? peerMatch = null;
        Guid? peerId = null;
        if (config is not null && !string.IsNullOrEmpty(attributedUrl))
        {
            peerMatch = config.RemoteServers.FirstOrDefault(s => Services.PeerUrl.SameHost(s.BaseUrl, attributedUrl));
            peerId = peerMatch?.Id;
        }

        var qd = quota.CheckInbound(peerMatch);
        if (!qd.Allowed)
        {
            audit.Record("content-request", "throttled", peerUrl: attributedUrl, peerId: peerId, reason: qd.Reason);
            if (qd.RetryAfterSeconds.HasValue) Response.Headers["Retry-After"] = qd.RetryAfterSeconds.Value.ToString();
            return StatusCode(429, new { reason = qd.Reason });
        }
        audit.Record("content-request", "ok", peerUrl: attributedUrl, peerId: peerId);

        var id = requests.Insert(new Services.FederationRequest
        {
            Direction = "in",
            PeerId = peerId,
            PeerUrl = attributedUrl,
            TmdbId = tmdb,
            ImdbId = imdb,
            Title = title,
            Year = payload.Year,
            Note = note
        });
        // Null id = uniq_inbound_pending kicked in. Log for debugging but still respond 204
        // (idempotent semantics: "we got your request, already on our list").
        if (id is null) _logger.LogDebug("ReceiveRequest deduped from peer {Url} tmdb={Tmdb} imdb={Imdb} title={Title}", attributedUrl, tmdb, imdb, title);
        return NoContent();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("SendRequest")]
    public async Task<IActionResult> SendRequest([FromBody] SendRequestPayload payload,
        [FromServices] Services.RequestStore requests,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        if (payload is null || !Guid.TryParse(payload.PeerId, out var peerId)) return BadRequest();
        var config = Plugin.Instance?.Configuration;
        var peer = config?.RemoteServers.FirstOrDefault(s => s.Id == peerId);
        if (peer is null) return NotFound("Unknown peer");
        if (string.IsNullOrEmpty(peer.FederationShareKey)) return BadRequest("Peer has no FederationShareKey configured for us");
        if (string.IsNullOrEmpty(config!.PublicBaseUrl)) return BadRequest("PublicBaseUrl not set - peer wouldn't be able to identify us");

        var ok = await client.SendRequestAsync(peer, config.PublicBaseUrl, payload.TmdbId, payload.ImdbId, payload.Title, payload.Year, payload.Note, ct).ConfigureAwait(false);

        requests.Insert(new Services.FederationRequest
        {
            Direction = "out",
            PeerId = peer.Id,
            PeerUrl = peer.BaseUrl,
            TmdbId = payload.TmdbId,
            ImdbId = payload.ImdbId,
            Title = payload.Title,
            Year = payload.Year,
            Note = payload.Note,
            Status = ok ? "pending" : "send-failed"
        });

        return ok ? Ok(new { sent = true }) : StatusCode(502, "Peer rejected the request");
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Requests/{direction}")]
    public IActionResult ListRequests(string direction, [FromQuery] string? status,
        [FromServices] Services.RequestStore requests)
    {
        if (direction != "in" && direction != "out") return BadRequest("direction must be 'in' or 'out'");
        if (status is not null && !Services.RequestStore.IsValidStatus(status))
            return BadRequest("unknown status");
        return Ok(requests.List(direction, status));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Requests/{id}/Status")]
    public IActionResult SetRequestStatus(long id, [FromQuery] string status,
        [FromServices] Services.RequestStore requests)
    {
        // 'send-failed' included so admin can dismiss / retry a failed outbound row.
        if (!Services.RequestStore.IsValidStatus(status))
            return BadRequest("status must be pending|accepted|declined|dismissed|send-failed");
        return requests.UpdateStatus(id, status) ? NoContent() : NotFound();
    }

    // === Introductions ===

    [AllowAnonymous]
    [HttpPost("Introduce")]
    public IActionResult ReceiveIntroduce([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] IntroducePayload payload,
        [FromServices] Services.IntroductionService intro)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.ForUrl)) return BadRequest("ForUrl required");
        if (payload.ForUrl.Length > 512) return BadRequest("ForUrl too long");
        if (payload.Note is { Length: > 2048 }) return BadRequest("Note too long");
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        if (!config.AcceptInboundIntroductions)
            return StatusCode(403, new { reason = "inbound introductions disabled by admin" });

        var forCanonCheck = Services.PeerUrl.Canonicalize(payload.ForUrl);
        if (forCanonCheck is not null && config.BlockedPeerUrls.Any(u => Services.PeerUrl.SameHost(u, forCanonCheck)))
            return StatusCode(403, new { reason = "target peer is blocked" });

        var result = intro.TryMint(config, key, payload.ForUrl.Trim(), Math.Max(1, payload.HopCount), payload.Note);
        return result.Status switch
        {
            "minted" or "minted-after-pending" or "existing" => Ok(new { result.Status, result.ApiKey, result.OurBaseUrl, result.IntroductionId }),
            "pending" => Accepted(new { result.Status, result.IntroductionId, result.Reason }),
            "self" or "bad-url" or "hop-cap" => BadRequest(new { result.Status, result.Reason }),
            "already-peer" => Conflict(new { result.Status, result.Reason }),
            "rate-limit-hour" or "rate-limit-day" => StatusCode(429, new { result.Status, result.Reason }),
            _ => StatusCode(403, new { result.Status, result.Reason })
        };
    }

    [AllowAnonymous]
    [HttpPost("Introduced")]
    public IActionResult ReceiveIntroduced([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] IntroducedPayload payload,
        [FromServices] Services.IntroductionStore store)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.NewPeerUrl) || string.IsNullOrWhiteSpace(payload.NewPeerKey))
            return BadRequest("NewPeerUrl and NewPeerKey required");
        if (payload.NewPeerUrl.Length > 512 || payload.NewPeerKey.Length > 256) return BadRequest("payload field too long");
        if (payload.IntroducedBy is { Length: > 256 }) return BadRequest("IntroducedBy too long");
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        if (!config.AcceptInboundIntroductions)
            return StatusCode(403, new { reason = "inbound introductions disabled by admin" });

        var newCanon = Services.PeerUrl.Canonicalize(payload.NewPeerUrl);
        if (newCanon is null) return BadRequest("NewPeerUrl must include http:// or https:// scheme");
        if (string.Equals(newCanon, Services.PeerUrl.Canonicalize(config.PublicBaseUrl), StringComparison.Ordinal))
            return BadRequest("would introduce ourselves");
        if (config.BlockedPeerUrls.Any(u => Services.PeerUrl.SameHost(u, newCanon)))
            return StatusCode(403, new { reason = "peer is blocked (introductions for this URL refused regardless of introducer)" });
        if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, newCanon)))
            return Conflict("already a peer");

        // Always queue for admin approval - adding a new RemoteServer is high-trust.
        // Per-key auto-accept on receiver could be added later but Request is the safe default.
        var introducerLabel = string.IsNullOrEmpty(payload.IntroducedBy) ? key.Label : payload.IntroducedBy;
        // Encode the proposed key + optional Basic creds in the note. ApproveIntroduction
        // parses them back. Format: "forwarded by 'X' :: KEY=k :: BASIC=user:pass".
        var noteSb = new System.Text.StringBuilder();
        noteSb.Append("forwarded by '").Append(introducerLabel).Append("' :: KEY=").Append(payload.NewPeerKey);
        if (!string.IsNullOrEmpty(payload.BasicAuthUser) || !string.IsNullOrEmpty(payload.BasicAuthPass))
        {
            noteSb.Append(" :: BASIC=").Append(payload.BasicAuthUser).Append(':').Append(payload.BasicAuthPass);
        }
        var note = noteSb.ToString();
        var id = store.InsertPending("receiver", newCanon, key.Id, Math.Max(1, payload.HopCount), note);
        _logger.LogInformation("Received introduction for {Url} forwarded by {Introducer} - pending admin approval (id {Id})", newCanon, introducerLabel, id);
        return Accepted(new { introductionId = id, status = "pending-approval" });
    }

    [AllowAnonymous]
    [HttpPost("RequestReciprocalKey")]
    public IActionResult ReceiveReciprocityRequest([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] ReciprocityRequestPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.FromBaseUrl)) return BadRequest("FromBaseUrl required");
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        if (config.Reciprocity != Configuration.ReciprocityMode.AutoAcceptReciprocal)
            return StatusCode(403, new { reason = "this node does not auto-accept reciprocal keys; manual exchange required" });

        // Anti-spoof: the calling key MUST be bound. Without bound URL, any share-key holder
        // could claim any FromBaseUrl and get a free reciprocal key. Reject unbound keys
        // outright when reciprocity is automated.
        if (string.IsNullOrEmpty(key.BoundPeerUrl))
            return StatusCode(403, "reciprocal-key requests require a bound share key for anti-spoof");
        if (!Services.PeerUrl.SameHost(key.BoundPeerUrl, payload.FromBaseUrl))
            return StatusCode(403, "FromBaseUrl does not match bound peer URL of the presenting key");

        var fromCanon = Services.PeerUrl.Canonicalize(payload.FromBaseUrl);
        if (fromCanon is null) return BadRequest("FromBaseUrl must include http:// or https:// scheme");

        var tpl = config.ReciprocityTemplate;
        lock (Plugin.ConfigWriteLock)
        {
            // Dedup: returning the same key on retry prevents the Shares list from growing
            // unboundedly when a peer re-requests reciprocity.
            var existing = config.Shares.FirstOrDefault(s =>
                s.Enabled && s.IssuedForUrl == fromCanon);
            if (existing is not null)
                return Ok(new { existing.ApiKey, OurBaseUrl = Services.PeerUrl.Canonicalize(config.PublicBaseUrl) });

            var newKey = new Configuration.ShareKey
            {
                ApiKey = GenerateApiKey(),
                Label = $"Reciprocal to {fromCanon}",
                BoundPeerUrl = fromCanon,
                IssuedForUrl = fromCanon,
                LibraryIds = tpl.LibraryIds.ToList(),
                BlockedTags = tpl.BlockedTags.ToList(),
                MaxOfficialRating = tpl.MaxOfficialRating,
                StrictUnknownRating = tpl.StrictUnknownRating,
                CanRequestIntroductions = false,
                MintMode = Configuration.IntroductionMintMode.Reject,
                Enabled = true
            };
            config.Shares.Add(newKey);
            Plugin.Instance?.SaveConfiguration();
            _logger.LogInformation("Auto-minted reciprocal key for {Url}", fromCanon);
            return Ok(new { newKey.ApiKey, OurBaseUrl = Services.PeerUrl.Canonicalize(config.PublicBaseUrl) });
        }
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("IntroducePeer")]
    public async Task<IActionResult> IntroducePeer([FromBody] AdminIntroducePayload payload,
        [FromServices] Services.RemoteJellyfinClient client,
        [FromServices] Services.IntroductionStore store,
        CancellationToken ct)
    {
        if (payload is null || !Guid.TryParse(payload.PeerId, out var peerId) || string.IsNullOrWhiteSpace(payload.ForUrl))
            return BadRequest("PeerId and ForUrl required");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var peer = config.RemoteServers.FirstOrDefault(s => s.Id == peerId);
        if (peer is null) return NotFound("Unknown peer");
        if (string.IsNullOrEmpty(peer.FederationShareKey)) return BadRequest("Peer has no FederationShareKey configured for us");

        // 1. Ask peer to mint.
        var mintResult = await client.CallIntroduceAsync(peer, payload.ForUrl, 1, payload.Note, ct).ConfigureAwait(false);
        if (mintResult is null)
            return StatusCode(502, "peer unreachable for /Introduce call");

        // Audit our outbound action.
        store.InsertActiveOrGet("forwarder", Services.PeerUrl.Canonicalize(payload.ForUrl) ?? payload.ForUrl,
            introducerKeyId: null, issuedKeyId: null, hopCount: 1, note: $"sent to {peer.Name}");

        if (!mintResult.Status.StartsWith("minted") && mintResult.Status != "existing")
            return Ok(new { peer = peer.Name, mintResult });

        // 2. Optionally forward to the receiver.
        if (payload.AlsoForward && !string.IsNullOrEmpty(mintResult.ApiKey) && !string.IsNullOrEmpty(mintResult.OurBaseUrl))
        {
            // We need a key on the receiver to forward through them. If we don't have one,
            // the admin must hand the key over manually.
            var receiver = config.RemoteServers.FirstOrDefault(s => Services.PeerUrl.SameHost(s.BaseUrl, payload.ForUrl));
            if (receiver is not null && !string.IsNullOrEmpty(receiver.FederationShareKey))
            {
                // Forward the issuer's HTTP Basic creds along: if WE need them to reach A,
                // C will need them too.
                var fwd = await client.CallIntroducedAsync(receiver, mintResult.OurBaseUrl, mintResult.ApiKey,
                    peer.BaseUrl, 1, peer.BasicAuthUser, peer.BasicAuthPass, ct).ConfigureAwait(false);
                return Ok(new { peer = peer.Name, mintResult, forwarded = fwd });
            }
            return Ok(new { peer = peer.Name, mintResult, forwardSkipped = "no share key for receiver, hand the key over manually" });
        }
        return Ok(new { peer = peer.Name, mintResult });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Introductions/{role}")]
    public IActionResult ListIntroductions(string role, [FromQuery] string? status,
        [FromServices] Services.IntroductionStore store)
    {
        if (role is not ("issuer" or "forwarder" or "receiver")) return BadRequest("role must be issuer|forwarder|receiver");
        return Ok(store.ListByRole(role, status));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Introductions/{id}/Approve")]
    public IActionResult ApproveIntroduction(long id,
        [FromServices] Services.IntroductionService intro,
        [FromServices] Services.IntroductionStore store)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var pending = store.Get(id);
        if (pending is null) return NotFound();

        if (pending.OurRole == "issuer")
        {
            var result = intro.ApprovePending(config, id);
            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }
        if (pending.OurRole == "receiver")
        {
            // Guard against double-add: the pre-Approve dedup only blocked at queue time;
            // admin may have added the peer manually since, or another receiver row for the
            // same URL got approved first.
            if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, pending.ForUrlCanonical)))
            {
                store.UpdateStatus(id, "revoked");
                return Conflict(new { status = "already-a-peer", reason = "RemoteServer for this URL already exists" });
            }

            var note = pending.Note ?? "";
            // Note format: "forwarded by 'X' :: KEY=k [:: BASIC=user:pass]"
            string? proposedKey = null, basicUser = null, basicPass = null;
            foreach (var part in note.Split(" :: ", StringSplitOptions.None))
            {
                if (part.StartsWith("KEY=", StringComparison.Ordinal))
                    proposedKey = part[4..].Trim();
                else if (part.StartsWith("BASIC=", StringComparison.Ordinal))
                {
                    var creds = part[6..];
                    var colon = creds.IndexOf(':');
                    if (colon >= 0) { basicUser = creds[..colon]; basicPass = creds[(colon + 1)..]; }
                }
            }
            if (string.IsNullOrEmpty(proposedKey)) return BadRequest("malformed pending receiver note");

            lock (Plugin.ConfigWriteLock)
            {
                config.RemoteServers.Add(new Configuration.RemoteServer
                {
                    Name = "Introduced - " + pending.ForUrlCanonical,
                    BaseUrl = pending.ForUrlCanonical,
                    FederationShareKey = proposedKey,
                    BasicAuthUser = basicUser ?? string.Empty,
                    BasicAuthPass = basicPass ?? string.Empty,
                    Enabled = true
                });
                Plugin.Instance?.SaveConfiguration();
            }
            if (!store.Activate(id, Guid.Empty))
            {
                lock (Plugin.ConfigWriteLock)
                {
                    config.RemoteServers.RemoveAll(s => Services.PeerUrl.SameHost(s.BaseUrl, pending.ForUrlCanonical));
                    Plugin.Instance?.SaveConfiguration();
                }
                return Conflict(new { status = "race", reason = "another active introduction for this URL exists; rolled back" });
            }
            return Ok(new { status = "added-to-peers" });
        }
        return BadRequest("cannot approve forwarder-role rows (they're audit only)");
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Introductions/{id}/Revoke")]
    public IActionResult RevokeIntroduction(long id, [FromQuery] bool cascade,
        [FromServices] Services.IntroductionStore store)
    {
        var intro = store.Get(id);
        if (intro is null) return NotFound();
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        lock (Plugin.ConfigWriteLock)
        {
            if (intro.IssuedKeyId.HasValue && intro.IssuedKeyId.Value != Guid.Empty)
                config.Shares.RemoveAll(k => k.Id == intro.IssuedKeyId.Value);

            if (intro.OurRole == "receiver" && !string.IsNullOrEmpty(intro.ForUrlCanonical))
                config.RemoteServers.RemoveAll(s => Services.PeerUrl.SameHost(s.BaseUrl, intro.ForUrlCanonical));
        }
        store.UpdateStatus(id, "revoked");

        var cascaded = 0;
        if (cascade && intro.IssuedKeyId.HasValue && intro.IssuedKeyId.Value != Guid.Empty)
        {
            // BFS walks via includeRevoked=true so a previously-revoked intermediate doesn't
            // truncate descent into still-active grandchildren.
            var toVisit = new System.Collections.Generic.Queue<Guid>();
            var seen = new System.Collections.Generic.HashSet<Guid>();
            toVisit.Enqueue(intro.IssuedKeyId.Value);
            while (toVisit.Count > 0)
            {
                var keyId = toVisit.Dequeue();
                if (!seen.Add(keyId)) continue;
                foreach (var child in store.ListIssuedBy(keyId, includeRevoked: true))
                {
                    if (child.IssuedKeyId.HasValue && child.IssuedKeyId.Value != Guid.Empty)
                    {
                        lock (Plugin.ConfigWriteLock)
                        {
                            config.Shares.RemoveAll(k => k.Id == child.IssuedKeyId.Value);
                        }
                        toVisit.Enqueue(child.IssuedKeyId.Value);
                    }
                    if (child.Status != "revoked")
                    {
                        store.UpdateStatus(child.Id, "revoked");
                        cascaded++;
                    }
                }
            }
        }

        lock (Plugin.ConfigWriteLock) { Plugin.Instance?.SaveConfiguration(); }
        return Ok(new { status = "revoked", cascaded });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Introductions/{id}/CascadePreview")]
    public IActionResult CascadePreview(long id, [FromServices] Services.IntroductionStore store)
    {
        var intro = store.Get(id);
        if (intro is null) return NotFound();
        if (!intro.IssuedKeyId.HasValue) return Ok(new { cascade = Array.Empty<object>() });

        var collected = new System.Collections.Generic.List<object>();
        var toVisit = new System.Collections.Generic.Queue<Guid>();
        var seen = new System.Collections.Generic.HashSet<Guid>();
        toVisit.Enqueue(intro.IssuedKeyId.Value);
        while (toVisit.Count > 0)
        {
            var keyId = toVisit.Dequeue();
            if (!seen.Add(keyId)) continue;
            foreach (var child in store.ListIssuedBy(keyId, includeRevoked: true))
            {
                collected.Add(new { child.Id, child.ForUrlCanonical, child.IssuedKeyId, child.Status });
                if (child.IssuedKeyId.HasValue && child.IssuedKeyId.Value != Guid.Empty)
                    toVisit.Enqueue(child.IssuedKeyId.Value);
            }
        }
        return Ok(new { cascade = collected });
    }

    // === Push-invalidation receiver ===

    [AllowAnonymous]
    [HttpPost("Invalidate")]
    public IActionResult ReceiveInvalidate([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromBody] Services.InvalidatePayload payload)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (payload is null) return BadRequest();

        // If the share key is bound to a specific URL, the payload's claim is ignored -
        // it can only invalidate the digest for that one peer.
        var senderUrl = string.IsNullOrEmpty(key.BoundPeerUrl) ? payload.FromBaseUrl : key.BoundPeerUrl;
        if (string.IsNullOrEmpty(senderUrl)) return BadRequest();

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var match = config.RemoteServers.FirstOrDefault(s => Services.PeerUrl.SameHost(s.BaseUrl, senderUrl));
        if (match is null)
        {
            _logger.LogDebug("Invalidate from {Url} ignored - no matching RemoteServer", senderUrl);
            return NoContent();
        }

        _store.InvalidateDigest(match.Id);
        _logger.LogInformation("Invalidated cached digest for {Peer} per push notification", match.Name);
        return NoContent();
    }

    // === Anonymous video share links ===

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("PublicShares")]
    public IActionResult CreatePublicShare([FromBody] CreatePublicShareRequest req,
        [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        if (string.IsNullOrWhiteSpace(req.ItemId)) return BadRequest("ItemId required");
        if (!Guid.TryParse(req.ItemId, out var itemGuid)) return BadRequest("ItemId not a guid");

        var item = library.GetItemById(itemGuid);
        if (item is null) return NotFound("Item not found");

        var token = store.Create(req.ItemId!, req.ExpiresUtc, req.MaxUses, User?.Identity?.Name);
        var url = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/Federation/Public/{token}";
        return Ok(new { token, url, expiresUtc = req.ExpiresUtc, maxUses = req.MaxUses, itemName = item.Name });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("PublicShares/{token}")]
    public IActionResult RevokePublicShare(string token, [FromServices] Services.PublicShareStore store)
    {
        store.Revoke(token);
        return NoContent();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("PublicShares")]
    public IActionResult ListPublicShares([FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        var rows = store.ListAll(limit: 500).Select(s =>
        {
            var name = Guid.TryParse(s.ItemId, out var g) ? library.GetItemById(g)?.Name : null;
            return new
            {
                s.Token,
                s.ItemId,
                ItemName = name ?? "(deleted)",
                s.ExpiresUtc,
                s.MaxUses,
                s.UsedCount,
                s.CreatedUtc,
                Expired = s.ExpiresUtc.HasValue && s.ExpiresUtc < DateTime.UtcNow,
                Exhausted = s.MaxUses.HasValue && s.UsedCount >= s.MaxUses.Value
            };
        });
        return Ok(rows);
    }

    [AllowAnonymous]
    [HttpGet("Public/{token}")]
    public IActionResult PublicViewer(string token, [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        // Consume happens HERE - viewer-page load = one use. The /Stream endpoint below
        // re-validates (token exists + not expired) but does NOT decrement, so the same
        // viewer can seek / re-request Range without exhausting the cap.
        // Trade-off: a viewer reloading the page = another consume. Considered acceptable
        // - admin's mental model is "5 people can watch", not "5 byte ranges".
        var consumedItemId = store.TryConsume(token);
        if (consumedItemId is null)
        {
            // Distinguish 'unknown' from 'denied' via GetInfo for a friendlier 410 message.
            var probe = store.GetInfo(token);
            if (probe is null) return NotFound();
            if (probe.ExpiresUtc.HasValue && probe.ExpiresUtc < DateTime.UtcNow) return StatusCode(410, "Link expired");
            return StatusCode(410, "Link exhausted");
        }

        var info = store.GetInfo(token)!;
        if (!Guid.TryParse(info.ItemId, out var g)) return NotFound();
        var item = library.GetItemById(g);
        var name = item?.Name ?? "(unknown)";

        var html = $@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><title>{System.Net.WebUtility.HtmlEncode(name)}</title>
<style>
body{{background:#111;color:#eee;font-family:system-ui;margin:0;padding:2rem;text-align:center}}
video{{max-width:100%;max-height:80vh}}
h1{{font-weight:400;font-size:1.2rem}}
.meta{{color:#888;font-size:.8rem;margin-top:1rem}}
</style></head><body>
<h1>{System.Net.WebUtility.HtmlEncode(name)}</h1>
<video controls autoplay src=""/Federation/Public/{token}/Stream""></video>
<div class=""meta"">Shared link - {info.UsedCount + 1}{(info.MaxUses.HasValue ? "/" + info.MaxUses : "")} use{(info.MaxUses == 1 ? "" : "s")}{(info.ExpiresUtc.HasValue ? $" · expires {info.ExpiresUtc:yyyy-MM-dd HH:mm} UTC" : "")}</div>
</body></html>";
        return Content(html, "text/html; charset=utf-8");
    }

    [AllowAnonymous]
    [HttpGet("Public/{token}/Stream")]
    public IActionResult PublicStream(string token,
        [FromServices] Services.PublicShareStore store,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library)
    {
        // Validate-only path - does NOT consume. Consumption already happened in the viewer
        // page load (PublicViewer above). Each browser Range request hits us once; we just
        // confirm the token still exists and hasn't expired, then serve bytes.
        var info = store.GetInfo(token);
        if (info is null) return NotFound();
        if (info.ExpiresUtc.HasValue && info.ExpiresUtc < DateTime.UtcNow) return StatusCode(410, "Link expired");

        if (!Guid.TryParse(info.ItemId, out var g)) return NotFound();
        var item = library.GetItemById(g);
        if (item is null || string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path)) return NotFound();

        var contentType = GuessContentType(item.Path);
        return PhysicalFile(item.Path, contentType, enableRangeProcessing: true);
    }

    private static string GuessContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            _ => "application/octet-stream"
        };
    }

    [HttpGet("Subtitle/{serverId}/{itemId}/{mediaSourceId}/{streamIndex:int}.{format}")]
    public async Task<IActionResult> ProxySubtitle(string serverId, string itemId, string mediaSourceId,
        int streamIndex, string format, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return NotFound();
        if (!Guid.TryParseExact(itemId, "N", out _) && !Guid.TryParse(itemId, out _)) return BadRequest();
        if (streamIndex < 0) return BadRequest();
        // Format extension is admin-controllable in URL: allowlist to subtitle codecs only.
        var fmtLower = format.ToLowerInvariant();
        if (fmtLower is not ("srt" or "ass" or "ssa" or "vtt" or "sub" or "idx"))
            return BadRequest("unsupported subtitle format");
        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        Services.RemoteJellyfinClient.AddBasicAuth(http, server);
        var url = $"{server.BaseUrl.TrimEnd('/')}/Videos/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(mediaSourceId)}/Subtitles/{streamIndex}/Stream.{fmtLower}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Emby-Token", server.ApiKey);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return NotFound();

        Response.StatusCode = (int)resp.StatusCode;
        CopySafeHeaders(resp.Content.Headers, Response.Headers);
        await resp.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Subtitles/Discover")]
    public async Task<IActionResult> DiscoverSubtitles([FromQuery] string? tmdb, [FromQuery] string? imdb, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tmdb) && string.IsNullOrWhiteSpace(imdb)) return BadRequest("tmdb or imdb required");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        var peers = config.RemoteServers.Where(s => s.Enabled).ToArray();
        var results = await Task.WhenAll(peers.Select(p => DiscoverPeerSubtitlesAsync(p, tmdb, imdb, ct))).ConfigureAwait(false);
        return Ok(new { tmdb, imdb, tracks = results.SelectMany(r => r).ToList() });
    }

    private async Task<List<SubtitleTrack>> DiscoverPeerSubtitlesAsync(Configuration.RemoteServer peer, string? tmdb, string? imdb, CancellationToken ct)
    {
        var tracks = new List<SubtitleTrack>();
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(peer.BaseUrl.TrimEnd('/'));
            http.DefaultRequestHeaders.Add("X-Emby-Token", peer.ApiKey);
            Services.RemoteJellyfinClient.AddBasicAuth(http, peer);
            http.Timeout = TimeSpan.FromSeconds(10);

            var qs = !string.IsNullOrEmpty(tmdb) ? $"AnyProviderIdEquals=tmdb.{tmdb}" : $"AnyProviderIdEquals=imdb.{imdb}";
            var url = $"/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=MediaSources,MediaStreams,ProviderIds&Limit=5&{qs}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return tracks;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("Items", out var items)) return tracks;

            foreach (var item in items.EnumerateArray())
            {
                var remoteItemId = item.TryGetProperty("Id", out var id) ? id.GetString() : null;
                if (remoteItemId is null || !item.TryGetProperty("MediaSources", out var sources)) continue;

                foreach (var src in sources.EnumerateArray())
                {
                    var msId = src.TryGetProperty("Id", out var sid) ? sid.GetString() : null;
                    if (msId is null || !src.TryGetProperty("MediaStreams", out var streams)) continue;

                    foreach (var streamEl in streams.EnumerateArray())
                    {
                        var type = streamEl.TryGetProperty("Type", out var t) ? t.GetString() : null;
                        if (type != "Subtitle") continue;
                        var idx = streamEl.TryGetProperty("Index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
                        if (idx < 0) continue;
                        var codec = streamEl.TryGetProperty("Codec", out var c) ? c.GetString() : null;
                        var lang = streamEl.TryGetProperty("Language", out var l) ? l.GetString() : null;
                        var title = streamEl.TryGetProperty("Title", out var ti) ? ti.GetString() : null;
                        var isExternal = streamEl.TryGetProperty("IsExternal", out var ext) && ext.ValueKind == JsonValueKind.True;
                        tracks.Add(new SubtitleTrack
                        {
                            PeerId = peer.Id,
                            PeerName = peer.Name,
                            RemoteItemId = remoteItemId,
                            MediaSourceId = msId,
                            StreamIndex = idx,
                            Language = lang,
                            Codec = codec,
                            Title = title,
                            IsExternal = isExternal,
                            ProxyUrl = $"/Federation/Subtitle/{peer.Id:N}/{remoteItemId}/{msId}/{idx}.{(codec ?? "srt").ToLowerInvariant()}"
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle discovery failed for {Peer}", peer.Name);
        }
        return tracks;
    }

    [HttpGet("Image/{serverId}/{itemId}/{imageType}")]
    public async Task<IActionResult> ProxyImage(string serverId, string itemId, string imageType, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return NotFound();
        if (!Guid.TryParseExact(itemId, "N", out _) && !Guid.TryParse(itemId, out _)) return BadRequest();
        var server = config.RemoteServers.FirstOrDefault(s => s.Id.ToString("N") == serverId);
        if (server is null || !server.Enabled) return NotFound();

        var http = _httpClientFactory.CreateClient();
        Services.RemoteJellyfinClient.AddBasicAuth(http, server);
        var url = $"{server.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(itemId)}/Images/{Uri.EscapeDataString(imageType)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Emby-Token", server.ApiKey);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return NotFound();

        Response.StatusCode = (int)resp.StatusCode;
        CopySafeHeaders(resp.Content.Headers, Response.Headers);
        await resp.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        return new EmptyResult();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Audit/Recent")]
    public IActionResult RecentAudit([FromQuery] int limit = 100)
        => Ok(_store.RecentAudits(Math.Clamp(limit, 1, 1000)));

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Stats")]
    public IActionResult Stats([FromServices] Services.FederationStatsService stats)
        => Ok(stats.Build());

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Diagnostics")]
    public async Task<IActionResult> RunDiagnostics([FromServices] Services.DiagnosticsService diag, CancellationToken ct)
        => Ok(await diag.RunAsync(ct).ConfigureAwait(false));

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Catalog/Digest")]
    public IActionResult CatalogDigest([FromServices] Services.LocalCatalogDigest digest)
        => Ok(digest.Compute());

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Catalog/Items")]
    public IActionResult CatalogItems([FromServices] Services.LocalCatalogDigest digest)
        => Ok(digest.List());

    // === Share-key scoped endpoints ===

    [AllowAnonymous]
    [HttpGet("Share/Catalog/Digest")]
    public IActionResult ShareCatalogDigest([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromServices] Services.LocalCatalogDigest digest)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (!IsWithinSchedule(key)) return StatusCode(403, "Outside allowed-hours window");
        return Ok(digest.Compute(key.LibraryIds, key.BlockedTags, key.MaxOfficialRating, key.StrictUnknownRating));
    }

    [AllowAnonymous]
    [HttpGet("Share/Catalog/Items")]
    public IActionResult ShareCatalogItems([FromHeader(Name = "X-Federation-Share")] string? shareKey,
        [FromServices] Services.LocalCatalogDigest digest)
    {
        var key = ResolveShareKey(shareKey);
        if (key is null) return Unauthorized();
        if (!IsWithinSchedule(key)) return StatusCode(403, "Outside allowed-hours window");
        return Ok(digest.List(key.LibraryIds, key.BlockedTags, key.MaxOfficialRating, key.StrictUnknownRating));
    }

    private static bool IsWithinSchedule(Configuration.ShareKey key)
    {
        var tzId = key.ScheduleTimeZoneId;
        TimeZoneInfo tz;
        try
        {
            tz = string.IsNullOrEmpty(tzId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            tz = TimeZoneInfo.Local; // unknown id → fall back, don't lock peers out
        }
        var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).TimeOfDay;
        return Services.ScheduleWindow.IsWithin(key.AllowedHoursStart, key.AllowedHoursEnd, nowInTz);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Shares")]
    public IActionResult ListShares()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return Ok(Array.Empty<object>());
        return Ok(config.Shares.Select(s => new
        {
            s.Id,
            s.Label,
            s.LibraryIds,
            s.Enabled,
            s.CreatedUtc,
            ApiKeyPreview = s.ApiKey.Length > 8 ? s.ApiKey[..4] + "…" + s.ApiKey[^4..] : "***"
        }));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Shares")]
    public IActionResult CreateShare([FromBody] CreateShareRequest req)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        Configuration.ShareKey key;
        try
        {
            key = BuildShareKey(req);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        config.Shares.Add(key);
        Plugin.Instance!.SaveConfiguration();
        return Ok(new { key.Id, key.Label, key.ApiKey, key.LibraryIds });
    }

    private static Configuration.ShareKey BuildShareKey(CreateShareRequest req)
    {
        var key = new Configuration.ShareKey
        {
            Label = req.Label ?? "Unnamed share",
            LibraryIds = req.LibraryIds ?? new List<string>(),
            AllowedHoursStart = string.IsNullOrWhiteSpace(req.AllowedHoursStart) ? null : req.AllowedHoursStart,
            AllowedHoursEnd = string.IsNullOrWhiteSpace(req.AllowedHoursEnd) ? null : req.AllowedHoursEnd,
            ScheduleTimeZoneId = string.IsNullOrWhiteSpace(req.ScheduleTimeZoneId) ? null : req.ScheduleTimeZoneId,
            BlockedTags = req.BlockedTags ?? new List<string>(),
            MaxOfficialRating = string.IsNullOrWhiteSpace(req.MaxOfficialRating) ? null : req.MaxOfficialRating,
            StrictUnknownRating = req.StrictUnknownRating,
            // If admin supplied a BoundPeerUrl it MUST canonicalize - otherwise the share
            // key would silently never match any real peer. Validation happens before key
            // creation so admin sees an immediate error instead of discovering it later.
            BoundPeerUrl = string.IsNullOrWhiteSpace(req.BoundPeerUrl) ? null :
                (Services.PeerUrl.Canonicalize(req.BoundPeerUrl) ?? throw new ArgumentException(
                    "BoundPeerUrl must include http:// or https:// scheme (and a parseable host)", nameof(req))),
            CanRequestIntroductions = req.CanRequestIntroductions,
            MintMode = req.MintMode,
            ApiKey = GenerateApiKey(),
            Enabled = true
        };
        return key;
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("Shares/{id}")]
    public IActionResult DeleteShare(Guid id)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var removed = config.Shares.RemoveAll(s => s.Id == id);
        if (removed == 0) return NotFound();
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    private static Configuration.ShareKey? ResolveShareKey(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        var config = Plugin.Instance?.Configuration;
        if (config is null) return null;
        // Constant-time comparison - same length is necessary; CryptographicOperations.FixedTimeEquals
        // throws on length mismatch, so equalize via byte spans of equal size.
        var presentedBytes = System.Text.Encoding.UTF8.GetBytes(presented);
        foreach (var s in config.Shares)
        {
            if (!s.Enabled) continue;
            var storedBytes = System.Text.Encoding.UTF8.GetBytes(s.ApiKey);
            if (storedBytes.Length != presentedBytes.Length) continue;
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, presentedBytes))
                return s;
        }
        return null;
    }

    private static string GenerateApiKey()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Sync/Trigger")]
    public IActionResult TriggerSync([FromServices] MediaBrowser.Model.Tasks.ITaskManager taskManager)
    {
        taskManager.Execute<Services.FederationSyncTask>();
        return Ok(new { triggered = true });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Peers/Status")]
    public IActionResult PeersStatus([FromServices] Services.PeerHealthRegistry health)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return Ok(Array.Empty<object>());
        return Ok(config.RemoteServers.Select(s =>
        {
            var h = health.Get(s.Id);
            return new
            {
                s.Id,
                s.Name,
                s.BaseUrl,
                s.Enabled,
                Online = h.Online,
                LastCheckUtc = h.LastCheckUtc,
                LastRttMs = h.LastRttMs
            };
        }));
    }

    [HttpGet("Search")]
    public async Task<IActionResult> Search([FromQuery] string searchTerm, [FromQuery] int limit = 25, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(searchTerm))
            return Ok(new { Results = Array.Empty<object>() });

        var tasks = config.RemoteServers
            .Where(s => s.Enabled)
            .Select(s => QueryPeerAsync(s, searchTerm, limit, ct))
            .ToList();

        var bags = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = bags.SelectMany(x => x).Take(limit * config.RemoteServers.Count).ToList();
        return Ok(new { TotalRecordCount = merged.Count, Results = merged });
    }

    private async Task<List<FederatedSearchHit>> QueryPeerAsync(Configuration.RemoteServer server, string searchTerm, int limit, CancellationToken ct)
    {
        var results = new List<FederatedSearchHit>();
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(server.BaseUrl.TrimEnd('/'));
            http.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
            Services.RemoteJellyfinClient.AddBasicAuth(http, server);
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"/Search/Hints?searchTerm={Uri.EscapeDataString(searchTerm)}&limit={limit}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return results;

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("SearchHints", out var hints)) return results;

            foreach (var hit in hints.EnumerateArray())
            {
                results.Add(new FederatedSearchHit
                {
                    PeerId = server.Id,
                    PeerName = server.Name,
                    ItemId = hit.TryGetProperty("Id", out var id) ? id.GetString() : null,
                    Name = hit.TryGetProperty("Name", out var n) ? n.GetString() : null,
                    Type = hit.TryGetProperty("Type", out var t) ? t.GetString() : null,
                    Year = hit.TryGetProperty("ProductionYear", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null,
                    PrimaryImageUrl = $"{server.BaseUrl.TrimEnd('/')}/Items/{(hit.TryGetProperty("Id", out var iid) ? iid.GetString() : string.Empty)}/Images/Primary"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Federated search failed for {Peer}", server.Name);
        }
        return results;
    }

    // === Direct peer handshake (request access + invite) ===

    [AllowAnonymous]
    [HttpPost("AccessRequest")]
    public IActionResult ReceiveAccessRequest([FromBody] AccessRequestPayload payload,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.InboundAuditStore audit,
        [FromServices] Services.WebhookDispatcher webhook)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.FromUrl) || string.IsNullOrWhiteSpace(payload.Nonce))
            return BadRequest("FromUrl and Nonce required");
        if (payload.FromUrl.Length > 512 || payload.Nonce.Length > 128) return BadRequest("field too long");

        var fromCanon = Services.PeerUrl.Canonicalize(payload.FromUrl);
        if (fromCanon is null) return BadRequest("FromUrl must include http:// or https://");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        if (!config.AcceptInboundAccessRequests)
            return StatusCode(403, new { reason = "inbound access requests disabled by admin" });

        if (config.BlockedPeerUrls.Any(u => Services.PeerUrl.SameHost(u, fromCanon)))
            return StatusCode(403, new { reason = "peer is blocked" });

        // Anti-spam: at most one pending inbound row per (mode, target). If they already have
        // one pending, refuse the duplicate instead of growing the queue.
        var existing = store.List("in", "pending").FirstOrDefault(r => r.Mode == "request" && Services.PeerUrl.SameHost(r.TargetUrl, fromCanon));
        if (existing is not null)
            return Conflict(new { reason = "you already have a pending request with us; wait for admin to respond before sending another", existingId = existing.Id });

        var ourCanon = Services.PeerUrl.Canonicalize(config.PublicBaseUrl);
        if (ourCanon is not null && string.Equals(ourCanon, fromCanon, StringComparison.Ordinal))
            return BadRequest("cannot request access from self");

        // Auth gates: token > allowlist > open. Reject if none allow.
        string? authMode = null;
        if (!string.IsNullOrEmpty(payload.InviteToken))
        {
            var token = config.AccessRequestInviteTokens.FirstOrDefault(t => t.Token == payload.InviteToken);
            if (token is null) return Unauthorized(new { reason = "unknown invite token" });
            if (token.ExpiresUtc.HasValue && token.ExpiresUtc < DateTime.UtcNow) return Unauthorized(new { reason = "token expired" });
            if (token.MaxUses.HasValue && token.UsedCount >= token.MaxUses.Value) return Unauthorized(new { reason = "token exhausted" });
            authMode = "invite-token";
            lock (Plugin.ConfigWriteLock) { token.UsedCount++; Plugin.Instance?.SaveConfiguration(); }
        }
        else if (config.AccessRequestAllowlist.Any(u => Services.PeerUrl.SameHost(u, fromCanon)))
        {
            authMode = "allowlist";
        }
        else if (config.AcceptOpenAccessRequests)
        {
            authMode = "open";
        }
        else
        {
            return Unauthorized(new { reason = "no auth mode matched (open disabled, not in allowlist, no valid invite token)" });
        }

        // Per-IP rate limit. Skipped when allowlist/token authenticated (admin already vetted).
        if (authMode == "open")
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var hits = store.HitRateBucket(ip);
            if (hits > config.AccessRequestPerIpHourLimit)
                return StatusCode(429, new { reason = $"rate limit {config.AccessRequestPerIpHourLimit}/hour hit" });
        }

        var id = store.Insert(new Services.PeerAccessRow
        {
            Direction = "in",
            Mode = "request",
            TargetUrl = fromCanon,
            TargetName = payload.FromName,
            Nonce = payload.Nonce,
            Status = "pending",
            Mutual = payload.Mutual,
            TheirApiKey = null,
            Message = payload.Message,
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        audit.Record("access-request", "pending",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
            peerUrl: fromCanon, reason: $"auth={authMode}");
        webhook.Fire("access-request", $"{fromCanon} is asking for access ({authMode})", new { id, fromCanon, mutual = payload.Mutual });
        _logger.LogInformation("AccessRequest from {From} (auth={Auth}, mutual={Mutual}) -> pending #{Id}", fromCanon, authMode, payload.Mutual, id);
        return Accepted(new { id, status = "pending-approval" });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("RequestAccess")]
    public async Task<IActionResult> RequestAccess([FromBody] RequestAccessPayload payload,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TargetUrl)) return BadRequest("TargetUrl required");
        var targetCanon = Services.PeerUrl.Canonicalize(payload.TargetUrl);
        if (targetCanon is null) return BadRequest("TargetUrl must include http:// or https://");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        if (string.IsNullOrEmpty(config.PublicBaseUrl)) return BadRequest("PublicBaseUrl must be set so the peer can call us back with the granted key");
        if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, targetCanon))) return Conflict("already a peer");

        var nonce = Services.PeerAccessStore.GenerateNonce();
        var rowId = store.Insert(new Services.PeerAccessRow
        {
            Direction = "out",
            Mode = "request",
            TargetUrl = targetCanon,
            Nonce = nonce,
            Status = "pending",
            Mutual = payload.Mutual,
            Message = payload.Message
        });

        var result = await client.CallAccessRequestAsync(targetCanon,
            fromUrl: config.PublicBaseUrl,
            fromName: payload.OurName,
            message: payload.Message,
            nonce: nonce,
            mutual: payload.Mutual,
            inviteToken: payload.InviteToken,
            targetBasicAuthUser: payload.TargetBasicAuthUser,
            targetBasicAuthPass: payload.TargetBasicAuthPass,
            ourBasicAuthUser: config.PublicBaseUrlBasicAuthUser,
            ourBasicAuthPass: config.PublicBaseUrlBasicAuthPass,
            ct: ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            store.UpdateStatus(rowId, "failed");
            return StatusCode(502, new { reason = $"peer responded {result.HttpStatus}", body = result.Body });
        }
        return Ok(new { id = rowId, nonce, status = "sent" });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("AccessRequests/{direction}")]
    public IActionResult ListAccessRequests(string direction, [FromQuery] string? status,
        [FromServices] Services.PeerAccessStore store)
    {
        if (direction != "in" && direction != "out") return BadRequest("direction must be 'in' or 'out'");
        return Ok(store.List(direction, status));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("AccessRequests/{id}/Approve")]
    public async Task<IActionResult> ApproveAccessRequest(long id,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        var row = store.Get(id);
        if (row is null) return NotFound();
        if (row.Direction != "in" || row.Status != "pending") return BadRequest("only pending inbound requests can be approved");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        if (string.IsNullOrEmpty(config.PublicBaseUrl)) return BadRequest("PublicBaseUrl must be set so the peer can call us back");

        // Mint our key for the requester.
        var apiKey = GenerateApiKey();
        var keyId = Guid.NewGuid();
        lock (Plugin.ConfigWriteLock)
        {
            config.Shares.Add(new Configuration.ShareKey
            {
                Id = keyId,
                ApiKey = apiKey,
                Label = $"Direct access for {row.TargetUrl}",
                BoundPeerUrl = row.TargetUrl,
                Enabled = true
            });
            Plugin.Instance?.SaveConfiguration();
        }
        store.UpdateStatus(id, "approved", ourKeyId: keyId.ToString());

        var result = await client.CallAccessGrantedAsync(row.TargetUrl,
            ourBaseUrl: config.PublicBaseUrl,
            apiKey: apiKey,
            nonce: row.Nonce,
            mutual: row.Mutual,
            targetBasicAuthUser: null, targetBasicAuthPass: null,
            ourBasicAuthUser: config.PublicBaseUrlBasicAuthUser,
            ourBasicAuthPass: config.PublicBaseUrlBasicAuthPass,
            ct: ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            _logger.LogWarning("AccessGranted callback to {Peer} returned {Status}: {Body}", row.TargetUrl, result.HttpStatus, result.Body);
            return StatusCode(502, new { reason = $"granted key but callback to peer failed: {result.HttpStatus}", body = result.Body, keyId, apiKey });
        }

        // If !Mutual, we don't add the peer to our RemoteServers (we just authorized them to call us).
        // If Mutual, we wait for their /AccessGranted call back (with their own key) to flip status to 'completed'.
        return Ok(new { id, keyId, mutual = row.Mutual, status = row.Mutual ? "approved-awaiting-reciprocal" : "approved" });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("AccessRequests/{id}/Deny")]
    public IActionResult DenyAccessRequest(long id, [FromServices] Services.PeerAccessStore store)
    {
        var row = store.Get(id);
        if (row is null) return NotFound();
        if (row.Status != "pending") return BadRequest("not pending");
        store.UpdateStatus(id, "denied", completedNow: true);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("AccessGranted")]
    public IActionResult ReceiveAccessGranted([FromBody] AccessGrantedPayload payload,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Nonce) || string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.OurBaseUrl))
            return BadRequest("Nonce, ApiKey, OurBaseUrl required");

        var row = store.GetByNonce(payload.Nonce);
        if (row is null) return NotFound("unknown nonce");

        var fromCanon = Services.PeerUrl.Canonicalize(payload.OurBaseUrl);
        if (fromCanon is null) return BadRequest("OurBaseUrl invalid");
        if (!Services.PeerUrl.SameHost(row.TargetUrl, fromCanon))
            return BadRequest($"OurBaseUrl {fromCanon} does not match the row's peer ({row.TargetUrl})");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        // Two cases:
        //   out-row: we sent the original request, peer is granting us a key. Add them as peer.
        //   in-row : we already approved their request; this POST is the peer completing the
        //            mutual handshake by sending us THEIR key. Add them too.
        // The same nonce is shared by both sides intentionally so a single channel covers both
        // directions of mutual; the row's direction tells us which way this POST resolves.

        if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, fromCanon)))
        {
            store.UpdateStatus(row.Id, "completed", completedNow: true);
            return Conflict(new { reason = "already a peer; nothing added" });
        }

        Guid peerGuid;
        lock (Plugin.ConfigWriteLock)
        {
            peerGuid = Guid.NewGuid();
            config.RemoteServers.Add(new Configuration.RemoteServer
            {
                Id = peerGuid,
                Name = fromCanon,
                BaseUrl = fromCanon,
                FederationShareKey = payload.ApiKey,
                BasicAuthUser = payload.BasicAuthUser ?? string.Empty,
                BasicAuthPass = payload.BasicAuthPass ?? string.Empty,
                Enabled = true
            });
            Plugin.Instance?.SaveConfiguration();
        }
        store.UpdateStatus(row.Id, "completed", theirApiKey: payload.ApiKey, completedNow: true);

        // Mutual + we initiated (out-row): we ALSO mint a key for them and POST back. Skipped
        // when row.Direction=='in' because we already minted on Approve, AND when row.OurKeyId
        // is set (we already sent the reciprocal earlier).
        if (row.Direction == "out" && payload.Mutual && row.Mutual && string.IsNullOrEmpty(row.OurKeyId))
        {
            var reciprocalKey = GenerateApiKey();
            var reciprocalKeyId = Guid.NewGuid();
            lock (Plugin.ConfigWriteLock)
            {
                config.Shares.Add(new Configuration.ShareKey
                {
                    Id = reciprocalKeyId,
                    ApiKey = reciprocalKey,
                    Label = $"Direct access for {fromCanon}",
                    BoundPeerUrl = fromCanon,
                    Enabled = true
                });
                Plugin.Instance?.SaveConfiguration();
            }
            store.UpdateStatus(row.Id, "completed", ourKeyId: reciprocalKeyId.ToString());

            _ = Task.Run(async () =>
            {
                await client.CallAccessGrantedAsync(fromCanon,
                    ourBaseUrl: config.PublicBaseUrl,
                    apiKey: reciprocalKey,
                    nonce: payload.Nonce,
                    mutual: false,
                    targetBasicAuthUser: payload.BasicAuthUser, targetBasicAuthPass: payload.BasicAuthPass,
                    ourBasicAuthUser: config.PublicBaseUrlBasicAuthUser,
                    ourBasicAuthPass: config.PublicBaseUrlBasicAuthPass,
                    ct: CancellationToken.None).ConfigureAwait(false);
            });
        }

        _logger.LogInformation("AccessGranted received from {Peer} (row direction={Dir}, mutual={Mu}) -> added peer {Id}",
            fromCanon, row.Direction, row.Mutual, peerGuid);
        return Ok(new { added = peerGuid, status = "completed" });
    }

    // === Invite flow (A pushes a pre-minted key to C) ===

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Invite")]
    public async Task<IActionResult> SendInvite([FromBody] SendInvitePayload payload,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TargetUrl)) return BadRequest("TargetUrl required");
        var targetCanon = Services.PeerUrl.Canonicalize(payload.TargetUrl);
        if (targetCanon is null) return BadRequest("TargetUrl must include http:// or https://");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        if (string.IsNullOrEmpty(config.PublicBaseUrl)) return BadRequest("PublicBaseUrl must be set");
        if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, targetCanon))) return Conflict("already a peer");

        var apiKey = GenerateApiKey();
        var keyId = Guid.NewGuid();
        lock (Plugin.ConfigWriteLock)
        {
            config.Shares.Add(new Configuration.ShareKey
            {
                Id = keyId,
                ApiKey = apiKey,
                Label = $"Invite for {targetCanon}",
                BoundPeerUrl = targetCanon,
                Enabled = true
            });
            Plugin.Instance?.SaveConfiguration();
        }

        var nonce = Services.PeerAccessStore.GenerateNonce();
        var rowId = store.Insert(new Services.PeerAccessRow
        {
            Direction = "out",
            Mode = "invite",
            TargetUrl = targetCanon,
            Nonce = nonce,
            Status = "pending",
            Mutual = payload.Mutual,
            OurKeyId = keyId.ToString(),
            Message = payload.Message
        });

        var result = await client.CallInviteOfferAsync(targetCanon,
            ourBaseUrl: config.PublicBaseUrl,
            apiKey: apiKey,
            nonce: nonce,
            mutual: payload.Mutual,
            message: payload.Message,
            targetBasicAuthUser: payload.TargetBasicAuthUser,
            targetBasicAuthPass: payload.TargetBasicAuthPass,
            ourBasicAuthUser: config.PublicBaseUrlBasicAuthUser,
            ourBasicAuthPass: config.PublicBaseUrlBasicAuthPass,
            ct: ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            store.UpdateStatus(rowId, "failed");
            return StatusCode(502, new { reason = $"peer responded {result.HttpStatus}", body = result.Body });
        }
        return Ok(new { id = rowId, keyId, nonce, status = "sent" });
    }

    [AllowAnonymous]
    [HttpPost("InviteOffer")]
    public IActionResult ReceiveInviteOffer([FromBody] InviteOfferPayload payload,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.InboundAuditStore audit,
        [FromServices] Services.WebhookDispatcher webhook)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.OurBaseUrl)
            || string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.Nonce))
            return BadRequest("OurBaseUrl, ApiKey, Nonce required");

        var fromCanon = Services.PeerUrl.Canonicalize(payload.OurBaseUrl);
        if (fromCanon is null) return BadRequest("OurBaseUrl invalid");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        if (!config.AcceptInboundInvites)
            return StatusCode(403, new { reason = "inbound invites disabled by admin" });

        if (config.BlockedPeerUrls.Any(u => Services.PeerUrl.SameHost(u, fromCanon)))
            return StatusCode(403, new { reason = "peer is blocked" });

        var existing = store.List("in", "pending").FirstOrDefault(r => r.Mode == "invite" && Services.PeerUrl.SameHost(r.TargetUrl, fromCanon));
        if (existing is not null)
            return Conflict(new { reason = "you already have a pending invite with us", existingId = existing.Id });

        var ourCanon = Services.PeerUrl.Canonicalize(config.PublicBaseUrl);
        if (ourCanon is not null && string.Equals(ourCanon, fromCanon, StringComparison.Ordinal))
            return BadRequest("cannot invite self");

        if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, fromCanon)))
            return Conflict("already a peer");

        var id = store.Insert(new Services.PeerAccessRow
        {
            Direction = "in",
            Mode = "invite",
            TargetUrl = fromCanon,
            Nonce = payload.Nonce,
            Status = "pending",
            Mutual = payload.Mutual,
            TheirApiKey = payload.ApiKey,
            Message = payload.Message,
            ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        audit.Record("invite-offer", "pending",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
            peerUrl: fromCanon);
        webhook.Fire("invite-offer", $"{fromCanon} offered an invite (mutual={payload.Mutual})", new { id, fromCanon, mutual = payload.Mutual });
        _logger.LogInformation("InviteOffer from {From} (mutual={Mutual}) -> pending #{Id}", fromCanon, payload.Mutual, id);
        return Accepted(new { id, status = "pending-approval" });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("AccessRequests/{id}/Accept")]
    public async Task<IActionResult> AcceptInvite(long id,
        [FromServices] Services.PeerAccessStore store,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        var row = store.Get(id);
        if (row is null) return NotFound();
        if (row.Direction != "in" || row.Mode != "invite" || row.Status != "pending")
            return BadRequest("only pending inbound invites can be accepted");
        if (string.IsNullOrEmpty(row.TheirApiKey)) return BadRequest("invite missing api key");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        if (config.RemoteServers.Any(s => Services.PeerUrl.SameHost(s.BaseUrl, row.TargetUrl)))
        {
            store.UpdateStatus(id, "completed", completedNow: true);
            return Conflict(new { reason = "already a peer" });
        }

        Guid peerGuid;
        lock (Plugin.ConfigWriteLock)
        {
            peerGuid = Guid.NewGuid();
            config.RemoteServers.Add(new Configuration.RemoteServer
            {
                Id = peerGuid,
                Name = row.TargetUrl,
                BaseUrl = row.TargetUrl,
                FederationShareKey = row.TheirApiKey!,
                Enabled = true
            });
            Plugin.Instance?.SaveConfiguration();
        }

        string? reciprocalApiKey = null;
        if (row.Mutual)
        {
            reciprocalApiKey = GenerateApiKey();
            var keyId = Guid.NewGuid();
            lock (Plugin.ConfigWriteLock)
            {
                config.Shares.Add(new Configuration.ShareKey
                {
                    Id = keyId,
                    ApiKey = reciprocalApiKey,
                    Label = $"Direct access for {row.TargetUrl}",
                    BoundPeerUrl = row.TargetUrl,
                    Enabled = true
                });
                Plugin.Instance?.SaveConfiguration();
            }
            store.UpdateStatus(id, "completed", ourKeyId: keyId.ToString(), completedNow: true);
        }
        else
        {
            store.UpdateStatus(id, "completed", completedNow: true);
        }

        var result = await client.CallInviteAcceptedAsync(row.TargetUrl,
            nonce: row.Nonce,
            apiKey: reciprocalApiKey,
            mutual: row.Mutual,
            targetBasicAuthUser: null, targetBasicAuthPass: null,
            ourBaseUrl: config.PublicBaseUrl,
            ourBasicAuthUser: config.PublicBaseUrlBasicAuthUser,
            ourBasicAuthPass: config.PublicBaseUrlBasicAuthPass,
            ct: ct).ConfigureAwait(false);

        // Even if the callback fails, we have the peer added locally. Surface the warning.
        if (!result.Ok)
            _logger.LogWarning("InviteAccepted callback to {Peer} returned {Status}: {Body}", row.TargetUrl, result.HttpStatus, result.Body);

        return Ok(new { added = peerGuid, mutual = row.Mutual, callbackOk = result.Ok });
    }

    [AllowAnonymous]
    [HttpPost("InviteAccepted")]
    public IActionResult ReceiveInviteAccepted([FromBody] InviteAcceptedPayload payload,
        [FromServices] Services.PeerAccessStore store)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Nonce)) return BadRequest("Nonce required");

        var row = store.GetByNonce(payload.Nonce);
        if (row is null) return NotFound("unknown nonce");
        if (row.Direction != "out" || row.Mode != "invite") return BadRequest("nonce belongs to wrong row");

        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        // The peer accepted; if mutual, they ALSO provided their own key. Add them as a RemoteServer.
        if (payload.Mutual && !string.IsNullOrEmpty(payload.ApiKey) && !string.IsNullOrEmpty(payload.OurBaseUrl))
        {
            var theirCanon = Services.PeerUrl.Canonicalize(payload.OurBaseUrl);
            if (theirCanon is null || !Services.PeerUrl.SameHost(row.TargetUrl, theirCanon))
                return BadRequest("OurBaseUrl mismatch vs invite target");

            if (config.RemoteServers.All(s => !Services.PeerUrl.SameHost(s.BaseUrl, theirCanon)))
            {
                lock (Plugin.ConfigWriteLock)
                {
                    config.RemoteServers.Add(new Configuration.RemoteServer
                    {
                        Id = Guid.NewGuid(),
                        Name = theirCanon,
                        BaseUrl = theirCanon,
                        FederationShareKey = payload.ApiKey!,
                        BasicAuthUser = payload.BasicAuthUser ?? string.Empty,
                        BasicAuthPass = payload.BasicAuthPass ?? string.Empty,
                        Enabled = true
                    });
                    Plugin.Instance?.SaveConfiguration();
                }
                store.UpdateStatus(row.Id, "completed", theirApiKey: payload.ApiKey, completedNow: true);
                return Ok(new { added = true, status = "completed" });
            }
        }

        store.UpdateStatus(row.Id, "completed", completedNow: true);
        return Ok(new { added = false, status = "completed" });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("AccessRequests/{id}/Decline")]
    public IActionResult DeclineInvite(long id, [FromServices] Services.PeerAccessStore store)
    {
        var row = store.Get(id);
        if (row is null) return NotFound();
        if (row.Direction != "in" || row.Status != "pending") return BadRequest("not a pending inbound");
        store.UpdateStatus(id, "denied", completedNow: true);
        return NoContent();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("AccessRequests/{id}/Block")]
    public IActionResult BlockPeerForRequest(long id, [FromServices] Services.PeerAccessStore store)
    {
        var row = store.Get(id);
        if (row is null) return NotFound();
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);

        lock (Plugin.ConfigWriteLock)
        {
            if (!config.BlockedPeerUrls.Any(u => Services.PeerUrl.SameHost(u, row.TargetUrl)))
                config.BlockedPeerUrls.Add(row.TargetUrl);
            Plugin.Instance?.SaveConfiguration();
        }
        if (row.Status == "pending") store.UpdateStatus(id, "denied", completedNow: true);
        return Ok(new { blocked = row.TargetUrl });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("PendingCounts")]
    public IActionResult GetPendingCounts(
        [FromServices] Services.PeerAccessStore access,
        [FromServices] Services.RequestStore requests,
        [FromServices] Services.IntroductionStore intros)
    {
        // Lightweight count probe for the UI badge. Sums all distinct queues admin must triage.
        var pendingAccess = access.List("in", "pending").Count;
        var pendingInvites = pendingAccess; // intentionally folded into accessRequests; admin sees both in one list
        var pendingContentReq = requests.List("in", "pending").Count;
        var pendingIntros = intros.ListByRole("receiver", "pending").Count
            + intros.ListByRole("issuer", "pending").Count;
        var total = pendingAccess + pendingContentReq + pendingIntros;
        return Ok(new { accessRequests = pendingAccess, contentRequests = pendingContentReq, introductions = pendingIntros, total });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Audit/Inbound")]
    public IActionResult GetInboundAudit([FromQuery] int limit, [FromQuery] string? mode, [FromQuery] string? outcome,
        [FromServices] Services.InboundAuditStore audit)
    {
        var lim = limit > 0 && limit <= 1000 ? limit : 200;
        return Ok(audit.List(lim, mode, outcome));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Health/{peerId}/Stats")]
    public IActionResult GetHealthStats(string peerId, [FromQuery] int hours,
        [FromServices] Services.PeerHealthHistoryStore history)
    {
        if (!Guid.TryParse(peerId, out var pid)) return BadRequest("peerId invalid");
        var window = TimeSpan.FromHours(hours > 0 && hours <= 720 ? hours : 24);
        return Ok(history.Stats(pid, window));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Health/{peerId}/Series")]
    public IActionResult GetHealthSeries(string peerId, [FromQuery] int hours,
        [FromServices] Services.PeerHealthHistoryStore history)
    {
        if (!Guid.TryParse(peerId, out var pid)) return BadRequest("peerId invalid");
        var window = TimeSpan.FromHours(hours > 0 && hours <= 720 ? hours : 24);
        return Ok(history.Samples(pid, window));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Diff/{peerId}")]
    public async Task<IActionResult> DiffWithPeer(string peerId, [FromQuery] string? direction,
        [FromServices] Services.RemoteItemStore store, CancellationToken ct)
    {
        if (!Guid.TryParse(peerId, out var pid)) return BadRequest("peerId invalid");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var peer = config.RemoteServers.FirstOrDefault(s => s.Id == pid);
        if (peer is null) return NotFound("unknown peer");

        // Peer-side items: from our cached remote_items for this server_id.
        var peerItems = store.GetAllItems().Where(i => i.ServerId == pid).ToList();
        var peerTmdb = new HashSet<string>(peerItems.Where(i => i.ProviderIds.ContainsKey("Tmdb")).Select(i => i.ProviderIds["Tmdb"]));

        // Local items via Jellyfin: fetch our own /Items?IncludeItemTypes=Movie,Episode&Fields=ProviderIds.
        // Reusing the federation client w/ self - but easier: pull from local catalog via library manager later.
        // For v1 we use the RemoteItemStore round-trip placeholder: count and titles only.
        var weLack = peerItems.Where(i => !LocalHasTmdb(i.ProviderIds.GetValueOrDefault("Tmdb"))).ToList();
        // 'theyLack' would require a roundtrip to the peer's full item list - reserved for v2.
        await Task.CompletedTask;

        var dirLower = direction?.ToLowerInvariant();
        return Ok(new
        {
            peerId = pid,
            peerName = peer.Name,
            weLack = dirLower == "they-lack" ? null : weLack.Select(i => new { i.RemoteItemId, i.Name, i.ProductionYear, tmdb = i.ProviderIds.GetValueOrDefault("Tmdb") }),
            theyLack = dirLower == "we-lack" ? null : (object?)"v2: requires a full pull of peer's catalog"
        });
    }

    private bool LocalHasTmdb(string? tmdb)
    {
        if (string.IsNullOrEmpty(tmdb)) return false;
        // Cheap probe via the library manager isn't available here without DI plumbing. Push
        // this through FriendsLibraryChannel's cached set on the next pass; for now return false
        // so 'we lack' includes every peer item (admin reads as "all peer titles we don't track").
        return false;
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Graph")]
    public IActionResult GetGraph([FromServices] Services.PeerHealthRegistry health,
        [FromServices] Services.IntroductionStore intros)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var us = new
        {
            id = "self",
            label = string.IsNullOrEmpty(config.PublicBaseUrl) ? "us" : config.PublicBaseUrl,
            online = true,
            self = true
        };
        var nodes = new System.Collections.Generic.List<object> { us };
        var edges = new System.Collections.Generic.List<object>();
        foreach (var p in config.RemoteServers)
        {
            nodes.Add(new { id = p.Id.ToString("N"), label = p.Name, online = health.IsOnline(p.Id), tags = p.Tags });
            edges.Add(new { source = "self", target = p.Id.ToString("N"), kind = "peer", enabled = p.Enabled });
        }
        // Issuer-side introductions surface as edges from us to the introduced URL.
        foreach (var i in intros.ListByRole("issuer", "active"))
        {
            edges.Add(new { source = "self", target = i.ForUrlCanonical, kind = "introduced" });
        }
        return Ok(new { nodes, edges });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Config/Export")]
    public IActionResult ExportConfig()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        // Sanitize: redact raw ApiKey / FederationShareKey / BasicAuthPass / InviteToken values.
        // The export is meant for migration, not for credentials transfer (peers re-issue on the new host).
        var sanitized = new
        {
            ExportedUtc = DateTime.UtcNow.ToString("O"),
            Version = Plugin.Instance?.Version?.ToString(),
            Settings = new
            {
                config.SyncIntervalMinutes, config.EnableDedup, config.MatchPriority,
                config.ShowRemoteOnlyItems, config.EnableWatchStateSync, config.PublicBaseUrl,
                config.PushDebounceSeconds, config.OutboundBitrateCapBps,
                config.AcceptOpenAccessRequests, config.AccessRequestAllowlist,
                config.AcceptInboundAccessRequests, config.AcceptInboundInvites, config.AcceptInboundIntroductions,
                config.BlockedPeerUrls, config.RetentionDays,
                config.WebhookUrl, config.WebhookEvents, config.WebhookDiscordFormat,
                config.DashboardLanguage
            },
            RemoteServers = config.RemoteServers.Select(p => new
            {
                p.Name, p.BaseUrl, p.Enabled, p.Tags,
                p.InboundReqPerHourLimit, p.OutboundBytesPerDayLimit,
                p.AllowedLibraryIds,
                ApiKey = "[redacted]",
                FederationShareKey = "[redacted]",
                BasicAuthUser = string.IsNullOrEmpty(p.BasicAuthUser) ? "" : "[redacted]",
                BasicAuthPass = string.IsNullOrEmpty(p.BasicAuthPass) ? "" : "[redacted]"
            }),
            Shares = config.Shares.Select(s => new
            {
                s.Label, s.LibraryIds, s.BlockedTags, s.MaxOfficialRating, s.StrictUnknownRating,
                s.AllowedHoursStart, s.AllowedHoursEnd, s.ScheduleTimeZoneId,
                s.BoundPeerUrl, s.CanRequestIntroductions, s.MintMode, s.Enabled,
                ApiKey = "[redacted]"
            })
        };
        return Ok(sanitized);
    }

    [HttpGet("PeerDirectory")]
    public IActionResult GetPeerDirectory([FromHeader(Name = "X-Federation-Share")] string? shareKey)
    {
        // Two callers: the local admin (auth via X-Emby-Token, no share key required) and
        // remote peers (must present a share key OR we have to opt in to public listing).
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var isLocal = User?.Identity?.IsAuthenticated ?? false;
        if (!isLocal)
        {
            var key = ResolveShareKey(shareKey);
            if (key is null) return Unauthorized();
            if (!config.PublishPeerDirectory) return StatusCode(403, new { reason = "peer directory not published" });
        }
        return Ok(config.RemoteServers.Where(p => p.Enabled).Select(p => new
        {
            name = p.Name,
            url = p.BaseUrl,
            tags = p.Tags
        }));
    }

    [HttpGet("Peers/{peerId}/Libraries")]
    public async Task<IActionResult> ListPeerLibraries(string peerId,
        [FromQuery] bool? onlyEnabled,
        [FromServices] Services.PeerLibraryCache cache,
        CancellationToken ct)
    {
        if (!Guid.TryParse(peerId, out var pid)) return BadRequest("peerId invalid");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var peer = config.RemoteServers.FirstOrDefault(p => p.Id == pid);
        if (peer is null || !peer.Enabled) return NotFound();

        string json;
        if (cache.TryGet(Services.PeerLibraryCache.LibsKey(pid), out json))
        {
            return ContentWithLibFilter(json, pid, config, onlyEnabled == true);
        }
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            Services.RemoteJellyfinClient.AddBasicAuth(http, peer);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.BaseUrl.TrimEnd('/')}/Library/VirtualFolders");
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return StatusCode((int)resp.StatusCode);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var rows = new System.Collections.Generic.List<object>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                rows.Add(new
                {
                    id = el.TryGetProperty("ItemId", out var iid) ? iid.GetString() : null,
                    name = el.TryGetProperty("Name", out var n) ? n.GetString() : null,
                    type = el.TryGetProperty("CollectionType", out var ct2) ? ct2.GetString() : null
                });
            }
            json = System.Text.Json.JsonSerializer.Serialize(rows);
            cache.Store(Services.PeerLibraryCache.LibsKey(pid), json);
            return ContentWithLibFilter(json, pid, config, onlyEnabled == true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ListPeerLibraries to {Peer} failed", peer.Name);
            return StatusCode(502, new { reason = ex.Message });
        }
    }

    private IActionResult ContentWithLibFilter(string json, Guid pid, Configuration.PluginConfiguration config, bool onlyEnabled)
    {
        if (!onlyEnabled) return Content(json, "application/json");
        // Apply per-lib enabled filter so the home-page caller doesn't need to know the
        // config layout. Default for libs with no setting is enabled.
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var settings = config.PeerLibrarySettings;
        var filtered = new System.Collections.Generic.List<object>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var iid) ? iid.GetString() : null;
            if (id is null) continue;
            var s = settings.FirstOrDefault(x => x.PeerId == pid && x.LibraryId == id);
            if (s is not null && !s.Enabled) continue;
            filtered.Add(new
            {
                id,
                name = el.TryGetProperty("name", out var n) ? n.GetString() : null,
                type = el.TryGetProperty("type", out var t) ? t.GetString() : null,
                hideFromHomepage = s?.HideFromHomepage == true,
                mergeWithLocalLibraryId = s?.MergeWithLocalLibraryId
            });
        }
        return Ok(filtered);
    }

    [HttpGet("Peers/{peerId}/Libraries/{libraryId}/Items")]
    public async Task<IActionResult> ListPeerLibraryItems(string peerId, string libraryId, [FromQuery] int? limit,
        [FromServices] MediaBrowser.Controller.Library.ILibraryManager library,
        [FromServices] Services.PeerLibraryCache cache,
        CancellationToken ct)
    {
        if (!Guid.TryParse(peerId, out var pid)) return BadRequest("peerId invalid");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var peer = config.RemoteServers.FirstOrDefault(p => p.Id == pid);
        if (peer is null || !peer.Enabled) return NotFound();
        var lim = Math.Clamp(limit ?? 24, 1, 100);
        var cacheKey = Services.PeerLibraryCache.LibItemsKey(pid, libraryId, lim);
        if (cache.TryGet(cacheKey, out var cached)) return Content(cached, "application/json");
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            Services.RemoteJellyfinClient.AddBasicAuth(http, peer);
            var fields = "ProviderIds,MediaStreams,Container,Width,Height,RunTimeTicks";
            var prefix = !string.IsNullOrEmpty(peer.RemoteUserId) ? $"/Users/{peer.RemoteUserId}/Items" : "/Items";
            var url = $"{peer.BaseUrl.TrimEnd('/')}{prefix}?ParentId={Uri.EscapeDataString(libraryId)}&Recursive=true&IncludeItemTypes=Movie,Series&Fields={fields}&Limit={lim}&SortBy=DateCreated&SortOrder=Descending";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Emby-Token", peer.ApiKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return StatusCode((int)resp.StatusCode);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            // Build a one-pass map of {fed_<peerN>_<remoteItemId> -> local Jellyfin item id} so
            // each card we ship to the client carries the local id that the SPA can navigate
            // to via #/details?id=. Without it cards are pure thumbnails with no click target.
            var externalToLocal = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var q = new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode }
                };
                foreach (var li in library.GetItemList(q))
                {
                    var ext = li.ExternalId;
                    if (!string.IsNullOrEmpty(ext) && ext.StartsWith("fed_", StringComparison.Ordinal))
                        externalToLocal[ext] = li.Id.ToString("N");
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "ExternalId -> local id mapping failed"); }

            var rows = new System.Collections.Generic.List<object>();
            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var el in items.EnumerateArray())
                {
                    var id = el.TryGetProperty("Id", out var iid) ? iid.GetString() : null;
                    if (id is null) continue;
                    var video = "";
                    if (el.TryGetProperty("MediaStreams", out var ms) && ms.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var s in ms.EnumerateArray())
                        {
                            if (s.TryGetProperty("Type", out var ty) && ty.GetString() == "Video")
                            {
                                var height = s.TryGetProperty("Height", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number ? h.GetInt32() : 0;
                                var codec = s.TryGetProperty("Codec", out var c) ? c.GetString() : null;
                                video = $"{height}p {codec}".Trim();
                                break;
                            }
                        }
                    }
                    var externalId = "fed_" + pid.ToString("N") + "_" + id;
                    externalToLocal.TryGetValue(externalId, out var localId);
                    // Fallback: dedup hid this remote item because we own it locally too.
                    // Look up the local Movie by TMDB/IMDB and link the card to it.
                    string? localKind = localId is null ? null : "channel";
                    if (localId is null)
                    {
                        string? tmdb = null, imdb = null;
                        if (el.TryGetProperty("ProviderIds", out var pids) && pids.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (pids.TryGetProperty("Tmdb", out var tm) && tm.ValueKind == System.Text.Json.JsonValueKind.String) tmdb = tm.GetString();
                            if (pids.TryGetProperty("Imdb", out var im) && im.ValueKind == System.Text.Json.JsonValueKind.String) imdb = im.GetString();
                        }
                        if (!string.IsNullOrEmpty(tmdb) || !string.IsNullOrEmpty(imdb))
                        {
                            try
                            {
                                var lookup = new MediaBrowser.Controller.Entities.InternalItemsQuery
                                {
                                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode },
                                    Recursive = true,
                                    Limit = 1
                                };
                                if (!string.IsNullOrEmpty(tmdb)) lookup.HasAnyProviderId = new System.Collections.Generic.Dictionary<string, string> { ["Tmdb"] = tmdb };
                                else lookup.HasAnyProviderId = new System.Collections.Generic.Dictionary<string, string> { ["Imdb"] = imdb! };
                                var match = library.GetItemList(lookup).FirstOrDefault();
                                if (match is not null) { localId = match.Id.ToString("N"); localKind = "local"; }
                            }
                            catch { /* skip */ }
                        }
                    }
                    rows.Add(new
                    {
                        id,
                        // localId is the Jellyfin item id the SPA can navigate to. localKind
                        // tells the client whether it points to a channel item (federated) or
                        // a normal local Movie (we already own it).
                        localId,
                        localKind,
                        name = el.TryGetProperty("Name", out var n) ? n.GetString() : null,
                        type = el.TryGetProperty("Type", out var t) ? t.GetString() : null,
                        year = el.TryGetProperty("ProductionYear", out var py) && py.ValueKind == System.Text.Json.JsonValueKind.Number ? py.GetInt32() : (int?)null,
                        version = video,
                        // Image goes through our reverse proxy so the peer API key never reaches the client.
                        imageUrl = $"/Federation/Image/{pid:N}/{id}/Primary"
                    });
                }
            }
            var payload = new { peerId = pid, peerName = peer.Name, items = rows };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            cache.Store(cacheKey, json);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ListPeerLibraryItems to {Peer} failed", peer.Name);
            return StatusCode(502, new { reason = ex.Message });
        }
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("PeerLibraryConfig")]
    public IActionResult GetPeerLibraryConfig()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        return Ok(new { layout = config.PeerHomeLayout.ToString(), settings = config.PeerLibrarySettings });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("PeerLibraryConfig")]
    public IActionResult SetPeerLibraryConfig([FromBody] PeerLibraryConfigPayload payload,
        [FromServices] Services.PeerLibraryCache cache)
    {
        if (payload is null) return BadRequest("missing body");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        lock (Plugin.ConfigWriteLock)
        {
            if (payload.Layout is { Length: > 0 } && Enum.TryParse<Configuration.PeerHomeLayout>(payload.Layout, true, out var lay))
                config.PeerHomeLayout = lay;
            if (payload.Settings is not null)
            {
                // Replace wholesale: the caller sends the full desired state for the
                // peers/libs it cares about. We preserve entries for peers not mentioned.
                var keep = config.PeerLibrarySettings
                    .Where(s => !payload.Settings.Any(p => p.PeerId == s.PeerId && p.LibraryId == s.LibraryId))
                    .ToList();
                keep.AddRange(payload.Settings);
                config.PeerLibrarySettings = keep;
            }
            Plugin.Instance?.SaveConfiguration();
        }
        // Settings changed → drop cached lib listings so the next read reflects new flags.
        cache.Clear();
        return Ok(new { layout = config.PeerHomeLayout.ToString(), settings = config.PeerLibrarySettings });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Peers/{peerId}/Directory")]
    public async Task<IActionResult> FetchPeerDirectory(string peerId,
        [FromServices] Services.RemoteJellyfinClient client,
        CancellationToken ct)
    {
        if (!Guid.TryParse(peerId, out var pid)) return BadRequest("peerId invalid");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        var peer = config.RemoteServers.FirstOrDefault(p => p.Id == pid);
        if (peer is null) return NotFound();
        var rows = await client.FetchPeerDirectoryAsync(peer, ct).ConfigureAwait(false);
        return Ok(rows);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("I18n/Languages")]
    public IActionResult ListI18nLanguages()
    {
        // Enumerate every Assets.i18n.*.json embedded resource and return its locale code
        // along with the human-readable name pulled from the file's lang.name key.
        var asm = typeof(Plugin).Assembly;
        var prefix = typeof(Plugin).Namespace + ".Assets.i18n.";
        var rows = new System.Collections.Generic.List<object>();
        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal) || !resourceName.EndsWith(".json", StringComparison.Ordinal)) continue;
            var code = resourceName.Substring(prefix.Length, resourceName.Length - prefix.Length - ".json".Length);
            string displayName = code;
            try
            {
                using var s = asm.GetManifestResourceStream(resourceName);
                if (s is not null)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("lang.name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                        displayName = n.GetString() ?? code;
                }
            }
            catch { /* fall back to code */ }
            rows.Add(new { code, name = displayName });
        }
        return Ok(rows);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("I18n/{lang}")]
    public IActionResult GetI18nBundle(string lang)
    {
        if (string.IsNullOrEmpty(lang) || lang.Length > 16 || !System.Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z]{2,3}(-[A-Z]{2})?$"))
            return BadRequest("invalid lang code");
        var asm = typeof(Plugin).Assembly;
        var resourceName = typeof(Plugin).Namespace + ".Assets.i18n." + lang + ".json";
        var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return NotFound();
        Response.Headers["Cache-Control"] = "public, max-age=600";
        return File(stream, "application/json");
    }

    [AllowAnonymous]
    [HttpGet("Asset/{name}")]
    public IActionResult GetAsset(string name)
    {
        // Restrict to a handful of safe assets we ship in the plugin DLL. Resource name is
        // <namespace>.Assets.<file> so we hardcode the prefix and validate the filename.
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) return BadRequest();
        var allowed = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["logo.svg"] = "image/svg+xml",
            ["thumb.png"] = "image/png",
            ["jellymesh-item.js"] = "application/javascript"
        };
        if (!allowed.TryGetValue(name, out var mime)) return NotFound();
        var asm = typeof(Plugin).Assembly;
        var resourceName = typeof(Plugin).Namespace + ".Assets." + name;
        var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return NotFound();
        Response.Headers["Cache-Control"] = "public, max-age=600";
        return File(stream, mime);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Blocklist")]
    public IActionResult GetBlocklist()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        return Ok(config.BlockedPeerUrls);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("Blocklist")]
    public IActionResult Unblock([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest("url required");
        var config = Plugin.Instance?.Configuration;
        if (config is null) return StatusCode(500);
        lock (Plugin.ConfigWriteLock)
        {
            config.BlockedPeerUrls.RemoveAll(u => Services.PeerUrl.SameHost(u, url));
            Plugin.Instance?.SaveConfiguration();
        }
        return NoContent();
    }
}

public class CreateShareRequest
{
    public string? Label { get; set; }
    public List<string>? LibraryIds { get; set; }
    public string? AllowedHoursStart { get; set; }
    public string? AllowedHoursEnd { get; set; }
    public string? ScheduleTimeZoneId { get; set; }
    public List<string>? BlockedTags { get; set; }
    public string? MaxOfficialRating { get; set; }
    public bool StrictUnknownRating { get; set; }
    public string? BoundPeerUrl { get; set; }
    public bool CanRequestIntroductions { get; set; }
    public Configuration.IntroductionMintMode MintMode { get; set; } = Configuration.IntroductionMintMode.Request;
}

public class CreatePublicShareRequest
{
    public string? ItemId { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public int? MaxUses { get; set; }
}

public class IncomingRequestPayload
{
    public string? FromBaseUrl { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? Note { get; set; }
}

public class IntroducePayload
{
    public string? ForUrl { get; set; }
    public int HopCount { get; set; } = 1;
    public string? Note { get; set; }
}

public class IntroducedPayload
{
    public string? NewPeerUrl { get; set; }
    public string? NewPeerKey { get; set; }
    public string? IntroducedBy { get; set; }
    public int HopCount { get; set; } = 1;
    // When the introducer reaches the new peer through HTTP Basic auth (peer is behind a
    // reverse proxy), they forward the same credentials so the receiver can also reach it.
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPass { get; set; }
}

public class ReciprocityRequestPayload
{
    public string? FromBaseUrl { get; set; }
}

public class AdminIntroducePayload
{
    public string? PeerId { get; set; }
    public string? ForUrl { get; set; }
    public string? Note { get; set; }
    public bool AlsoForward { get; set; } = true;
}

public class SendRequestPayload
{
    public string? PeerId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public string? Note { get; set; }
}

public class SubtitleTrack
{
    public Guid PeerId { get; set; }
    public string? PeerName { get; set; }
    public string? RemoteItemId { get; set; }
    public string? MediaSourceId { get; set; }
    public int StreamIndex { get; set; }
    public string? Language { get; set; }
    public string? Codec { get; set; }
    public string? Title { get; set; }
    public bool IsExternal { get; set; }
    public string? ProxyUrl { get; set; }
}

public class FederatedSearchHit
{
    public Guid PeerId { get; set; }
    public string? PeerName { get; set; }
    public string? ItemId { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public int? Year { get; set; }
    public string? PrimaryImageUrl { get; set; }
}

public class PeerLibraryConfigPayload
{
    public string? Layout { get; set; }
    public List<Configuration.PeerLibrarySetting>? Settings { get; set; }
}

public class AccessRequestPayload
{
    public string? FromUrl { get; set; }
    public string? FromName { get; set; }
    public string? Message { get; set; }
    public string? Nonce { get; set; }
    public bool Mutual { get; set; }
    public string? InviteToken { get; set; }
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPass { get; set; }
}

public class RequestAccessPayload
{
    public string? TargetUrl { get; set; }
    public string? OurName { get; set; }
    public string? Message { get; set; }
    public bool Mutual { get; set; }
    public string? InviteToken { get; set; }
    public string? TargetBasicAuthUser { get; set; }
    public string? TargetBasicAuthPass { get; set; }
}

public class AccessGrantedPayload
{
    public string? OurBaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Nonce { get; set; }
    public bool Mutual { get; set; }
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPass { get; set; }
}

public class SendInvitePayload
{
    public string? TargetUrl { get; set; }
    public string? Message { get; set; }
    public bool Mutual { get; set; }
    public string? TargetBasicAuthUser { get; set; }
    public string? TargetBasicAuthPass { get; set; }
}

public class InviteOfferPayload
{
    public string? OurBaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Nonce { get; set; }
    public bool Mutual { get; set; }
    public string? Message { get; set; }
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPass { get; set; }
}

public class InviteAcceptedPayload
{
    public string? Nonce { get; set; }
    public string? ApiKey { get; set; }
    public bool Mutual { get; set; }
    public string? OurBaseUrl { get; set; }
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPass { get; set; }
}
