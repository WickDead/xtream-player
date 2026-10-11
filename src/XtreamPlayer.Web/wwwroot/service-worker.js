importScripts("offline-handler.js");
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
self.addEventListener("fetch", event => {
    const url = new URL(event.request.url), base = new URL(self.registration.scope);
    if (url.origin === base.origin && url.pathname.startsWith(base.pathname + "offline-media/"))
        event.respondWith(offlineResponse(event.request, url.pathname.slice((base.pathname + "offline-media/").length)));
});
