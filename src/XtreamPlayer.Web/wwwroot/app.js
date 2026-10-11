(() => {
    let dotnet, registration, videoCleanup, objectUrl, hls;
    const base = () => new URL(document.baseURI);
    window.xtream = {
        get: xtreamStorage.get, set: xtreamStorage.set, remove: xtreamStorage.remove,
        async init(reference) {
            dotnet = reference;
            addEventListener("hashchange", () => dotnet.invokeMethodAsync("RouteChanged", location.hash.slice(1) || "home"));
            addEventListener("online", () => dotnet.invokeMethodAsync("Connectivity", true));
            addEventListener("offline", () => dotnet.invokeMethodAsync("Connectivity", false));
            addEventListener("pagehide", () => window.xtream.checkpoint());
            document.addEventListener("visibilitychange", () => window.xtream.checkpoint());
            if ("serviceWorker" in navigator) {
                registration = await navigator.serviceWorker.register(new URL("service-worker.js", base()), { scope: base().pathname, updateViaCache: "none" });
                const changed = () => dotnet.invokeMethodAsync("UpdateAvailable", !!registration.waiting && !!navigator.serviceWorker.controller);
                registration.addEventListener("updatefound", () => registration.installing?.addEventListener("statechange", changed));
                changed();
                navigator.serviceWorker.addEventListener("controllerchange", () => {
                    changed();
                    if (window.xtream.updating) location.reload();
                });
            }
            return { route: location.hash.slice(1) || "home", online: navigator.onLine };
        },
        navigate(route) { location.hash = route; },
        scroll() { return document.querySelector("main")?.scrollTop ?? 0; },
        restoreScroll(top) { requestAnimationFrame(() => { const main = document.querySelector("main"); if (main) main.scrollTop = top; }); },
        update() {
            const video = document.querySelector("video");
            if (video && !video.paused) throw new Error("Pause playback before updating.");
            window.xtream.updating = true; registration?.waiting?.postMessage("activate-update");
        },
        checkpoint() {
            const video = document.querySelector("video");
            if (video && Number.isFinite(video.currentTime)) return dotnet?.invokeMethodAsync("Progress", video.currentTime, Number.isFinite(video.duration) ? video.duration : 0);
        },
        async play(url, position) {
            const video = document.querySelector("video");
            if (!video) return;
            videoCleanup?.(); hls?.destroy(); hls = null;
            let last = 0, resumeApplied = false;
            const progress = () => { if (Date.now() - last > 5000) { last = Date.now(); window.xtream.checkpoint(); } };
            const loaded = () => {
                if (!resumeApplied && position > 0 && Number.isFinite(video.duration) && video.duration > 0) {
                    video.currentTime = Math.min(position, video.duration - 0.1); resumeApplied = true;
                }
            };
            const error = () => dotnet.invokeMethodAsync("PlayerError",
                "This stream could not play. The provider may be unavailable, block browser access, or use a format this browser cannot play. Try another title.");
            const start = async () => {
                try { await video.play(); } catch (e) {
                    if (e.name !== "NotAllowedError" && e.name !== "AbortError") error();
                }
            };
            video.addEventListener("timeupdate", progress);
            video.addEventListener("loadedmetadata", loaded);
            video.addEventListener("durationchange", loaded);
            video.addEventListener("error", error);
            video.addEventListener("pause", window.xtream.checkpoint);
            videoCleanup = () => {
                video.removeEventListener("timeupdate", progress); video.removeEventListener("loadedmetadata", loaded);
                video.removeEventListener("durationchange", loaded); video.removeEventListener("error", error);
                video.removeEventListener("pause", window.xtream.checkpoint);
            };
            const isHls = new URL(url, base()).pathname.toLowerCase().endsWith(".m3u8");
            if (isHls && !video.canPlayType("application/vnd.apple.mpegurl")) {
                if (!window.Hls?.isSupported()) { error(); return; }
                hls = new Hls({ debug: false, enableWorker: true, maxBufferLength: 30 });
                hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) { hls?.destroy(); hls = null; error(); } });
                hls.on(Hls.Events.MANIFEST_PARSED, start);
                hls.loadSource(url); hls.attachMedia(video);
            } else {
                video.src = url; video.load(); await start();
            }
        },
        async stop() {
            await window.xtream.checkpoint(); videoCleanup?.(); videoCleanup = null;
            hls?.destroy(); hls = null;
            const video = document.querySelector("video");
            if (video) { video.pause(); video.removeAttribute("src"); video.load(); }
            if (objectUrl) URL.revokeObjectURL(objectUrl); objectUrl = null;
        },
        localFile() {
            const file = document.getElementById("local-file")?.files?.[0]; if (!file) return null;
            if (objectUrl) URL.revokeObjectURL(objectUrl);
            objectUrl = URL.createObjectURL(file);
            return { url: objectUrl, title: file.name };
        }
    };
})();
