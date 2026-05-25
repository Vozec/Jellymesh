// Jellymesh client-side: injects a "Share" button into the item details page that
// creates a public share link via /Federation/PublicShares and copies the URL.
//
// Include this file in Jellyfin's web/index.html with:
//   <script defer src="/Federation/Asset/jellymesh-item.js"></script>
// or load it as a userscript / browser extension on the same origin.

(function () {
    'use strict';
    var BTN_ID = 'jm-share-btn';

    function currentItemId() {
        var m = location.hash.match(/[?&]id=([0-9a-f]{32})/i);
        return m ? m[1] : null;
    }

    function ensureButton() {
        if (!location.hash.startsWith('#/details')) return;
        var itemId = currentItemId();
        if (!itemId) return;
        if (document.getElementById(BTN_ID)) return;
        var bar = document.querySelector('.detailPagePrimaryContainer .mainDetailButtons, .detailButtons, .detailPagePrimaryContainer');
        if (!bar) return;
        var btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.className = 'detailButton emby-button';
        btn.style.cssText = 'margin:0 0.4em;';
        btn.innerHTML = '<span class="material-icons">share</span><span class="detailButton-textRow">Share</span>';
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            var maxUses = prompt('Max uses (blank for unlimited):', '5');
            if (maxUses === null) return;
            var expiryHours = prompt('Expires in N hours (blank for no expiry):', '24');
            if (expiryHours === null) return;
            var body = { ItemId: itemId };
            var n = parseInt(maxUses, 10); if (!isNaN(n)) body.MaxUses = n;
            var h = parseInt(expiryHours, 10);
            if (!isNaN(h)) body.ExpiresUtc = new Date(Date.now() + h * 3600 * 1000).toISOString();
            fetch('/Federation/PublicShares', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-Emby-Token': window.ApiClient && ApiClient.accessToken() },
                body: JSON.stringify(body)
            })
            .then(function (r) { return r.ok ? r.json() : r.text().then(function (t) { throw new Error('HTTP ' + r.status + ': ' + t); }); })
            .then(function (resp) {
                navigator.clipboard.writeText(resp.url).catch(function () {});
                alert('Public share link (copied to clipboard):\n\n' + resp.url);
            })
            .catch(function (err) { alert('Share failed: ' + err.message); });
        });
        bar.appendChild(btn);
    }

    // The Jellyfin SPA swaps the details view in/out without a full page reload, so we hook
    // both initial load and the hashchange events to re-attach the button.
    document.addEventListener('DOMContentLoaded', function () {
        setInterval(ensureButton, 800);
    });
    window.addEventListener('hashchange', function () { setTimeout(ensureButton, 400); });
})();
