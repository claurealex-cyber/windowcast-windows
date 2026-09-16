// Service worker — network-first to avoid stale cache issues during development
const CACHE_NAME = 'windowcast-v6';

self.addEventListener('install', event => {
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    // Delete ALL old caches
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys.map(k => caches.delete(k)))
        )
    );
    self.clients.claim();
});

self.addEventListener('fetch', event => {
    // Network-first for everything — always get fresh content
    event.respondWith(
        fetch(event.request).catch(() => caches.match(event.request))
    );
});
