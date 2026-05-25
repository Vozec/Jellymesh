// Jellymesh client-side: injects a "Share" button into the item details page that
// creates a public share link via /Federation/PublicShares and copies the URL.
// Loaded by IndexHtmlInjector via a <script src> tag in Jellyfin's index.html.

(function () {
    'use strict';
    const BTN_ID = 'jm-share-btn';
    const STYLE_ID = 'jm-share-style';
    const MODAL_ID = 'jm-share-modal';

    function currentItemId() {
        const m = location.hash.match(/[?&]id=([0-9a-f]{32})/i);
        return m ? m[1] : null;
    }

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        const s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent = `
            #${BTN_ID} {
                background: transparent !important;
                border: none !important;
                color: inherit !important;
                padding: 0.4em !important;
                margin: 0 0.25em !important;
                cursor: pointer;
                display: inline-flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
                width: 2.2em;
                height: 2.2em;
            }
            #${BTN_ID}:hover { background: rgba(255,255,255,0.08) !important; }
            #${BTN_ID} .material-icons { font-size: 1.4em; }
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
            .jm-share-toast { position: fixed; left: 50%; bottom: 5vh; transform: translateX(-50%); background: #2f7c4a; color: #fff; padding: 0.6em 1.2em; border-radius: 0.4em; box-shadow: 0 6px 20px rgba(0,0,0,0.4); z-index: 10000; opacity: 0; transition: opacity 0.25s; }
            .jm-share-toast.show { opacity: 1; }
        `;
        document.head.appendChild(s);
    }

    function toast(msg, kind) {
        const t = document.createElement('div');
        t.className = 'jm-share-toast';
        t.textContent = msg;
        if (kind === 'error') t.style.background = '#a23333';
        document.body.appendChild(t);
        requestAnimationFrame(() => t.classList.add('show'));
        setTimeout(() => { t.classList.remove('show'); setTimeout(() => t.remove(), 300); }, 2500);
    }

    function openShareModal(itemId) {
        document.getElementById(MODAL_ID)?.remove();
        const overlay = document.createElement('div');
        overlay.id = MODAL_ID;
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
                const maxRaw = document.getElementById('jm-share-max').value.trim();
                const expRaw = document.getElementById('jm-share-exp').value.trim();
                const body = { ItemId: itemId };
                const n = parseInt(maxRaw, 10);
                if (!isNaN(n) && n > 0) body.MaxUses = n;
                const h = parseInt(expRaw, 10);
                if (!isNaN(h) && h > 0) body.ExpiresUtc = new Date(Date.now() + h * 3600 * 1000).toISOString();
                t.disabled = true;
                t.textContent = 'Generating...';
                fetch('/Federation/PublicShares', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-Emby-Token': window.ApiClient && ApiClient.accessToken()
                    },
                    body: JSON.stringify(body)
                })
                .then((r) => r.ok ? r.json() : r.text().then((txt) => { throw new Error(`HTTP ${r.status}: ${txt}`); }))
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
                    t.disabled = false;
                    t.textContent = 'Generate link';
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

    function ensureButton() {
        if (!location.hash.startsWith('#/details')) return;
        const itemId = currentItemId();
        if (!itemId) return;
        if (document.getElementById(BTN_ID)) return;
        const bar = document.querySelector('.detailPagePrimaryContainer .mainDetailButtons, .detailButtons, .detailPagePrimaryContainer');
        if (!bar) return;
        ensureStyle();
        const btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.title = 'Share via Jellymesh';
        btn.innerHTML = `<span class="material-icons">share</span>`;
        btn.addEventListener('click', (e) => {
            e.preventDefault();
            openShareModal(itemId);
        });
        bar.appendChild(btn);
    }

    // The Jellyfin SPA swaps the details view in/out without a full page reload, so we hook
    // both initial load and the hashchange events to re-attach the button.
    document.addEventListener('DOMContentLoaded', () => {
        setInterval(ensureButton, 800);
    });
    window.addEventListener('hashchange', () => setTimeout(ensureButton, 400));
})();
