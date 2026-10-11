const { test } = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");
const path = require("node:path");
function player(nativeHls) {
    const handlers = new Map(), instances = [];
    const video = {
        currentTime: 0, duration: 600, plays: 0, paused: true,
        addEventListener: (name, fn) => handlers.set(name, fn),
        removeEventListener: name => handlers.delete(name),
        canPlayType: () => nativeHls ? "probably" : "",
        load() {}, async play() { this.plays++; this.paused = false; },
        pause() { this.paused = true; }, removeAttribute(name) { delete this[name]; }
    };
    class Hls {
        static isSupported() { return true; }
        static Events = { ERROR: "error", MANIFEST_PARSED: "parsed" };
        constructor() { this.events = {}; instances.push(this); }
        on(event, handler) { this.events[event] = handler; }
        loadSource(url) { this.url = url; }
        attachMedia(media) { this.media = media; }
        destroy() { this.destroyed = true; }
    }
    const window = { Hls };
    const context = vm.createContext({ window, Hls, URL, xtreamStorage: {}, document: { baseURI: "https://app.example/", querySelector: () => video } });
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../src/XtreamPlayer.Web/wwwroot/app.js"), "utf8"), context);
    return { api: window.xtream, video, handlers, instances };
}
test("native HLS is preferred and resume seeks once without double offsets", async () => {
    const p = player(true);
    await p.api.play("https://provider.example/live/1.m3u8", 120);
    assert.equal(p.instances.length, 0);
    assert.equal(p.video.src, "https://provider.example/live/1.m3u8");
    p.handlers.get("loadedmetadata")();
    assert.equal(p.video.currentTime, 120);
    p.video.currentTime = 125;
    p.handlers.get("durationchange")();
    assert.equal(p.video.currentTime, 125);
    await p.api.stop();
    assert.equal(p.video.src, undefined);
});
test("HLS fallback loads directly and releases the stream on close", async () => {
    const p = player(false);
    await p.api.play("https://provider.example/live/1.m3u8", 0);
    assert.equal(p.instances.length, 1);
    assert.equal(p.instances[0].url, "https://provider.example/live/1.m3u8");
    await p.instances[0].events.parsed();
    assert.equal(p.video.plays, 1);
    await p.api.stop();
    assert.equal(p.instances[0].destroyed, true);
});
