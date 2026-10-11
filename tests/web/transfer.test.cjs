const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const script = fs.readFileSync(require("node:path").join(__dirname, "../../src/XtreamPlayer.Web/wwwroot/offline-download.worker.js"), "utf8");

function harness({ existing, initial = new Uint8Array(), failWrite = false, status = 206, interrupt = false } = {}) {
    let data = initial, record = existing, closed = false, calls = [], messages = [];
    const handle = {
        getSize: () => data.length,
        truncate(size) { data = data.slice(0, size); },
        write(bytes, { at }) {
            if (failWrite) throw new DOMException("Full", "QuotaExceededError");
            const next = new Uint8Array(at + bytes.length); next.set(data); next.set(bytes, at); data = next;
            return bytes.length;
        },
        flush() {}, close() { closed = true; }
    };
    const context = vm.createContext({
        importScripts() {}, Uint8Array, AbortController, DOMException, console,
        xtreamStorage: {
            validId() {}, get: async () => record,
            set: async (_, value) => { record = structuredClone(value); },
            directory: async () => ({ getFileHandle: async () => ({ createSyncAccessHandle: async () => handle }) })
        },
        fetch: async (_, options) => {
            calls.push(options.headers.Range);
            if (interrupt) throw new DOMException("Paused", "AbortError");
            const [start, end] = options.headers.Range.slice(6).split("-").map(Number);
            return new Response(Uint8Array.from({ length: end - start + 1 }, (_, i) => start + i), {
                status, headers: { "Content-Range": `bytes ${start}-${end}/10` }
            });
        },
        postMessage(message) { messages.push(structuredClone(message)); }
    });
    vm.runInContext(script, context);
    return { async run() {
        await context.onmessage({ data: { localId: "a".repeat(32), file: { id: "job", title: "Test", bytes: 10, version: "v1", url: "https://example.test/file" } } });
        return { record, data, calls, closed, messages };
    } };
}
test("interrupted transfers resume from the last committed chunk", async () => {
    const result = await harness({ existing: { version: "v1", bytes: 4 }, initial: new Uint8Array([0,1,2,3,99,99]) }).run();
    assert.deepEqual(result.calls, ["bytes=4-9"]);
    assert.deepEqual([...result.data], [0,1,2,3,4,5,6,7,8,9]);
    assert.equal(result.record.complete, true);
    assert.equal(result.closed, true);
});
test("a replaced server file restarts an old partial copy", async () => {
    const result = await harness({ existing: { version: "old", bytes: 4 }, initial: new Uint8Array([99,99,99,99]) }).run();
    assert.deepEqual(result.calls, ["bytes=0-9"]);
    assert.equal(result.record.complete, true);
});
test("storage exhaustion and interrupted requests never mark media complete", async () => {
    for (const options of [{ failWrite: true }, { interrupt: true }, { status: 200 }]) {
        const result = await harness(options).run();
        assert.equal(result.record.complete, false);
        assert.equal(result.closed, true);
        assert.ok(result.messages.some(m => m.error));
    }
});
