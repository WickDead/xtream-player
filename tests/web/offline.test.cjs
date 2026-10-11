const { test } = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");
const path = require("node:path");
const root = path.resolve(__dirname, "../../src/XtreamPlayer.Web/wwwroot");
const context = vm.createContext({ navigator: {}, console, Response, Blob });
vm.runInContext(fs.readFileSync(path.join(root, "storage.js"), "utf8"), context);
const storage = context.xtreamStorage;
test("range requests support Safari probes, suffixes and open-ended seeks", () => {
    assert.equal(JSON.stringify(storage.range("bytes=0-1", 100)), JSON.stringify({ start: 0, end: 1, partial: true }));
    assert.equal(storage.range("bytes=50-", 100).end, 99);
    assert.equal(storage.range("bytes=-20", 100).start, 80);
    assert.equal(storage.range("bytes=0-1000", 100).end, 99);
    assert.equal(storage.range(null, 100).partial, false);
});
test("malformed, multiple, reversed and unsatisfiable ranges are rejected", () => {
    for (const range of ["bytes=100-", "bytes=20-10", "bytes=0-1,5-8", "bytes=-", "items=0-2", "bytes=-0", "bytes=9007199254740993-"])
        assert.equal(storage.range(range, 100), null, range);
    assert.equal(storage.range("bytes=0-1", 0), null);
});
test("offline IDs cannot escape the media directory", () => {
    for (const id of ["../secret", "a/b", "", "file.mp4", "%2e%2e"]) assert.throws(() => storage.validId(id));
    assert.equal(storage.validId("a".repeat(32)), "a".repeat(32));
});
test("service worker streams only the requested bytes and handles HEAD", async () => {
    const file = new Blob([Uint8Array.from({ length: 100 }, (_, i) => i)]);
    context.importScripts = () => {};
    context.xtreamStorage = { ...storage,
        get: async () => ({ complete: true, total: 100 }),
        directory: async () => ({ getFileHandle: async () => ({ getFile: async () => file }) }) };
    vm.runInContext(fs.readFileSync(path.join(root, "offline-handler.js"), "utf8"), context);
    const request = new Request("https://example.com/offline-media/" + "a".repeat(32), { headers: { Range: "bytes=40-49" } });
    const response = await context.offlineResponse(request, "a".repeat(32));
    assert.equal(response.status, 206);
    assert.equal(response.headers.get("Content-Range"), "bytes 40-49/100");
    assert.deepEqual([...new Uint8Array(await response.arrayBuffer())], [40,41,42,43,44,45,46,47,48,49]);
    const head = await context.offlineResponse(new Request(request.url, { method: "HEAD" }), "a".repeat(32));
    assert.equal(head.headers.get("Content-Length"), "100");
    assert.equal((await head.arrayBuffer()).byteLength, 0);
});
test("incomplete and evicted offline media fail clearly", async () => {
    context.xtreamStorage.get = async () => ({ complete: false });
    assert.equal((await context.offlineResponse(new Request("https://example.com"), "a".repeat(32))).status, 404);
    context.xtreamStorage.get = async () => ({ complete: true, total: 999 });
    assert.equal((await context.offlineResponse(new Request("https://example.com"), "a".repeat(32))).status, 404);
});
