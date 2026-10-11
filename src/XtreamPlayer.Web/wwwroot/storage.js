/* Shared by the page, downloader worker and service worker. No credentials in media metadata. */
globalThis.xtreamStorage = (() => {
    let opened;
    function db() {
        return opened ??= new Promise((resolve, reject) => {
            const r = indexedDB.open("xtream-device-v1", 1);
            r.onupgradeneeded = () => r.result.createObjectStore("entries");
            r.onsuccess = () => resolve(r.result);
            r.onerror = () => { opened = null; reject(r.error); };
        });
    }
    async function transaction(mode, action) {
        const database = await db();
        return new Promise((resolve, reject) => {
            const tx = database.transaction("entries", mode);
            const result = action(tx.objectStore("entries"));
            tx.oncomplete = () => resolve(result.result ?? null);
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error ?? new Error("Storage transaction aborted."));
        });
    }
    const get = key => transaction("readonly", s => s.get(key));
    const set = (key, value) => transaction("readwrite", s => s.put(value, key));
    const remove = key => transaction("readwrite", s => s.delete(key));
    async function media() {
        const keys = await transaction("readonly", s => s.getAllKeys());
        return Promise.all(keys.filter(k => typeof k === "string" && k.startsWith("offline:")).map(get));
    }
    async function directory() {
        if (!navigator.storage?.getDirectory) throw new Error("Offline video storage is unavailable in this browser. Use iOS 17 or newer.");
        return (await navigator.storage.getDirectory()).getDirectoryHandle("xtream-media-v1", { create: true });
    }
    function validId(id) { if (!/^[a-f0-9]{32,64}$/.test(id)) throw new Error("Invalid media ID."); return id; }
    function range(header, size) {
        if (!header) return { start: 0, end: size - 1, partial: false };
        const m = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
        if (!m || (!m[1] && !m[2]) || size <= 0) return null;
        let start = m[1] ? Number(m[1]) : Math.max(0, size - Number(m[2]));
        let end = m[1] ? (m[2] ? Math.min(Number(m[2]), size - 1) : size - 1) : size - 1;
        if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || start > end || start >= size) return null;
        return { start, end, partial: true };
    }
    return { get, set, remove, media, directory, validId, range };
})();
