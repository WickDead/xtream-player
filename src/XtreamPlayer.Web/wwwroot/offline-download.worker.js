importScripts("storage.js");
let busy = false, controller;
onmessage = async event => {
    if (event.data.action === "pause") { controller?.abort(); return; }
    if (busy) { postMessage({ error: "Another offline transfer is running." }); return; }
    busy = true;
    controller = new AbortController();
    const { file, localId, item, providerId, serverUrl } = event.data;
    let handle, metadata;
    try {
        xtreamStorage.validId(localId);
        const dir = await xtreamStorage.directory();
        const existing = await xtreamStorage.get("offline:" + localId);
        metadata = { id: localId, jobId: file.id, title: file.title, version: file.version, total: file.bytes,
            bytes: 0, complete: false, item, providerId, serverUrl, error: null };
        const fileHandle = await dir.getFileHandle(localId + ".mp4", { create: true });
        handle = await fileHandle.createSyncAccessHandle();
        if (existing?.version === file.version) metadata.bytes = Math.min(existing.bytes, handle.getSize());
        handle.truncate(metadata.bytes);
        const chunk = 4 * 1024 * 1024;
        while (metadata.bytes < file.bytes) {
            if (controller.signal.aborted) throw new DOMException("Paused", "AbortError");
            const start = metadata.bytes, end = Math.min(start + chunk - 1, file.bytes - 1);
            const response = await fetch(file.url, { headers: { Range: `bytes=${start}-${end}` }, signal: controller.signal, cache: "no-store", referrerPolicy: "no-referrer" });
            if (response.status !== 206 || response.headers.get("Content-Range") !== `bytes ${start}-${end}/${file.bytes}`)
                throw new Error("Server did not return the requested bytes. Reconnect and retry.");
            const bytes = new Uint8Array(await response.arrayBuffer());
            if (bytes.byteLength !== end - start + 1) throw new Error("Incomplete transfer. Resume to retry.");
            const written = handle.write(bytes, { at: start });
            if (written !== bytes.byteLength) throw new Error("Could not store the complete chunk.");
            handle.flush();
            metadata.bytes += written;
            await xtreamStorage.set("offline:" + localId, metadata);
            postMessage({ progress: metadata });
        }
        handle.flush();
        if (handle.getSize() !== file.bytes) throw new Error("Offline copy did not pass its size check.");
        metadata.complete = true;
        await xtreamStorage.set("offline:" + localId, metadata);
        postMessage({ progress: metadata, done: true });
    } catch (e) {
        if (metadata) {
            metadata.error = e.name === "AbortError" ? "Paused. Resume while the app is open." :
                e.name === "QuotaExceededError" ? "iPhone storage is full. Remove an offline video and retry." : e.message;
            try { await xtreamStorage.set("offline:" + localId, metadata); } catch { /* Quota may also prevent updating metadata. */ }
            postMessage({ progress: metadata });
        }
        postMessage({ error: e.name === "AbortError" ? "Transfer paused." : e.message });
    } finally { handle?.close(); busy = false; controller = null; }
};
