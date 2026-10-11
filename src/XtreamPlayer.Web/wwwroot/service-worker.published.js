importScripts("service-worker-assets.js", "offline-handler.js");
const prefix = "xtream-shell:" + self.registration.scope + ":";
const cacheName = prefix + self.assetsManifest.version;
const base = new URL(self.registration.scope);
const absolute = path => new URL(path, base).href;
const assets = self.assetsManifest.assets.filter(a => !/service-worker(?:\.published)?\.js$/.test(a.url))
    .map(a => new Request(absolute(a.url), { integrity: a.hash, cache: "no-cache" }));
self.addEventListener("install", event => event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(assets))));
self.addEventListener("activate", event => event.waitUntil((async () => {
    for (const key of await caches.keys()) if (key.startsWith(prefix) && key !== cacheName) await caches.delete(key);
    await self.clients.claim();
})()));
self.addEventListener("message", event => { if (event.data === "activate-update") self.skipWaiting(); });
self.addEventListener("fetch", event => {
    const request = event.request, url = new URL(request.url);
    if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) return;
    if (url.pathname.startsWith(base.pathname + "offline-media/")) {
        event.respondWith(offlineResponse(request, url.pathname.slice((base.pathname + "offline-media/").length))); return;
    }
    if (request.method !== "GET" || url.pathname.includes("/api/") || url.pathname.includes("/media/")) return;
    event.respondWith((async () => {
        const cache = await caches.open(cacheName);
        return await cache.match(request.mode === "navigate" ? absolute("index.html") : request) ?? fetch(request);
    })());
});
