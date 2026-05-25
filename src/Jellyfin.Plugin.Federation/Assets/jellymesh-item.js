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

            /* Home-page peer sections */
            .jm-peer-section { padding: 1.2em 3.3% 0; }
            .jm-peer-head { display: flex; align-items: center; gap: 0.5em; margin-bottom: 0.5em; }
            .jm-peer-head h2 { margin: 0; font-size: 1.15em; }
            .jm-peer-chip { font-size: 0.7em; background: #3b6fa4; color: #fff; padding: 0.15em 0.55em; border-radius: 0.7em; }
            .jm-lib-block { margin: 0.6em 0 1em; }
            .jm-lib-block h3 { margin: 0 0 0.4em; font-size: 0.95em; color: #aaa; font-weight: 500; }
            .jm-card-row { display: flex; gap: 0.6em; overflow-x: auto; padding-bottom: 0.5em; scrollbar-width: thin; }
            .jm-card-row::-webkit-scrollbar { height: 6px; }
            .jm-card-row::-webkit-scrollbar-thumb { background: #444; border-radius: 3px; }
            .jm-card { flex: 0 0 150px; background: #1c1c1c; border-radius: 0.4em; overflow: hidden; cursor: default; position: relative; }
            .jm-card img { width: 100%; height: 225px; object-fit: cover; background: #111; display: block; }
            .jm-card .jm-card-info { padding: 0.4em 0.5em; }
            .jm-card .jm-card-name { font-size: 0.85em; color: #eee; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .jm-card .jm-card-meta { font-size: 0.7em; color: #888; margin-top: 0.15em; }
            .jm-card .jm-card-badge { position: absolute; top: 0.3em; right: 0.3em; background: rgba(59,111,164,0.92); color: #fff; padding: 0.1em 0.45em; border-radius: 0.6em; font-size: 0.65em; font-weight: 600; }

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
        const homeView = document.querySelector('.homeSectionsContainer, .libraryPage, .view[data-type="home"]') || document.querySelector('.homePage');
        if (!homeView) return;
        if (homeView.dataset.jmInjected === 'yes') return;
        homeView.dataset.jmInjected = 'yes';

        // Container we append our sections into. We piggyback on the home view's last child so
        // we appear AFTER 'My media' / latest movies.
        const host = document.createElement('div');
        host.id = 'jm-home-host';
        host.style.cssText = 'margin-top:1em;';
        homeView.appendChild(host);

        jApi('/Federation/Stats').then((stats) => {
            const peers = (stats.Peers || []).filter((p) => p.Enabled && p.Online);
            if (peers.length === 0) {
                host.dataset.jmEmpty = 'yes';
                return;
            }
            peers.forEach((peer) => renderPeerSection(host, peer));
        }).catch(() => { delete homeView.dataset.jmInjected; });
    }

    function renderPeerSection(host, peer) {
        const section = document.createElement('section');
        section.className = 'jm-peer-section';
        section.dataset.peerId = peer.Id;
        section.innerHTML = `
            <div class="jm-peer-head">
                <h2>${escapeHtml(peer.Name || 'Peer')}</h2>
                <span class="jm-peer-chip">Federated</span>
            </div>
            <div class="jm-peer-libs"></div>
        `;
        host.appendChild(section);

        const libsHost = section.querySelector('.jm-peer-libs');
        jApi('/Federation/Peers/' + peer.Id + '/Libraries').then((libs) => {
            if (!libs || libs.length === 0) {
                libsHost.innerHTML = '<em style="color:#777;">No shared libraries.</em>';
                return;
            }
            libs.forEach((lib) => {
                const block = document.createElement('div');
                block.className = 'jm-lib-block';
                block.innerHTML = `<h3>${escapeHtml(lib.name || 'Library')}</h3><div class="jm-card-row"><em style="color:#777;">Loading...</em></div>`;
                libsHost.appendChild(block);
                jApi(`/Federation/Peers/${peer.Id}/Libraries/${encodeURIComponent(lib.id)}/Items?limit=18`).then((data) => {
                    const row = block.querySelector('.jm-card-row');
                    const items = (data && data.items) || [];
                    if (items.length === 0) {
                        row.innerHTML = '<em style="color:#777;">Empty.</em>';
                        return;
                    }
                    row.innerHTML = items.map((it) => `
                        <div class="jm-card" data-peer="${escapeHtml(peer.Id)}" data-item="${escapeHtml(it.id)}">
                            <span class="jm-card-badge">${escapeHtml(peer.Name)}</span>
                            <img loading="lazy" src="${escapeHtml(it.imageUrl)}" alt="" onerror="this.style.background='#222';this.removeAttribute('src');" />
                            <div class="jm-card-info">
                                <div class="jm-card-name">${escapeHtml(it.name || '')}</div>
                                <div class="jm-card-meta">${it.year || ''}${it.version ? ' &middot; ' + escapeHtml(it.version) : ''}</div>
                            </div>
                        </div>
                    `).join('');
                }).catch(() => { block.querySelector('.jm-card-row').innerHTML = '<em style="color:#888;">Cannot load items.</em>'; });
            });
        }).catch(() => { libsHost.innerHTML = '<em style="color:#888;">Cannot load libraries from this peer.</em>'; });
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
    }

    document.addEventListener('DOMContentLoaded', () => {
        ensureStyle();
        setInterval(tick, 800);
    });
    window.addEventListener('hashchange', () => setTimeout(tick, 400));
})();
