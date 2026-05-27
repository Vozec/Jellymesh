// Jellymesh client-side injections:
//   1. Share button on item details page (creates a public share link).
//   2. Per-peer library sections on the home page (under "My media").
//   3. Source + version badge on item details for federated items.
//   4. Link to the plugin config page in the dashboard left navigation drawer.
// Loaded by IndexHtmlInjector via a <script src> tag in Jellyfin's index.html.

(function () {
    'use strict';

    // ----- shared helpers ----------------------------------------------------
    function token() { return window.ApiClient && ApiClient.accessToken(); }
    function jApi(path, opts) {
        opts = opts || {};
        opts.headers = Object.assign({ 'X-Emby-Token': token() }, opts.headers || {});
        return fetch(path, opts).then((r) => r.ok ? (opts.raw ? r : r.json()) : r.text().then((t) => { throw new Error(`HTTP ${r.status}: ${t}`); }));
    }

    // ----- fetch hook: federated library transparency -----------------------
    // Trick: peer libraries are exposed as virtual VirtualFolders inside Jellyfin's SPA by
    // navigating to #/movies.html?topParentId=fedlib_{peerN}_{libId}. The native page issues
    // /Items?ParentId=fedlib_... and /Items/{id}/Images/... requests; we intercept those and
    // proxy to the peer via /Federation/Peers/.../Items + /Federation/Image. The peer's
    // item ids are surfaced as 'fed_{peerN}_{remoteItemId}' so the click chain on each card
    // round-trips through us. ImageTags is set to a non-empty sentinel so Jellyfin emits the
    // /Images/Primary URL we can intercept.
    const FED_LIB_PREFIX = 'fedlib_';
    const FED_ITEM_PREFIX = 'fed_';

    function parseFedLib(id) {
        if (!id || !id.startsWith(FED_LIB_PREFIX)) return null;
        const rest = id.substring(FED_LIB_PREFIX.length);
        const sep = rest.indexOf('_');
        if (sep <= 0) return null;
        return { peerId: hyphenateGuid(rest.substring(0, sep)), libId: rest.substring(sep + 1) };
    }
    function parseFedItem(id) {
        if (!id || !id.startsWith(FED_ITEM_PREFIX)) return null;
        const rest = id.substring(FED_ITEM_PREFIX.length);
        const sep = rest.indexOf('_');
        if (sep <= 0) return null;
        return { peerId: hyphenateGuid(rest.substring(0, sep)), itemId: rest.substring(sep + 1) };
    }
    function hyphenateGuid(s) {
        if (!s) return s;
        return s.length === 32 ? s.replace(/(.{8})(.{4})(.{4})(.{4})(.{12})/, '$1-$2-$3-$4-$5') : s;
    }

    function fakeFolderItem(peerId, lib) {
        return {
            Id: FED_LIB_PREFIX + peerId.replace(/-/g, '') + '_' + lib.id,
            Name: lib.name || 'Library',
            ServerId: (window.ApiClient && ApiClient.serverId && ApiClient.serverId()) || '',
            Type: 'CollectionFolder',
            CollectionType: lib.type || 'movies',
            IsFolder: true,
            ImageTags: {},
            BackdropImageTags: []
        };
    }
    function mapPeerItem(it, peerN) {
        const fedId = FED_ITEM_PREFIX + peerN + '_' + it.id;
        return {
            Id: fedId,
            Name: it.name,
            Type: it.type || 'Movie',
            MediaType: 'Video',
            ProductionYear: it.year,
            ServerId: (window.ApiClient && ApiClient.serverId && ApiClient.serverId()) || '',
            ImageTags: { Primary: 'fed' },
            BackdropImageTags: [],
            UserData: { Played: false, IsFavorite: false, PlaybackPositionTicks: 0, PlayCount: 0 },
            IsFolder: false
        };
    }

    function getQuery(url) {
        const q = url.indexOf('?');
        if (q < 0) return {};
        const out = {};
        url.substring(q + 1).split('&').forEach(function (p) {
            if (!p) return;
            const i = p.indexOf('=');
            if (i < 0) out[decodeURIComponent(p)] = '';
            else out[decodeURIComponent(p.substring(0, i))] = decodeURIComponent(p.substring(i + 1));
        });
        return out;
    }

    function jsonResponse(obj) {
        return new Response(JSON.stringify(obj), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }

    function rewriteImageUrl(url) {
        const m = url.match(/\/Items\/(fed_[0-9a-fA-F]{32}_[^/?]+)\/Images\/([^/?]+)/);
        if (!m) return null;
        const parsed = parseFedItem(m[1]);
        if (!parsed) return null;
        return `/Federation/Image/${parsed.peerId.replace(/-/g, '')}/${parsed.itemId}/${m[2]}?api_key=${encodeURIComponent(token())}`;
    }

    function handleHookedRequest(url, originalArgs) {
        // /Items?ParentId=fedlib_... or /Users/{uid}/Items?ParentId=fedlib_...
        const q = getQuery(url);
        if (q.ParentId && q.ParentId.startsWith(FED_LIB_PREFIX)) {
            const parsed = parseFedLib(q.ParentId);
            if (!parsed) return null;
            const limit = Math.min(parseInt(q.Limit, 10) || 100, 200);
            return jApi(`/Federation/Peers/${parsed.peerId}/Libraries/${encodeURIComponent(parsed.libId)}/Items?limit=${limit}`)
                .then((data) => {
                    const peerN = parsed.peerId.replace(/-/g, '');
                    const items = ((data && data.items) || []).map((it) => mapPeerItem(it, peerN));
                    return jsonResponse({ Items: items, TotalRecordCount: items.length, StartIndex: 0 });
                });
        }
        // /Items?ParentId=<local_lib> where the local lib has merged-in peer libs.
        // Forward original request to the server, then concat the peer items into the
        // response so the user sees them alongside their local items.
        if (q.ParentId && /^[0-9a-fA-F]{32}$/.test(q.ParentId)) {
            return ensureMergeConfig().then(async (settings) => {
                const merges = (settings || []).filter((s) =>
                    s.Enabled !== false &&
                    s.MergeWithLocalLibraryId &&
                    s.MergeWithLocalLibraryId.replace(/-/g, '') === q.ParentId);
                const origResp = await _origFetch.apply(window, originalArgs);
                if (merges.length === 0 || !origResp.ok) return origResp;
                const data = await origResp.clone().json().catch(() => null);
                if (!data || !Array.isArray(data.Items)) return origResp;
                const limit = Math.min(parseInt(q.Limit, 10) || 100, 200);
                for (const m of merges) {
                    try {
                        const peerData = await jApi(`/Federation/Peers/${m.PeerId}/Libraries/${encodeURIComponent(m.LibraryId)}/Items?limit=${limit}`);
                        const peerN = m.PeerId.replace(/-/g, '');
                        const items = ((peerData && peerData.items) || []).map((it) => mapPeerItem(it, peerN));
                        data.Items = data.Items.concat(items);
                        data.TotalRecordCount = (data.TotalRecordCount || 0) + items.length;
                    } catch (_) { /* peer offline mid-render; skip */ }
                }
                return jsonResponse(data);
            });
        }
        // /Users/{uid}/Items/fedlib_X  ->  return a fake CollectionFolder so movies.html
        // doesn't 400 when it fetches the library metadata.
        const libLookup = url.match(/\/Items\/(fedlib_[0-9a-fA-F]{32}_[^/?]+)(?:\?|$)/);
        if (libLookup) {
            const p = parseFedLib(libLookup[1]);
            if (p) {
                return jApi(`/Federation/Peers/${p.peerId}/Libraries`).then((libs) => {
                    const lib = (libs || []).find((l) => l.id === p.libId) || { id: p.libId, name: 'Library', type: 'movies' };
                    return jsonResponse(fakeFolderItem(p.peerId, lib));
                }).catch(() => jsonResponse(fakeFolderItem(p.peerId, { id: p.libId, name: 'Library', type: 'movies' })));
            }
        }
        // /Users/{uid}/Items/fed_X  -> stub item dto so the SPA doesn't 400 trying to fetch
        // the detail metadata. Real playback uses the local channel item; this stub only
        // exists for transient lookups like the back-button state.
        const itemLookup = url.match(/\/Items\/(fed_[0-9a-fA-F]{32}_[^/?]+)(?:\?|$)/);
        if (itemLookup) {
            const p = parseFedItem(itemLookup[1]);
            if (p) return jsonResponse(mapPeerItem({ id: p.itemId, name: '(federated)', type: 'Movie' }, p.peerId.replace(/-/g, '')));
        }
        // /Items/fed_X/Images/Y or any prefix-variant -> reroute to our image proxy.
        const rewritten = rewriteImageUrl(url);
        if (rewritten) return fetch(rewritten);
        return null;
    }

    const _origFetch = window.fetch;
    // Merge config cached in-process; refreshed at intervals so saves in the dashboard
    // panel propagate to all open tabs within 30 s. ensureMergeConfig() awaits the first
    // load so the intercepted request on the very first movies.html mount sees the
    // merge mappings (otherwise the cache is still null and merge silently no-ops).
    let mergeConfigCache = null;
    let mergeConfigInflight = null;
    function refreshMergeConfig() {
        mergeConfigInflight = jApi('/Federation/PeerLibraryConfig').then((cfg) => {
            mergeConfigCache = (cfg && cfg.settings) || [];
            mergeConfigInflight = null;
            return mergeConfigCache;
        }).catch(() => {
            mergeConfigCache = mergeConfigCache || [];
            mergeConfigInflight = null;
            return mergeConfigCache;
        });
        return mergeConfigInflight;
    }
    function ensureMergeConfig() {
        if (mergeConfigCache !== null) return Promise.resolve(mergeConfigCache);
        return mergeConfigInflight || refreshMergeConfig();
    }
    refreshMergeConfig();
    setInterval(refreshMergeConfig, 30000);

    window.fetch = function (input, init) {
        const url = typeof input === 'string' ? input : (input && input.url);
        const isFederated = url && (url.indexOf(FED_LIB_PREFIX) >= 0 || url.indexOf(FED_ITEM_PREFIX) >= 0);
        const looksLikeItemsList = url && /\/Items(\?|$)/.test(url) && url.indexOf('ParentId=') > 0;
        if (isFederated || looksLikeItemsList) {
            const hooked = handleHookedRequest(url, arguments);
            if (hooked) return Promise.resolve(hooked);
        }
        return _origFetch.apply(this, arguments);
    };

    // XMLHttpRequest path: Jellyfin's legacy ApiClient uses XHR for many endpoints, so the
    // fetch hook alone misses them. Intercept open() to rewrite federated URLs the same way.
    const _origXhrOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        if (typeof url === 'string' && url.indexOf(FED_ITEM_PREFIX) >= 0) {
            const rewritten = rewriteImageUrl(url);
            if (rewritten) {
                arguments[1] = rewritten;
                return _origXhrOpen.apply(this, arguments);
            }
        }
        // Items list and metadata XHRs are harder to rewrite synchronously here; we leave
        // them to flow into the fetch path or to fail benignly. The user-visible covers
        // and the items grid (handled via fetch by modern Jellyfin) are the priority.
        return _origXhrOpen.apply(this, arguments);
    };

    // ApiClient.getImageUrl is called by the SPA to build <img src> attribute values for
    // posters - those never go through fetch, so we patch the getter directly. When the
    // itemId is federated, return our proxy URL; otherwise fall back to the original.
    function patchApiClient() {
        const ac = window.ApiClient;
        if (!ac || ac._jmPatched) { setTimeout(patchApiClient, 200); return; }
        ac._jmPatched = true;
        if (typeof ac.getImageUrl === 'function') {
            const origGetImage = ac.getImageUrl.bind(ac);
            ac.getImageUrl = function (itemId, options) {
                if (typeof itemId === 'string' && itemId.startsWith(FED_ITEM_PREFIX)) {
                    const p = parseFedItem(itemId);
                    if (p) {
                        const type = (options && options.type) || 'Primary';
                        return `/Federation/Image/${p.peerId.replace(/-/g, '')}/${p.itemId}/${type}?api_key=${encodeURIComponent(token())}`;
                    }
                }
                return origGetImage(itemId, options);
            };
        }
        if (typeof ac.getScaledImageUrl === 'function') {
            const orig = ac.getScaledImageUrl.bind(ac);
            ac.getScaledImageUrl = function (itemId, options) {
                if (typeof itemId === 'string' && itemId.startsWith(FED_ITEM_PREFIX)) {
                    const p = parseFedItem(itemId);
                    if (p) {
                        const type = (options && options.type) || 'Primary';
                        return `/Federation/Image/${p.peerId.replace(/-/g, '')}/${p.itemId}/${type}?api_key=${encodeURIComponent(token())}`;
                    }
                }
                return orig(itemId, options);
            };
        }
    }
    setTimeout(patchApiClient, 200);

    // Last-resort: any <img> the SPA already rendered with src='/Items/fed_*/Images/*' (before
    // our ApiClient patch landed) shows a broken image. MutationObserver rewrites those src
    // attributes inline. Same logic also fires for background-image inline styles on
    // .cardImage elements.
    function rewriteFedImages(root) {
        root.querySelectorAll('img[src*="/Items/fed_"]').forEach((img) => {
            const newSrc = rewriteImageUrl(img.src);
            if (newSrc && newSrc !== img.src) img.src = newSrc;
        });
        root.querySelectorAll('[style*="/Items/fed_"]').forEach((el) => {
            const bg = el.style.backgroundImage;
            if (!bg) return;
            const m = bg.match(/url\(["']?([^"')]+)["']?\)/);
            if (!m) return;
            const newUrl = rewriteImageUrl(m[1]);
            if (newUrl && newUrl !== m[1]) el.style.backgroundImage = `url('${newUrl}')`;
        });
        // Native cards bound to a federated id: stamp a 'REMOTE' ribbon on top so the user
        // sees at a glance that it's not actually on their disk. We attach to .cardImageContainer
        // (Jellyfin's own poster container) so the band overlays the image area only.
        root.querySelectorAll('[data-id^="fed_"]').forEach((card) => {
            if (card.dataset.jmRibbon === 'yes') return;
            const host = card.querySelector('.cardImageContainer, .cardImage, .cardScalable');
            if (!host) return;
            const ribbon = document.createElement('div');
            ribbon.className = 'jm-remote-ribbon';
            ribbon.textContent = 'REMOTE';
            // Make sure the container can host an absolute child.
            if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
            host.appendChild(ribbon);
            card.dataset.jmRibbon = 'yes';
        });
    }
    function startImageRewriter() {
        rewriteFedImages(document);
        const obs = new MutationObserver((muts) => {
            for (const m of muts) {
                m.addedNodes.forEach((n) => {
                    if (n.nodeType !== 1) return;
                    rewriteFedImages(n);
                });
                if (m.type === 'attributes' && m.target instanceof Element) rewriteFedImages(m.target);
            }
        });
        obs.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['src', 'style'] });
    }
    if (document.body) startImageRewriter();
    else document.addEventListener('DOMContentLoaded', startImageRewriter);

    function ensureStyle() {
        if (document.getElementById('jm-style')) return;
        const s = document.createElement('style');
        s.id = 'jm-style';
        s.textContent = `
            #jm-share-btn { background: transparent !important; border: none !important; color: inherit !important; padding: 0.4em !important; margin: 0 0.25em !important; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; border-radius: 50%; width: 2.2em; height: 2.2em; }
            #jm-share-btn:hover { background: rgba(255,255,255,0.08) !important; }
            #jm-share-btn .material-icons { font-size: 1.4em; }
            .jm-share-overlay { display: none; position: fixed; inset: 0; background: rgba(0,0,0,0.72); z-index: 9999; align-items: center; justify-content: center; }
            .jm-share-overlay.open { display: flex; }
            .jm-share-card { background: #181818; color: #ddd; border-radius: 0.6em; width: min(420px, 92vw); box-shadow: 0 12px 50px rgba(0,0,0,0.7); overflow: hidden; }
            .jm-share-head { padding: 0.9em 1.1em; border-bottom: 1px solid #333; display: flex; justify-content: space-between; align-items: center; }
            .jm-share-head h3 { margin: 0; font-size: 1.05em; }
            .jm-share-head button { background: none; border: none; color: #aaa; font-size: 1.3em; cursor: pointer; }
            .jm-share-body { padding: 1em 1.1em; display: flex; flex-direction: column; gap: 0.8em; }
            .jm-share-body label { display: flex; flex-direction: column; gap: 0.2em; font-size: 0.9em; color: #bbb; }
            .jm-share-body input { background: #222; color: #fff; border: 1px solid #444; border-radius: 0.3em; padding: 0.4em 0.6em; font-size: 1em; }
            .jm-share-foot { padding: 0.7em 1.1em; border-top: 1px solid #333; display: flex; justify-content: flex-end; gap: 0.5em; }
            .jm-share-foot button { padding: 0.5em 1em; border-radius: 0.3em; border: 1px solid #555; background: #2a2a2a; color: #ddd; cursor: pointer; font-size: 0.95em; }
            .jm-share-foot button.primary { background: #3b6fa4; border-color: #3b6fa4; color: #fff; }
            .jm-share-foot button:hover { filter: brightness(1.15); }
            .jm-share-result { background: #1f2a1f; border: 1px solid #345; border-radius: 0.4em; padding: 0.6em; font-family: monospace; font-size: 0.8em; word-break: break-all; }
            .jm-toast { position: fixed; left: 50%; bottom: 5vh; transform: translateX(-50%); background: #2f7c4a; color: #fff; padding: 0.6em 1.2em; border-radius: 0.4em; box-shadow: 0 6px 20px rgba(0,0,0,0.4); z-index: 10000; opacity: 0; transition: opacity 0.25s; }
            .jm-toast.show { opacity: 1; }
            .jm-toast.error { background: #a23333; }

            /* Peer-source chip overlaid on otherwise-native Jellyfin cards. */
            .jm-card-badge { position: absolute; top: 0.35em; right: 0.35em; background: rgba(59,111,164,0.92); color: #fff; padding: 0.1em 0.5em; border-radius: 0.7em; font-size: 0.7em; font-weight: 600; z-index: 1; pointer-events: none; }
            /* Native Jellyfin cards (movies.html merged view) get this band added by JS. */
            .jm-remote-ribbon { position: absolute; top: 0; left: 0; right: 0; background: linear-gradient(180deg, rgba(59,111,164,0.95), rgba(59,111,164,0.7)); color: #fff; padding: 0.25em 0.5em; font-size: 0.72em; font-weight: 600; text-align: center; z-index: 2; pointer-events: none; letter-spacing: 0.04em; }

            /* Dashboard libraries panel */
            #jm-dashlibs { margin: 1.5em 0; padding: 1.3em 1.4em; background: linear-gradient(180deg, #1a1a1a 0%, #161616 100%); border: 1px solid #2e2e2e; border-radius: 0.6em; box-shadow: 0 1px 0 rgba(255,255,255,0.03) inset; }
            #jm-dashlibs h2 { margin: 0 0 0.2em; font-size: 1.4em; }
            #jm-dashlibs h2 .material-icons { vertical-align: middle; margin-right: 0.3em; color: #6aa6db; }
            #jm-dashlibs .jm-dashlibs-sub { color: #999; margin: 0 0 1.1em; font-size: 0.95em; }
            #jm-dashlibs .jm-toolbar { display: flex; align-items: center; gap: 0.8em; flex-wrap: wrap; margin-bottom: 1em; padding: 0.7em 0.9em; background: #1f1f1f; border: 1px solid #2e2e2e; border-radius: 0.4em; }
            #jm-dashlibs .jm-field { display: flex; align-items: center; gap: 0.5em; }
            #jm-dashlibs .jm-field-label { color: #aaa; font-size: 0.9em; }
            #jm-dashlibs select.jm-select { background: #222; color: #eee; border: 1px solid #3a3a3a; padding: 0.45em 2em 0.45em 0.7em; border-radius: 0.35em; appearance: none; -webkit-appearance: none; background-image: linear-gradient(45deg, transparent 50%, #888 50%), linear-gradient(135deg, #888 50%, transparent 50%); background-position: calc(100% - 14px) 50%, calc(100% - 9px) 50%; background-size: 5px 5px, 5px 5px; background-repeat: no-repeat; cursor: pointer; }
            #jm-dashlibs select.jm-select:focus, #jm-dashlibs select.jm-select:hover { border-color: #4a87c0; outline: none; }
            #jm-dashlibs .jm-btn { display: inline-flex; align-items: center; gap: 0.4em; background: #2a2a2a; color: #ddd; border: 1px solid #4a4a4a; border-radius: 0.35em; padding: 0.45em 1em; cursor: pointer; font: inherit; font-size: 0.92em; transition: background 0.15s, border-color 0.15s, transform 0.05s; }
            #jm-dashlibs .jm-btn:hover { background: #353535; border-color: #5a5a5a; }
            #jm-dashlibs .jm-btn:active { transform: scale(0.97); }
            #jm-dashlibs .jm-btn.primary { background: #3b6fa4; border-color: #3b6fa4; color: #fff; }
            #jm-dashlibs .jm-btn.primary:hover { background: #4682c0; border-color: #4682c0; }
            #jm-dashlibs .jm-btn .material-icons { font-size: 1.15em; }
            #jm-dashlibs .jm-peer-block { background: #1c1c1c; border: 1px solid #2e2e2e; border-radius: 0.5em; margin: 0.6em 0; overflow: hidden; }
            #jm-dashlibs .jm-peer-header { display: flex; align-items: center; gap: 0.6em; padding: 0.75em 1em; background: #232323; border-bottom: 1px solid #2e2e2e; }
            #jm-dashlibs .jm-peer-header h3 { margin: 0; font-size: 1.05em; flex: 1; }
            #jm-dashlibs .jm-peer-status { font-size: 0.78em; padding: 0.15em 0.6em; border-radius: 0.8em; font-weight: 600; }
            #jm-dashlibs .jm-peer-status.online { background: rgba(74,144,80,0.25); color: #b6e0ba; border: 1px solid rgba(74,144,80,0.5); }
            #jm-dashlibs .jm-peer-status.offline { background: rgba(180,60,60,0.2); color: #f0b0b0; border: 1px solid rgba(180,60,60,0.5); }
            #jm-dashlibs .jm-lib-table { padding: 0.4em 0.8em; }
            #jm-dashlibs .jm-lib-row { display: grid; grid-template-columns: 2fr repeat(3, auto) 1.5fr auto; gap: 0.9em; align-items: center; padding: 0.6em 0.4em; border-bottom: 1px solid #262626; }
            #jm-dashlibs .jm-lib-row:last-child { border-bottom: none; }
            #jm-dashlibs .jm-lib-name strong { display: block; }
            #jm-dashlibs .jm-lib-name small { color: #888; font-size: 0.8em; }
            #jm-dashlibs .jm-toggle { display: inline-flex; align-items: center; gap: 0.35em; color: #bbb; font-size: 0.88em; cursor: pointer; user-select: none; }
            #jm-dashlibs .jm-toggle input { accent-color: #4a87c0; width: 1.05em; height: 1.05em; cursor: pointer; }
            #jm-dashlibs .jm-empty { padding: 1em; color: #888; font-style: italic; text-align: center; }
            .jm-progress-host { display: flex; flex-direction: column; gap: 0.4em; padding: 0.6em 0.9em; background: #1c1c1c; border: 1px solid #2e2e2e; border-radius: 0.4em; margin: 0.6em 0; }
            .jm-progress-host[hidden] { display: none; }
            .jm-progress-row { display: grid; grid-template-columns: 1fr auto auto; gap: 0.5em; align-items: center; font-size: 0.88em; }
            .jm-progress-name { font-weight: 600; color: #ddd; }
            .jm-progress-detail { color: #888; font-size: 0.78em; }
            .jm-progress-pct { color: #9bc5ee; font-variant-numeric: tabular-nums; min-width: 3.5em; text-align: right; }
            .jm-progress-bar { grid-column: 1 / -1; height: 6px; background: #2a2a2a; border-radius: 3px; overflow: hidden; position: relative; }
            .jm-progress-bar > i { display: block; height: 100%; background: linear-gradient(90deg, #3b6fa4, #4a87c0); transition: width 0.4s ease; }
            .jm-progress-bar.done > i { background: #4a8c52; }
            .jm-progress-bar.failed > i { background: #c44; }
            .jm-progress-bar.skipped > i { background: #666; }

            /* Item details source badge */
            .jm-source-badge { display: inline-flex; align-items: center; gap: 0.35em; background: rgba(59,111,164,0.18); border: 1px solid rgba(59,111,164,0.55); color: #9bc5ee; padding: 0.2em 0.6em; border-radius: 0.4em; font-size: 0.78em; margin: 0.4em 0.4em 0 0; }
            .jm-source-badge .material-icons { font-size: 0.95em; }
            .jm-source-badge.local { background: rgba(74,144,80,0.18); border-color: rgba(74,144,80,0.55); color: #b6e0ba; }

            /* Dashboard left-nav link */
            .jm-nav-link { display: flex; align-items: center; padding: 0.75em 1.5em; color: inherit; text-decoration: none; }
            .jm-nav-link:hover { background: rgba(255,255,255,0.05); }
            .jm-nav-link .material-icons { margin-right: 1em; opacity: 0.7; }
        `;
        document.head.appendChild(s);
    }

    function toast(msg, kind) {
        const t = document.createElement('div');
        t.className = 'jm-toast' + (kind === 'error' ? ' error' : '');
        t.textContent = msg;
        if (kind === 'error') t.style.background = '#a23333';
        document.body.appendChild(t);
        requestAnimationFrame(() => t.classList.add('show'));
        setTimeout(() => { t.classList.remove('show'); setTimeout(() => t.remove(), 300); }, 2500);
    }

    function currentItemId() {
        const m = location.hash.match(/[?&]id=([0-9a-f]{32})/i);
        return m ? m[1] : null;
    }

    // ----- 1. Share button + modal ------------------------------------------
    function openShareModal(itemId) {
        document.getElementById('jm-share-modal')?.remove();
        const overlay = document.createElement('div');
        overlay.id = 'jm-share-modal';
        overlay.className = 'jm-share-overlay open';
        overlay.innerHTML = `
            <div class="jm-share-card">
                <div class="jm-share-head">
                    <h3>Share this item</h3>
                    <button type="button" data-close>&times;</button>
                </div>
                <div class="jm-share-body">
                    <label>Maximum uses (blank for unlimited)
                        <input id="jm-share-max" type="number" min="1" value="5" />
                    </label>
                    <label>Expires in (hours, blank for no expiry)
                        <input id="jm-share-exp" type="number" min="1" value="24" />
                    </label>
                    <div id="jm-share-out" style="display:none;"></div>
                </div>
                <div class="jm-share-foot">
                    <button type="button" data-close>Cancel</button>
                    <button type="button" class="primary" data-go>Generate link</button>
                </div>
            </div>
        `;
        document.body.appendChild(overlay);
        overlay.addEventListener('click', (e) => {
            const t = e.target;
            if (t === overlay || t.hasAttribute('data-close')) { overlay.remove(); return; }
            if (t.hasAttribute('data-go')) {
                const body = { ItemId: itemId };
                const n = parseInt(document.getElementById('jm-share-max').value, 10);
                if (!isNaN(n) && n > 0) body.MaxUses = n;
                const h = parseInt(document.getElementById('jm-share-exp').value, 10);
                if (!isNaN(h) && h > 0) body.ExpiresUtc = new Date(Date.now() + h * 3600 * 1000).toISOString();
                t.disabled = true; t.textContent = 'Generating...';
                jApi('/Federation/PublicShares', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
                    .then((resp) => {
                        const out = document.getElementById('jm-share-out');
                        out.className = 'jm-share-result';
                        out.style.display = 'block';
                        out.textContent = resp.url;
                        t.textContent = 'Copy & close';
                        t.disabled = false;
                        t.removeAttribute('data-go');
                        t.setAttribute('data-copy', resp.url);
                    })
                    .catch((err) => {
                        t.disabled = false; t.textContent = 'Generate link';
                        toast(`Share failed: ${err.message}`, 'error');
                    });
                return;
            }
            if (t.hasAttribute('data-copy')) {
                navigator.clipboard.writeText(t.getAttribute('data-copy')).catch(() => {});
                overlay.remove();
                toast('Link copied to clipboard');
            }
        });
    }

    function ensureShareButton() {
        if (!location.hash.startsWith('#/details')) return;
        const itemId = currentItemId();
        if (!itemId) return;
        if (document.getElementById('jm-share-btn')) return;
        const bar = document.querySelector('.detailPagePrimaryContainer .mainDetailButtons, .detailButtons, .detailPagePrimaryContainer');
        if (!bar) return;
        const btn = document.createElement('button');
        btn.id = 'jm-share-btn';
        btn.type = 'button';
        btn.title = 'Share via Jellymesh';
        btn.innerHTML = `<span class="material-icons">share</span>`;
        btn.addEventListener('click', (e) => { e.preventDefault(); openShareModal(itemId); });
        bar.appendChild(btn);
    }

    // ----- 2. Home-page sections per peer ------------------------------------
    function ensureHomeSections() {
        if (!/^#\/(home(\.html)?)?$/.test(location.hash) && location.hash !== '' && location.hash !== '#/') return;
        const homeView = document.querySelector('.homeSectionsContainer, .homePage');
        if (!homeView) return;
        if (homeView.dataset.jmInjected === 'yes') return;
        homeView.dataset.jmInjected = 'yes';

        // Load layout config + peer list in parallel.
        Promise.all([
            jApi('/Federation/PeerLibraryConfig').catch(() => ({ layout: 'SectionPerPeer', settings: [] })),
            jApi('/Federation/Stats')
        ]).then(([cfg, stats]) => {
            if (cfg.layout === 'Off') return;
            const peers = (stats.Peers || []).filter((p) => p.Enabled && p.Online);
            if (peers.length === 0) return;
            if (cfg.layout === 'OneSectionAllPeers') {
                renderCombinedSection(homeView, peers);
            } else {
                peers.forEach((peer) => renderPeerSection(homeView, peer));
            }
        }).catch(() => { delete homeView.dataset.jmInjected; });
    }

    // One section per peer, rendered as a row of LIBRARY cards (one tile per shared lib).
    // Clicking a library card opens an in-Jellymesh items modal. Mirrors how Jellyfin's
    // 'My Media' row shows one card per local library, not one per item.
    function renderPeerSection(host, peer) {
        jApi(`/Federation/Peers/${peer.Id}/Libraries?onlyEnabled=true`).then((libs) => {
            const visible = (libs || []).filter((l) => !l.hideFromHomepage);
            if (visible.length === 0) return;
            const section = buildSection(`Peer: ${escapeHtml(peer.Name || 'Peer')}`);
            host.appendChild(section);
            fillLibraryCards(section, peer, visible);
        }).catch(() => {});
    }

    // Single combined section. Same logic but every peer's libs in one row.
    function renderCombinedSection(host, peers) {
        const section = buildSection('From your friends');
        host.appendChild(section);
        const pool = [];
        Promise.all(peers.map((peer) =>
            jApi(`/Federation/Peers/${peer.Id}/Libraries?onlyEnabled=true`).then((libs) => {
                (libs || []).filter((l) => !l.hideFromHomepage).forEach((lib) => pool.push({ peer, lib }));
            }).catch(() => {})
        )).then(() => {
            if (pool.length === 0) { section.remove(); return; }
            fillLibraryCardsMixed(section, pool);
        });
    }

    function buildSection(titleHtml) {
        const section = document.createElement('div');
        section.className = 'verticalSection';
        section.innerHTML = `
            <div class="sectionTitleContainer sectionTitleContainer-cards padded-left">
                <h2 class="sectionTitle sectionTitle-cards">${titleHtml}</h2>
            </div>
            <div is="emby-itemscontainer" class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" data-monitor="" style="white-space:nowrap;overflow-x:auto;">
                <div class="jm-placeholder" style="padding:1em;color:#777;">Loading...</div>
            </div>
        `;
        return section;
    }

    // Library-level cards (My Media style): one card per shared lib. Click opens an items
    // browse overlay. Mirrors Jellyfin's home 'My Media' row, which is what users actually
    // expect after the 'My libraries / Their libraries' mental model.
    function fillLibraryCards(section, peer, libs) {
        const row = section.querySelector('.itemsContainer');
        if (!row) return;
        const apiKey = token();
        row.innerHTML = libs.map((lib) => buildLibraryCard(lib, peer, apiKey)).join('');
    }
    function fillLibraryCardsMixed(section, pool) {
        const row = section.querySelector('.itemsContainer');
        if (!row) return;
        const apiKey = token();
        row.innerHTML = pool.map(({ peer, lib }) => buildLibraryCard(lib, peer, apiKey)).join('');
    }

    // Library card: clicking navigates to Jellyfin's native movies.html with a fedlib_*
    // topParentId. Our fetch hook above intercepts the /Items?ParentId=fedlib_... requests
    // and proxies them to the peer, so Jellyfin's own page handles filtering, sorting,
    // pagination, hover, etc. - no custom overlay UI needed.
    function buildLibraryCard(lib, peer, apiKey) {
        const peerId = peer.Id;
        const libId = lib.id;
        const fedLibId = FED_LIB_PREFIX + peerId.replace(/-/g, '') + '_' + libId;
        const collectionType = (lib.type || 'movies').toLowerCase();
        // Use movies.html for movies, tvshows.html for series, list.html as fallback.
        const route = collectionType === 'tvshows' ? 'tvshows.html'
                    : collectionType === 'movies' ? 'movies.html'
                    : 'list.html';
        const href = `#/${route}?topParentId=${fedLibId}&collectionType=${collectionType}`;
        const imageUrl = `/Federation/Image/${peerId}/${libId}/Primary?api_key=${encodeURIComponent(apiKey)}`;
        const colorIdx = (libId.charCodeAt(0) + libId.charCodeAt(libId.length - 1)) % 6;
        const palette = ['#2b4d72', '#5a3a72', '#724a3a', '#3a724f', '#723a3a', '#3a6072'];
        const fallback = palette[colorIdx];
        return `
            <div class="card backdropCard backdropCard-scalable card-hoverable" data-jm-peer="${peerId}" data-jm-lib="${escapeHtml(libId)}" style="display:inline-block;white-space:normal;vertical-align:top;margin:0.3em;width:280px;">
                <div class="cardBox cardBox-bottompadded">
                    <div class="cardScalable">
                        <div class="cardPadder cardPadder-backdrop"></div>
                        <a class="cardImageContainer coveredImage cardContent" href="${href}" style="background:${fallback};">
                            <span class="jm-card-badge">${escapeHtml(peer.Name)}</span>
                            <div class="cardImage" style="background-image:url('${imageUrl}');background-size:cover;background-position:center;"></div>
                        </a>
                    </div>
                    <div class="cardText cardTextCentered cardText-first"><bdi>${escapeHtml(lib.name || 'Library')}</bdi></div>
                    <div class="cardText cardTextCentered cardText-secondary"><bdi>${escapeHtml(lib.type || 'mixed')}</bdi></div>
                </div>
            </div>
        `;
    }

    // Per-item card (used inside the library overlay + by the dashboard libraries panel for
    // previews). Items not yet matched to a local channel/movie are still shown (image +
    // badge + name) so the user sees what the peer has even before our sync round runs.
    function buildCard(it, peer, apiKey, ourServerId) {
        const localId = it.localId || '';
        const clickable = !!localId;
        const href = clickable ? `#/details?id=${localId}&serverId=${ourServerId}` : '#';
        // background-image URLs are fetched by the browser without any Jellyfin auth header,
        // so we tack on ?api_key which Jellyfin's auth pipeline accepts equivalent.
        const imageUrl = `${it.imageUrl}?api_key=${encodeURIComponent(apiKey)}`;
        const dataAttrs = clickable
            ? `data-action="link" data-id="${localId}" data-serverid="${ourServerId}" data-type="${escapeHtml(it.type || 'Movie')}" data-mediatype="Video" data-isfolder="false"`
            : `data-action="none" title="Not synced yet - run the federation sync task to make this clickable"`;
        const dimStyle = clickable ? '' : 'opacity:0.55;cursor:default;';
        const pendingBadge = clickable ? '' : '<span class="jm-card-badge jm-pending" style="left:0.35em;right:auto;background:rgba(180,140,40,0.9);">Not synced</span>';
        return `
            <div class="card overflowPortraitCard card-hoverable" data-id="${localId}" data-serverid="${ourServerId}" data-type="${escapeHtml(it.type || 'Movie')}" data-prefix="" style="display:inline-block;white-space:normal;vertical-align:top;margin:0.3em;width:150px;${dimStyle}">
                <div class="cardBox cardBox-bottompadded">
                    <div class="cardScalable">
                        <div class="cardPadder cardPadder-overflowPortrait"></div>
                        <a class="cardImageContainer coveredImage cardContent itemAction" href="${href}" ${dataAttrs}>
                            <span class="jm-card-badge">${escapeHtml(peer.Name)}</span>
                            ${pendingBadge}
                            <div class="cardImage" style="background-image:url('${imageUrl}');"></div>
                        </a>
                    </div>
                    <div class="cardText cardTextCentered cardText-first">
                        ${clickable
                            ? `<a class="itemAction" href="${href}" ${dataAttrs} style="color:inherit;text-decoration:none;"><bdi>${escapeHtml(it.name || '')}</bdi></a>`
                            : `<bdi>${escapeHtml(it.name || '')}</bdi>`}
                    </div>
                    <div class="cardText cardTextCentered cardText-secondary"><bdi>${it.year || ''}${it.version ? ' &middot; ' + escapeHtml(it.version) : ''}</bdi></div>
                </div>
            </div>
        `;
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    // Polls /Federation/Sync/Progress every 800ms while a sync round is active, rendering a
    // per-peer progress bar inside the supplied host. Calls onDone() once the round ends
    // (running=false). Multiple callers can poll concurrently; we attach a polling state
    // per host element to avoid duplicate intervals.
    function pollSyncProgress(host, onDone) {
        if (!host) return;
        if (host.dataset.jmPolling === 'yes') return;
        host.dataset.jmPolling = 'yes';
        let tickCount = 0;
        const interval = setInterval(() => {
            jApi('/Federation/Sync/Progress').then((p) => {
                tickCount++;
                if (!p.peers || p.peers.length === 0) {
                    // Nothing to show. If we've polled a few times and there's still no run,
                    // stop quietly.
                    if (!p.running && tickCount > 2) { stop(); }
                    host.hidden = true;
                    return;
                }
                host.hidden = false;
                host.innerHTML = renderProgressRows(p.peers);
                if (!p.running) {
                    // Linger 2s on the completed state so the user sees the green bars,
                    // then hide + call back to refresh whatever surface owns the host.
                    setTimeout(() => { host.hidden = true; stop(); }, 2000);
                }
            }).catch(() => {});
        }, 800);
        function stop() {
            clearInterval(interval);
            delete host.dataset.jmPolling;
            if (typeof onDone === 'function') onDone();
        }
    }

    function renderProgressRows(peers) {
        return peers.map((p) => {
            const cls = ['Done', 'Failed', 'Skipped'].includes(p.phase) ? p.phase.toLowerCase() : '';
            const itemsStr = p.itemsTotal > 0 ? `${p.itemsSeen} / ${p.itemsTotal}` : (p.itemsSeen ? String(p.itemsSeen) : '');
            const detail = [p.phase, p.detail, itemsStr].filter(Boolean).join(' · ');
            return `
                <div class="jm-progress-row">
                    <div>
                        <div class="jm-progress-name">${escapeHtml(p.peerName)}</div>
                        <div class="jm-progress-detail">${escapeHtml(detail)}</div>
                    </div>
                    <div></div>
                    <div class="jm-progress-pct">${p.percent}%</div>
                    <div class="jm-progress-bar ${cls}"><i style="width:${p.percent}%;"></i></div>
                </div>
            `;
        }).join('');
    }

    // ----- 3. Item details source badge --------------------------------------
    function ensureItemBadge() {
        if (!location.hash.startsWith('#/details')) return;
        const itemId = currentItemId();
        if (!itemId) return;
        const head = document.querySelector('.detailPagePrimaryContainer h3.itemName, .detailPagePrimaryContainer .nameContainer, .detailPagePrimaryContainer');
        if (!head) return;
        if (head.querySelector('.jm-source-badge')) return;
        jApi(`/Items/${itemId}?Fields=ProviderIds,SourceType,MediaSources`).then((item) => {
            if (!item) return;
            const badges = [];
            const isChannelFederated = item.SourceType === 'Channel' && item.ChannelName === 'Friends Library';
            if (isChannelFederated) {
                // ExternalId of the channel item is fed_<peerGuid>_<remoteItemId>.
                const ext = item.ExternalId || item.Id;
                const m = ext && ext.match(/^fed_([0-9a-f]{32})_/i);
                if (m) {
                    const peerGuidN = m[1];
                    jApi('/Federation/Stats').then((stats) => {
                        const peer = (stats.Peers || []).find((p) => (p.Id || '').replace(/-/g, '') === peerGuidN);
                        const peerName = peer ? peer.Name : 'a peer';
                        appendBadge(head, `<span class="material-icons">cloud</span>From ${escapeHtml(peerName)}`);
                        const v = primaryVersionLabel(item);
                        if (v) appendBadge(head, v, false);
                    });
                }
                return;
            }
            // Local item: still show "Local" + version, plus any federated alt sources
            appendBadge(head, `<span class="material-icons">computer</span>Local`, true);
            const v = primaryVersionLabel(item);
            if (v) appendBadge(head, v, true);
            const fedAlts = (item.MediaSources || []).filter((ms) => ms.IsRemote || (ms.Name || '').startsWith('['));
            fedAlts.forEach((ms) => appendBadge(head, `<span class="material-icons">cloud</span>${escapeHtml(ms.Name || 'Peer source')}`, false));
        }).catch(() => {});
    }

    function appendBadge(host, htmlContent, local) {
        const span = document.createElement('span');
        span.className = 'jm-source-badge' + (local ? ' local' : '');
        span.innerHTML = htmlContent;
        host.appendChild(span);
    }

    function primaryVersionLabel(item) {
        const ms = (item.MediaSources || [])[0];
        if (!ms) return null;
        const v = (ms.MediaStreams || []).find((s) => s.Type === 'Video');
        if (!v) return null;
        const parts = [];
        if (v.Height) parts.push(v.Height + 'p');
        if (v.Codec) parts.push(v.Codec.toUpperCase());
        if (ms.Container) parts.push(ms.Container.toUpperCase());
        return parts.join(' ');
    }

    // ----- 4b. Dashboard / libraries panel ----------------------------------
    function ensureDashboardLibrariesPanel() {
        if (!location.hash.startsWith('#/dashboard/libraries')) return;
        if (document.getElementById('jm-dashlibs')) return;
        // The Jellyfin libraries page renders 'addLibrary' / VirtualFolders list inside a
        // .content-primary. We append our panel right after it.
        const host = document.querySelector('.content-primary') || document.querySelector('.libraryPage');
        if (!host) return;
        const panel = document.createElement('div');
        panel.id = 'jm-dashlibs';
        panel.innerHTML = `
            <h2><span class="material-icons">hub</span>Federated libraries</h2>
            <p class="jm-dashlibs-sub">Libraries shared by your peers. Toggle visibility, hide from the home page, or merge a peer library into one of your local libraries.</p>
            <div class="jm-toolbar">
                <div class="jm-field">
                    <span class="jm-field-label">Home layout</span>
                    <select id="jm-layout" class="jm-select">
                        <option value="SectionPerPeer">One section per peer</option>
                        <option value="OneSectionAllPeers">One section for all peers</option>
                        <option value="Off">Hide federated content from home</option>
                    </select>
                </div>
                <button type="button" class="jm-btn" id="jm-libs-refresh" style="margin-left:auto;"><span class="material-icons">refresh</span>Refresh peers</button>
                <button type="button" class="jm-btn primary" id="jm-libs-sync"><span class="material-icons">sync</span>Sync now</button>
            </div>
            <div id="jm-dashlibs-progress" class="jm-progress-host" hidden></div>
            <div id="jm-dashlibs-body"><div class="jm-empty">Loading...</div></div>
        `;
        host.appendChild(panel);
        document.getElementById('jm-libs-refresh').addEventListener('click', () => loadDashlibs(true));
        document.getElementById('jm-libs-sync').addEventListener('click', (e) => {
            const btn = e.currentTarget;
            btn.disabled = true;
            jApi('/Federation/Sync/Trigger', { method: 'POST' })
                .then(() => { toast('Sync started.'); pollSyncProgress(document.getElementById('jm-dashlibs-progress'), () => { loadDashlibs(true); btn.disabled = false; }); })
                .catch((err) => { toast(`Sync failed: ${err.message}`, 'error'); btn.disabled = false; });
        });

        // Auto-show progress if a sync is already running when the panel opens.
        pollSyncProgress(document.getElementById('jm-dashlibs-progress'), null);
        document.getElementById('jm-layout').addEventListener('change', (e) => {
            jApi('/Federation/PeerLibraryConfig', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ Layout: e.target.value }) })
                .then(() => toast('Saved.'))
                .catch(() => toast('Save failed.', 'error'));
        });
        loadDashlibs(false);
    }

    function loadDashlibs(forceRefresh) {
        const body = document.getElementById('jm-dashlibs-body');
        if (!body) return;
        Promise.all([
            jApi('/Federation/Stats'),
            jApi('/Federation/PeerLibraryConfig'),
            jApi('/Library/VirtualFolders')
        ]).then(([stats, cfg, localLibs]) => {
            const layoutSel = document.getElementById('jm-layout');
            if (layoutSel) layoutSel.value = cfg.layout || 'SectionPerPeer';
            const peers = (stats.Peers || []).filter((p) => p.Enabled);
            if (peers.length === 0) { body.innerHTML = '<em style="color:#777;">No peers configured.</em>'; return; }
            body.innerHTML = '';
            peers.forEach((peer) => {
                const block = document.createElement('div');
                block.className = 'jm-peer-block';
                block.innerHTML = `
                    <div class="jm-peer-header">
                        <h3>${escapeHtml(peer.Name)}</h3>
                        <span class="jm-peer-status ${peer.Online ? 'online' : 'offline'}">${peer.Online ? 'online' : 'offline'}</span>
                    </div>
                    <div class="jm-lib-table" data-jm-libs><div class="jm-empty">Loading...</div></div>
                `;
                body.appendChild(block);
                jApi(`/Federation/Peers/${peer.Id}/Libraries${forceRefresh ? '?refresh=1' : ''}`).then((libs) => {
                    const tbl = block.querySelector('[data-jm-libs]');
                    if (!libs || libs.length === 0) { tbl.innerHTML = '<div class="jm-empty">No libraries shared (or peer offline).</div>'; return; }
                    tbl.innerHTML = libs.map((lib) => renderLibRow(peer, lib, cfg.settings || [], localLibs || [])).join('');
                }).catch(() => { block.querySelector('[data-jm-libs]').innerHTML = '<div class="jm-empty">Unreachable.</div>'; });
            });
        }).catch(() => { body.innerHTML = '<div class="jm-empty" style="color:#e88;">Failed to load peer/library config.</div>'; });
    }

    function renderLibRow(peer, lib, settings, localLibs) {
        const setting = settings.find((s) => s.PeerId === peer.Id && s.LibraryId === lib.id) || {};
        const enabled = setting.Enabled !== false;
        const hidden = !!setting.HideFromHomepage;
        const mergeTarget = setting.MergeWithLocalLibraryId || '';
        const localOpts = ['<option value="">No merge</option>']
            .concat((localLibs || []).map((ll) => `<option value="${escapeHtml(ll.ItemId)}" ${mergeTarget === ll.ItemId ? 'selected' : ''}>${escapeHtml(ll.Name)}</option>`))
            .join('');
        return `
            <div class="jm-lib-row" data-jm-peer="${peer.Id}" data-jm-lib="${escapeHtml(lib.id)}">
                <div class="jm-lib-name">
                    <strong>${escapeHtml(lib.name)}</strong>
                    <small>${escapeHtml(lib.type || 'mixed')}</small>
                </div>
                <label class="jm-toggle"><input type="checkbox" class="jm-lib-enabled" ${enabled ? 'checked' : ''} /> Enabled</label>
                <label class="jm-toggle"><input type="checkbox" class="jm-lib-hide" ${hidden ? 'checked' : ''} /> Hide from home</label>
                <span></span>
                <label class="jm-toggle" style="gap:0.5em;">
                    <span style="color:#aaa;font-size:0.88em;">Merge into</span>
                    <select class="jm-select jm-lib-merge">${localOpts}</select>
                </label>
                <button type="button" class="jm-btn primary jm-lib-save"><span class="material-icons">check</span>Save</button>
            </div>
        `;
    }

    // Save handler delegated on the dashboard panel.
    document.addEventListener('click', function (e) {
        if (!e.target.classList || !e.target.classList.contains('jm-lib-save')) return;
        const row = e.target.closest('[data-jm-peer]');
        if (!row) return;
        const peerId = row.dataset.jmPeer;
        const libId = row.dataset.jmLib;
        const setting = {
            PeerId: peerId,
            LibraryId: libId,
            Enabled: row.querySelector('.jm-lib-enabled').checked,
            HideFromHomepage: row.querySelector('.jm-lib-hide').checked,
            MergeWithLocalLibraryId: row.querySelector('.jm-lib-merge').value || null
        };
        jApi('/Federation/PeerLibraryConfig', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ Settings: [setting] }) })
            .then(() => toast('Saved.'))
            .catch(() => toast('Save failed.', 'error'));
    });

    // ----- 4. Dashboard nav link --------------------------------------------
    // Jellyfin 10.10's admin drawer is a MUI list. The 'Mes extensions' / 'My Plugins' anchor
    // lives directly inside <ul aria-labelledby="plugins-subheader">. We clone its className
    // (minus the active 'Mui-selected' flag) and substitute the SVG icon + label so our row
    // inherits exactly the same styling, hover and active behaviour.
    function ensureNavLink() {
        if (document.getElementById('jm-nav-link')) return;
        const myExt = document.querySelector('a[href="#/dashboard/plugins"]');
        if (!myExt) return;
        const link = document.createElement('a');
        link.id = 'jm-nav-link';
        link.className = (myExt.className || '').replace(/\bMui-selected\b/g, '').replace(/\s+/g, ' ').trim();
        link.href = '#/configurationpage?name=Jellymesh';
        link.tabIndex = 0;
        // Reuse the existing icon container + text container classes so spacing stays consistent.
        const iconContainer = myExt.querySelector('.MuiListItemIcon-root');
        const textContainer = myExt.querySelector('.MuiListItemText-root');
        const textSpan = textContainer ? textContainer.querySelector('span') : null;
        const iconCls = iconContainer ? iconContainer.className : 'MuiListItemIcon-root';
        const textRootCls = textContainer ? textContainer.className : 'MuiListItemText-root';
        const textSpanCls = textSpan ? textSpan.className : 'MuiTypography-root MuiTypography-body1 MuiListItemText-primary';
        link.innerHTML = `
            <div class="${iconCls}">
                <img src="/Federation/Asset/logo.svg" alt="" style="width:1.5em;height:1.5em;object-fit:contain;" />
            </div>
            <div class="${textRootCls}"><span class="${textSpanCls}">Jellymesh</span></div>
            <span class="MuiTouchRipple-root"></span>
        `;
        myExt.parentNode.insertBefore(link, myExt.nextSibling);
    }

    // ----- 5. Tick loop ------------------------------------------------------
    function tick() {
        ensureStyle();
        ensureShareButton();
        ensureItemBadge();
        ensureHomeSections();
        ensureNavLink();
        ensureDashboardLibrariesPanel();
    }

    document.addEventListener('DOMContentLoaded', () => {
        ensureStyle();
        setInterval(tick, 800);
    });
    window.addEventListener('hashchange', () => setTimeout(tick, 400));
})();
