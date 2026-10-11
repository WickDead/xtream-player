// Development-only static server, with a browser harness and generated video fixture.
// node tests/web/serve-preview.mjs [port] [fixture.mp4]
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const site = path.join(root, "dist/pwa/wwwroot");
const fixture = path.resolve(process.argv[3] || path.join(root, "dist/test-tools/fixture.mp4"));
const types = { ".html":"text/html", ".js":"text/javascript", ".css":"text/css", ".json":"application/json", ".wasm":"application/wasm", ".webmanifest":"application/manifest+json", ".png":"image/png", ".mp4":"video/mp4" };
http.createServer((req,res) => {
    const url = new URL(req.url, "http://localhost");
    if (url.pathname === "/fixture-info") { res.writeHead(200, {"Content-Type":"application/json"}); res.end(JSON.stringify({bytes:fs.statSync(fixture).size})); return; }
    let file;
    if (url.pathname === "/fixture.mp4") file = fixture;
    else if (url.pathname === "/xtream-player/harness.html") file = path.join(root, "tests/web/browser-harness.html");
    else {
        if (!url.pathname.startsWith("/xtream-player/")) { res.writeHead(404); res.end(); return; }
        const relative = decodeURIComponent(url.pathname.slice("/xtream-player/".length)) || "index.html";
        file = path.resolve(site, relative);
        if (!file.startsWith(site + path.sep)) { res.writeHead(403); res.end(); return; }
    }
    if (!fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
    const size = fs.statSync(file).size;
    let start = 0, end = size-1;
    const match = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
    if (match) { start=Number(match[1]); end=match[2] ? Math.min(Number(match[2]),end) : end; }
    if (start > end || start >= size) { res.writeHead(416, {"Content-Range":`bytes */${size}`}); res.end(); return; }
    const headers = {"Content-Type":types[path.extname(file)] || "application/octet-stream", "Content-Length":end-start+1, "Accept-Ranges":"bytes", "Cache-Control":"no-cache"};
    if (match) headers["Content-Range"]=`bytes ${start}-${end}/${size}`;
    res.writeHead(match ? 206 : 200, headers);
    if(req.method === "HEAD")res.end(); else fs.createReadStream(file,{start,end}).pipe(res);
}).listen(Number(process.argv[2] || 5092), "127.0.0.1", () => console.log("Preview and offline harness on port " + (process.argv[2] || 5092)));
