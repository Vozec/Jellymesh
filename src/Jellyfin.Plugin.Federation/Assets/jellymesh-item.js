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

    // Reproduce Jellyfin's home-page section markup so our injected sections inherit every
    // built-in style + the horizontal scroller behaviour automatically. Skips peers whose
    // libs are all disabled/hidden or whose lib listing fails.
    function renderPeerSection(host, peer) {
        jApi(`/Federation/Peers/${peer.Id}/Libraries?onlyEnabled=true`).then((libs) => {
            const visible = (libs || []).filter((l) => !l.hideFromHomepage);
            if (visible.length === 0) return; // skip section entirely - user asked
            visible.forEach((lib) => {
                const section = buildSection(`${escapeHtml(lib.name || 'Library')} <span style="opacity:0.65;font-weight:400;">&middot; ${escapeHtml(peer.Name || 'Peer')}</span>`);
                host.appendChild(section);
                jApi(`/Federation/Peers/${peer.Id}/Libraries/${encodeURIComponent(lib.id)}/Items?limit=18`)
                    .then((data) => fillSectionCards(section, peer, data.items || []))
                    .catch(() => { section.querySelector('.jm-placeholder').textContent = 'Cannot load items.'; });
            });
        }).catch(() => { /* peer offline mid-render */ });
    }

    // Single combined 'From your friends' section that pools items from every enabled+visible
    // lib across all peers.
    function renderCombinedSection(host, peers) {
        const section = buildSection('From your friends');
        host.appendChild(section);
        const pool = [];
        Promise.all(peers.map((peer) =>
            jApi(`/Federation/Peers/${peer.Id}/Libraries?onlyEnabled=true`).then((libs) => {
                const visible = (libs || []).filter((l) => !l.hideFromHomepage);
                return Promise.all(visible.map((lib) =>
                    jApi(`/Federation/Peers/${peer.Id}/Libraries/${encodeURIComponent(lib.id)}/Items?limit=12`)
                        .then((data) => (data.items || []).forEach((it) => pool.push({ peer, it })))
                        .catch(() => {})
                ));
            }).catch(() => {})
        )).then(() => {
            if (pool.length === 0) { section.remove(); return; }
            fillSectionCardsMixed(section, pool);
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

    function fillSectionCardsMixed(section, pool) {
        const row = section.querySelector('.itemsContainer');
        if (!row) return;
        const apiKey = token();
        const ourServerId = (window.ApiClient && (ApiClient.serverId ? ApiClient.serverId() : (ApiClient.serverInfo() || {}).Id)) || '';
        const cards = pool.map(({ peer, it }) => buildCard(it, peer, apiKey, ourServerId)).filter(Boolean);
        row.innerHTML = cards.length ? cards.join('') : '<div class="jm-placeholder" style="padding:1em;color:#777;">No items.</div>';
    }

    function fillSectionCards(section, peer, items) {
        const row = section.querySelector('.itemsContainer');
        if (!row) return;
        const apiKey = token();
        const ourServerId = (window.ApiClient && (ApiClient.serverId ? ApiClient.serverId() : (ApiClient.serverInfo() || {}).Id)) || '';
        const cards = items.map((it) => buildCard(it, peer, apiKey, ourServerId)).filter(Boolean);
        row.innerHTML = cards.length
            ? cards.join('')
            : '<div class="jm-placeholder" style="padding:1em;color:#777;">No playable items (waiting for sync).</div>';
    }

    // Returns null when the peer item has no local equivalent yet (channel sync hasn't run
    // or dedup hid it). Skipping the card is better than rendering a dead href='#'.
    function buildCard(it, peer, apiKey, ourServerId) {
        const localId = it.localId || '';
        if (!localId) return null;
        const href = `#/details?id=${localId}&serverId=${ourServerId}`;
        // background-image URLs are fetched by the browser without any Jellyfin auth header,
        // so we tack on ?api_key which Jellyfin's auth pipeline accepts equivalent.
        const imageUrl = `${it.imageUrl}?api_key=${encodeURIComponent(apiKey)}`;
        const dataAttrs = `data-action="link" data-id="${localId}" data-serverid="${ourServerId}" data-type="${escapeHtml(it.type || 'Movie')}" data-mediatype="Video" data-isfolder="false"`;
        return `
            <div class="card overflowPortraitCard card-hoverable" data-id="${localId}" data-serverid="${ourServerId}" data-type="${escapeHtml(it.type || 'Movie')}" data-prefix="" style="display:inline-block;white-space:normal;vertical-align:top;margin:0.3em;width:150px;">
                <div class="cardBox cardBox-bottompadded">
                    <div class="cardScalable">
                        <div class="cardPadder cardPadder-overflowPortrait"></div>
                        <a class="cardImageContainer coveredImage cardContent itemAction" href="${href}" ${dataAttrs}>
                            <span class="jm-card-badge">${escapeHtml(peer.Name)}</span>
                            <div class="cardImage" style="background-image:url('${imageUrl}');"></div>
                        </a>
                    </div>
                    <div class="cardText cardTextCentered cardText-first">
                        <a class="itemAction" href="${href}" ${dataAttrs} style="color:inherit;text-decoration:none;"><bdi>${escapeHtml(it.name || '')}</bdi></a>
                    </div>
                    <div class="cardText cardTextCentered cardText-secondary"><bdi>${it.year || ''}${it.version ? ' &middot; ' + escapeHtml(it.version) : ''}</bdi></div>
                </div>
            </div>
        `;
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
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
        panel.style.cssText = 'margin-top:2em;padding-top:1em;border-top:1px solid #333;';
        panel.innerHTML = `
            <h2 style="margin-bottom:0.3em;">Federated libraries</h2>
            <p style="color:#aaa;margin:0 0 1em;">Libraries shared by your peers. Toggle visibility, hide from home, or merge into one of your local libraries.</p>
            <div style="margin-bottom:0.8em;display:flex;align-items:center;gap:0.6em;flex-wrap:wrap;">
                <label style="display:flex;align-items:center;gap:0.4em;">
                    <span style="color:#bbb;">Home layout:</span>
                    <select id="jm-layout" style="background:#222;color:#eee;border:1px solid #444;padding:0.3em 0.5em;border-radius:0.3em;">
                        <option value="SectionPerPeer">One section per peer</option>
                        <option value="OneSectionAllPeers">One section for all peers</option>
                        <option value="Off">Hide federated content from home</option>
                    </select>
                </label>
                <button type="button" class="raised" id="jm-libs-refresh" style="margin-left:auto;background:#2a2a2a;color:#ddd;border:1px solid #555;border-radius:0.3em;padding:0.4em 0.9em;cursor:pointer;">Refresh peer lists</button>
            </div>
            <div id="jm-dashlibs-body"><em style="color:#777;">Loading...</em></div>
        `;
        host.appendChild(panel);
        document.getElementById('jm-libs-refresh').addEventListener('click', () => loadDashlibs(true));
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
                block.style.cssText = 'background:#1c1c1c;border:1px solid #333;border-radius:0.4em;margin:0.6em 0;padding:0.8em 1em;';
                block.innerHTML = `<h3 style="margin:0 0 0.4em;">${escapeHtml(peer.Name)} <span style="font-size:0.85em;color:${peer.Online ? '#8c8' : '#c88'};">${peer.Online ? 'online' : 'offline'}</span></h3><div data-jm-libs><em style="color:#777;">Loading...</em></div>`;
                body.appendChild(block);
                jApi(`/Federation/Peers/${peer.Id}/Libraries${forceRefresh ? '?refresh=1' : ''}`).then((libs) => {
                    if (!libs || libs.length === 0) { block.querySelector('[data-jm-libs]').innerHTML = '<em style="color:#777;">No libraries shared (or peer offline).</em>'; return; }
                    block.querySelector('[data-jm-libs]').innerHTML = libs.map((lib) => renderLibRow(peer, lib, cfg.settings || [], localLibs || [])).join('');
                }).catch(() => { block.querySelector('[data-jm-libs]').innerHTML = '<em style="color:#888;">Unreachable.</em>'; });
            });
        }).catch(() => { body.innerHTML = '<em style="color:#c88;">Failed to load peer/library config.</em>'; });
    }

    function renderLibRow(peer, lib, settings, localLibs) {
        const setting = settings.find((s) => s.PeerId === peer.Id && s.LibraryId === lib.id) || {};
        const enabled = setting.Enabled !== false;
        const hidden = !!setting.HideFromHomepage;
        const mergeTarget = setting.MergeWithLocalLibraryId || '';
        const localOpts = ['<option value="">(no merge)</option>']
            .concat((localLibs || []).map((ll) => `<option value="${escapeHtml(ll.ItemId)}" ${mergeTarget === ll.ItemId ? 'selected' : ''}>${escapeHtml(ll.Name)}</option>`))
            .join('');
        return `
            <div style="display:grid;grid-template-columns:1.4fr auto auto 1.6fr auto;gap:0.6em;align-items:center;padding:0.4em 0;border-bottom:1px solid #2a2a2a;"
                 data-jm-peer="${peer.Id}" data-jm-lib="${escapeHtml(lib.id)}">
                <div><strong>${escapeHtml(lib.name)}</strong> <span style="color:#888;font-size:0.85em;">${escapeHtml(lib.type || 'mixed')}</span></div>
                <label style="display:flex;align-items:center;gap:0.3em;color:#bbb;"><input type="checkbox" class="jm-lib-enabled" ${enabled ? 'checked' : ''} /> Enabled</label>
                <label style="display:flex;align-items:center;gap:0.3em;color:#bbb;"><input type="checkbox" class="jm-lib-hide" ${hidden ? 'checked' : ''} /> Hide from home</label>
                <label style="display:flex;align-items:center;gap:0.3em;color:#bbb;">Merge into:
                    <select class="jm-lib-merge" style="background:#222;color:#eee;border:1px solid #444;border-radius:0.3em;padding:0.25em 0.4em;">${localOpts}</select>
                </label>
                <button type="button" class="jm-lib-save" style="background:#3b6fa4;color:#fff;border:none;border-radius:0.3em;padding:0.4em 0.8em;cursor:pointer;">Save</button>
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
    function ensureNavLink() {
        if (document.getElementById('jm-nav-link')) return;
        // The drawer's "My Extensions" entry has data-itemid='myplugins' or anchor text matching.
        const drawer = document.querySelector('.mainDrawer, .drawer-content, .dashboardDocument .navMenuContainer');
        if (!drawer) return;
        const myExt = Array.from(drawer.querySelectorAll('a, .navMenuOption')).find((el) => {
            const href = el.getAttribute('href') || '';
            const txt = (el.textContent || '').trim().toLowerCase();
            return href.includes('/installedplugins') || href.includes('/dashboard/plugins') || txt === 'my plugins' || txt === 'mes extensions' || txt === 'mes plugins';
        });
        if (!myExt) return;
        const link = document.createElement('a');
        link.id = 'jm-nav-link';
        link.className = myExt.className || 'jm-nav-link';
        link.href = '#/configurationpage?name=Jellymesh';
        link.innerHTML = `<span class="material-icons">hub</span><span>Jellymesh</span>`;
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
