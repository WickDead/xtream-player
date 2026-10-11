importScripts("storage.js");
async function offlineResponse(request, id) {
    try {
        xtreamStorage.validId(id);
        const metadata = await xtreamStorage.get("offline:" + id);
        if (!metadata?.complete) return new Response("Offline copy is incomplete.", { status: 404 });
        const dir = await xtreamStorage.directory();
        const file = await (await dir.getFileHandle(id + ".mp4")).getFile();
        if (file.size !== metadata.total) return new Response("Offline copy is missing or incomplete.", { status: 404 });
        const range = xtreamStorage.range(request.headers.get("Range"), file.size);
        if (!range) return new Response(null, { status: 416, headers: { "Content-Range": "bytes */" + file.size } });
        const headers = { "Content-Type": "video/mp4", "Accept-Ranges": "bytes", "Cache-Control": "no-store",
            "Content-Length": String(range.end - range.start + 1) };
        if (range.partial) headers["Content-Range"] = `bytes ${range.start}-${range.end}/${file.size}`;
        return new Response(request.method === "HEAD" ? null : file.slice(range.start, range.end + 1).stream(),
            { status: range.partial ? 206 : 200, headers });
    } catch { return new Response("Offline video is no longer stored on this iPhone.", { status: 404 }); }
}
