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
        // The Jellyfin SPA renders home tabs into a section with class .homeSectionsContainer
        // inside the active tab. Wait for the standard 'My Media' section to be in the DOM
        // before injecting so our sections appear AFTER it.
        const homeView = document.querySelector('.homeSectionsContainer, .homePage');
        if (!homeView) return;
        if (homeView.dataset.jmInjected === 'yes') return;
        homeView.dataset.jmInjected = 'yes';

        jApi('/Federation/Stats').then((stats) => {
            const peers = (stats.Peers || []).filter((p) => p.Enabled && p.Online);
            if (peers.length === 0) return;
            peers.forEach((peer) => renderPeerSection(homeView, peer));
        }).catch(() => { delete homeView.dataset.jmInjected; });
    }

    // Reproduce Jellyfin's home-page section markup so our injected sections inherit every
    // built-in style + the horizontal scroller behaviour automatically.
    function renderPeerSection(host, peer) {
        // One verticalSection per library on the peer (mirrors how Jellyfin shows Latest Movies
        // and Latest Shows as separate sections rather than nesting). Section title says
        // 'Library on PeerName'.
        jApi('/Federation/Peers/' + peer.Id + '/Libraries').then((libs) => {
            (libs || []).forEach((lib) => {
                const section = document.createElement('div');
                section.className = 'verticalSection';
                section.dataset.jmPeer = peer.Id;
                section.dataset.jmLib = lib.id;
                section.innerHTML = `
                    <div class="sectionTitleContainer sectionTitleContainer-cards padded-left">
                        <h2 class="sectionTitle sectionTitle-cards">${escapeHtml(lib.name || 'Library')} <span style="opacity:0.65;font-weight:400;">&middot; ${escapeHtml(peer.Name || 'Peer')}</span></h2>
                    </div>
                    <div is="emby-itemscontainer" class="itemsContainer scrollSlider focuscontainer-x padded-left padded-right" data-monitor="" style="white-space:nowrap;overflow-x:auto;">
                        <div class="jm-placeholder" style="padding:1em;color:#777;">Loading...</div>
                    </div>
                `;
                host.appendChild(section);
                jApi(`/Federation/Peers/${peer.Id}/Libraries/${encodeURIComponent(lib.id)}/Items?limit=18`)
                    .then((data) => fillSectionCards(section, peer, data.items || []))
                    .catch(() => { section.querySelector('.jm-placeholder').textContent = 'Cannot load items.'; });
            });
        }).catch(() => { /* peer offline mid-render */ });
    }

    function fillSectionCards(section, peer, items) {
        const row = section.querySelector('.itemsContainer');
        if (!row) return;
        if (items.length === 0) {
            row.innerHTML = '<div class="jm-placeholder" style="padding:1em;color:#777;">Empty.</div>';
            return;
        }
        // Standard portrait card markup pulled from Jellyfin's cardBuilder output.
        row.innerHTML = items.map((it) => `
            <div class="card overflowPortraitCard card-hoverable" data-id="${escapeHtml(it.id)}" data-serverid="${escapeHtml(peer.Id)}" data-type="${escapeHtml(it.type || 'Movie')}" data-prefix="" style="display:inline-block;white-space:normal;">
                <div class="cardBox cardBox-bottompadded">
                    <div class="cardScalable">
                        <div class="cardPadder cardPadder-overflowPortrait"></div>
                        <div class="cardImageContainer coveredImage cardContent" style="position:relative;">
                            <span class="jm-card-badge">${escapeHtml(peer.Name)}</span>
                            <div class="cardImage" style="background-image:url('${escapeHtml(it.imageUrl)}');background-size:cover;background-position:center;"></div>
                        </div>
                    </div>
                    <div class="cardText cardTextCentered cardText-first"><bdi>${escapeHtml(it.name || '')}</bdi></div>
                    <div class="cardText cardTextCentered cardText-secondary"><bdi>${it.year || ''}${it.version ? ' &middot; ' + escapeHtml(it.version) : ''}</bdi></div>
                </div>
            </div>
        `).join('');
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
