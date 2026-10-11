// Local generated-media provider for direct browser integration checks. Never published.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
const media = path.resolve("dist/test-tools");
http.createServer((req, res) => {
    res.setHeader("Access-Control-Allow-Origin", "*");
    res.setHeader("Access-Control-Expose-Headers", "Content-Length, Content-Range");
    const url = new URL(req.url, "http://127.0.0.1");
    if (url.pathname === "/player_api.php") {
        const action = url.searchParams.get("action");
        const category = [{ category_id: "1", category_name: "Generated test media" }];
        const body = action?.endsWith("_categories") ? category :
            action === "get_live_streams" ? [{ stream_id: 1, name: "HLS playback test", category_id: "1" }] :
            action === "get_vod_streams" ? [{ stream_id: 7, name: "MP4 playback test", category_id: "1", container_extension: "mp4" }] :
            action === "get_vod_info" ? { info: { name: "MP4 playback test", plot: "Generated test pattern, no real provider." }, movie_data: { stream_id: 7, name: "MP4 playback test" } } :
            action === "get_series" ? [{ series_id: 10, name: "Test series", category_id: "1" }] :
            action === "get_series_info" ? { info: { name: "Test series" }, episodes: { "1": [{ id: "11", title: "Test episode", episode_num: 1, season: 1, container_extension: "mp4" }] } } :
            { user_info: { status: "Active", max_connections: "5", allowed_output_formats: ["m3u8", "ts"] }, server_info: {} };
        res.writeHead(200, { "Content-Type": "application/json" }); res.end(JSON.stringify(body)); return;
    }
    let file;
    if (url.pathname.endsWith(".mp4")) file = path.join(media, "fixture.mp4");
    else if (url.pathname.endsWith(".m3u8")) file = path.join(media, "hls", "index.m3u8");
    else if (/\/index\d+\.ts$/.test(url.pathname)) file = path.join(media, "hls", path.basename(url.pathname));
    else { res.writeHead(404); res.end(); return; }
    const size = fs.statSync(file).size;
    let start = 0, end = size - 1;
    const match = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
    if (match) { start = Number(match[1]); end = match[2] ? Math.min(Number(match[2]), end) : end; }
    if (start > end || start >= size) { res.writeHead(416, { "Content-Range": `bytes */${size}` }); res.end(); return; }
    res.setHeader("Content-Type", file.endsWith(".m3u8") ? "application/vnd.apple.mpegurl" : file.endsWith(".ts") ? "video/mp2t" : "video/mp4");
    res.setHeader("Content-Length", end - start + 1); res.setHeader("Accept-Ranges", "bytes");
    if (match) res.setHeader("Content-Range", `bytes ${start}-${end}/${size}`);
    res.writeHead(match ? 206 : 200);
    if (req.method === "HEAD") res.end(); else fs.createReadStream(file, { start, end }).pipe(res);
}).listen(5094, "127.0.0.1", () => console.log("Generated IPTV fixture: http://127.0.0.1:5094"));
