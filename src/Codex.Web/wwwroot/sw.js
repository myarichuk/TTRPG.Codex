// TTRPG.Codex service worker. The app is a live Blazor Server circuit, so pages themselves are
// never served from cache: this worker only (1) keeps the static shell (styles, scripts, icons)
// fresh with a stale-while-revalidate strategy and (2) shows an offline page when the server
// can't be reached. Anything private (/media, /_blazor, API calls) is never cached.
const VERSION = 'codex-shell-v17';
const SHELL = [
    '/offline.html',
    '/app.css',
    '/lib/bootstrap/bootstrap.min.css',
    '/js/app-startup.js',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
    '/favicon.png',
    '/fonts/cinzel/cinzel-latin-wght-normal.woff2',
    '/fonts/alegreya-sans/alegreya-sans-latin-400-normal.woff2',
    '/fonts/alegreya-sans/alegreya-sans-latin-700-normal.woff2'
];

self.addEventListener('install', event => {
    // 'reload' skips the browser's HTTP cache, which may still hold the previous version's files.
    event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL.map(url => new Request(url, { cache: 'reload' })))).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

function isShellAsset(url) {
    return url.origin === self.location.origin && (
        url.pathname === '/app.css' ||
        url.pathname === '/favicon.png' ||
        url.pathname.startsWith('/lib/') ||
        url.pathname.startsWith('/js/') ||
        url.pathname.startsWith('/icons/') ||
        url.pathname.startsWith('/fonts/') ||
        url.pathname.startsWith('/_framework/')
    );
}

self.addEventListener('fetch', event => {
    const request = event.request;
    if (request.method !== 'GET') return;
    const url = new URL(request.url);

    if (request.mode === 'navigate') {
        // Always go to the server for pages; fall back to the offline page only when it is unreachable.
        event.respondWith(fetch(request).catch(() => caches.match('/offline.html')));
        return;
    }

    if (isShellAsset(url)) {
        event.respondWith(
            caches.open(VERSION).then(async cache => {
                const cached = await cache.match(request);
                // 'no-cache' revalidates with the server, so the refreshed copy is the server's, not the HTTP cache's.
                const network = fetch(request, { cache: 'no-cache' }).then(response => {
                    if (response.ok) cache.put(request, response.clone());
                    return response;
                }).catch(() => cached);
                return cached || network;
            })
        );
    }
});
