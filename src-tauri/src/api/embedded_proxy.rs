use std::{
    io::{Cursor, Read},
    net::TcpListener,
    sync::OnceLock,
    thread,
};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use reqwest::{blocking::Client, Url};
use tiny_http::{Header, Method, Response, ResponseBox, Server, StatusCode};

const EMBEDDED_PROXY_HOST: &str = "127.0.0.1";
const USER_AGENT: &str = "Xtream Player/0.1.1";
const MANIFEST_PEEK_BYTES: usize = 1024;

struct EmbeddedProxyServer {
    base_url: String,
}

static EMBEDDED_PROXY_SERVER: OnceLock<Result<EmbeddedProxyServer, String>> = OnceLock::new();

#[tauri::command]
pub fn resolve_embedded_stream_url(stream_url: String) -> Result<String, String> {
    encode_proxy_url(&stream_url)
}

fn ensure_proxy_server() -> Result<&'static EmbeddedProxyServer, String> {
    EMBEDDED_PROXY_SERVER
        .get_or_init(start_proxy_server)
        .as_ref()
        .map_err(|error| error.clone())
}

fn start_proxy_server() -> Result<EmbeddedProxyServer, String> {
    let listener = TcpListener::bind((EMBEDDED_PROXY_HOST, 0)).map_err(|error| error.to_string())?;
    let address = listener.local_addr().map_err(|error| error.to_string())?;
    let server = Server::from_listener(listener, None).map_err(|error| error.to_string())?;
    let base_url = format!("http://{}:{}", EMBEDDED_PROXY_HOST, address.port());

    thread::spawn(move || {
        for request in server.incoming_requests() {
            handle_http_proxy_request(request);
        }
    });

    Ok(EmbeddedProxyServer { base_url })
}

fn handle_http_proxy_request(request: tiny_http::Request) {
    let method = request.method().clone();
    let is_head = matches!(method, Method::Head);

    if !matches!(method, Method::Get | Method::Head) {
        let response = build_error_http_response(405, "Only GET and HEAD are supported for embedded playback.");
        let _ = request.respond(response);
        return;
    }

    let request_url = format!("http://{EMBEDDED_PROXY_HOST}{}", request.url());
    let parsed_request_url = match Url::parse(&request_url) {
        Ok(parsed) => parsed,
        Err(error) => {
            let response = build_error_http_response(400, &format!("Invalid embedded proxy request URL: {error}"));
            let _ = request.respond(response);
            return;
        }
    };

    let upstream_url = match extract_upstream_url(parsed_request_url.query()) {
        Ok(url) => url,
        Err(error) => {
            let response = build_error_http_response(400, &error);
            let _ = request.respond(response);
            return;
        }
    };

    let range_header = request
        .headers()
        .iter()
        .find(|header| header.field.equiv("Range"))
        .map(|header| header.value.as_str().to_string());

    let response = match build_proxy_response(&upstream_url, is_head, range_header.as_deref()) {
        Ok(response) => response,
        Err(error) => build_error_http_response(502, &error).boxed(),
    };

    let _ = request.respond(response);
}

fn build_proxy_response(
    upstream_url: &str,
    is_head: bool,
    range_header: Option<&str>,
) -> Result<ResponseBox, String> {
    let mut upstream = fetch_upstream(upstream_url, range_header)?;
    let status = upstream.status();
    let final_upstream_url = upstream.url().to_string();
    let upstream_headers = upstream.headers().clone();
    let content_type = upstream_headers
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .map(str::to_owned);

    let mut prefix = Vec::new();
    if !is_head {
        let mut buffer = [0_u8; MANIFEST_PEEK_BYTES];
        let read_len = upstream.read(&mut buffer).map_err(|error| error.to_string())?;
        prefix.extend_from_slice(&buffer[..read_len]);
    }

    if is_manifest_response(&final_upstream_url, content_type.as_deref(), &prefix) {
        let mut body = prefix;

        if !is_head {
            upstream.read_to_end(&mut body).map_err(|error| error.to_string())?;
            body = rewrite_manifest(&final_upstream_url, &body)?;
        }

        let mut response = Response::from_data(body).with_status_code(StatusCode(status.as_u16()));
        add_header(&mut response, "Access-Control-Allow-Origin", "*")?;
        add_header(&mut response, "Cache-Control", "no-store")?;
        add_header(&mut response, "Content-Type", "application/vnd.apple.mpegurl")?;

        for header_name in [
            reqwest::header::ACCEPT_RANGES,
            reqwest::header::CONTENT_RANGE,
            reqwest::header::ETAG,
            reqwest::header::LAST_MODIFIED,
        ] {
            if let Some(value) = upstream_headers.get(&header_name).and_then(|value| value.to_str().ok()) {
                add_header(&mut response, header_name.as_str(), value)?;
            }
        }

        return Ok(response.boxed());
    }

    let content_length = upstream_headers
        .get(reqwest::header::CONTENT_LENGTH)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<usize>().ok());

    let mut response = Response::new(
        StatusCode(status.as_u16()),
        Vec::new(),
        Cursor::new(prefix).chain(upstream),
        content_length,
        None,
    );

    add_header(&mut response, "Access-Control-Allow-Origin", "*")?;
    add_header(&mut response, "Cache-Control", "no-store")?;

    if let Some(content_type) = content_type.as_deref() {
        add_header(&mut response, "Content-Type", content_type)?;
    }

    for header_name in [
        reqwest::header::ACCEPT_RANGES,
        reqwest::header::CONTENT_RANGE,
        reqwest::header::ETAG,
        reqwest::header::LAST_MODIFIED,
    ] {
        if let Some(value) = upstream_headers.get(&header_name).and_then(|value| value.to_str().ok()) {
            add_header(&mut response, header_name.as_str(), value)?;
        }
    }

    Ok(response.boxed())
}

fn fetch_upstream(
    upstream_url: &str,
    range_header: Option<&str>,
) -> Result<reqwest::blocking::Response, String> {
    let client = Client::builder()
        .redirect(reqwest::redirect::Policy::limited(10))
        .user_agent(USER_AGENT)
        .build()
        .map_err(|error| error.to_string())?;

    let mut upstream_request = client
        .get(upstream_url)
        .header(reqwest::header::ACCEPT, "*/*")
        .header(reqwest::header::ACCEPT_LANGUAGE, "en-US,en;q=0.9")
        .header(reqwest::header::CACHE_CONTROL, "no-cache")
        .header(reqwest::header::PRAGMA, "no-cache");

    if let Some(range_header) = range_header {
        upstream_request = upstream_request.header(reqwest::header::RANGE, range_header);
    }

    upstream_request.send().map_err(|error| error.to_string())
}

fn extract_upstream_url(query: Option<&str>) -> Result<String, String> {
    let source = query
        .ok_or_else(|| "Missing embedded proxy source URL.".to_string())?
        .split('&')
        .find_map(|pair| pair.strip_prefix("source="))
        .ok_or_else(|| "Missing embedded proxy source URL.".to_string())?;

    let bytes = URL_SAFE_NO_PAD
        .decode(source)
        .map_err(|error| format!("Invalid embedded proxy source URL: {error}"))?;
    let decoded = String::from_utf8(bytes).map_err(|error| error.to_string())?;
    Url::parse(&decoded).map_err(|error| error.to_string())?;
    Ok(decoded)
}

fn encode_proxy_url(stream_url: &str) -> Result<String, String> {
    Url::parse(stream_url).map_err(|error| error.to_string())?;
    let base_url = &ensure_proxy_server()?.base_url;
    let encoded = URL_SAFE_NO_PAD.encode(stream_url.as_bytes());
    Ok(format!("{base_url}/stream?source={encoded}"))
}

fn is_manifest_response(url: &str, content_type: Option<&str>, body_prefix: &[u8]) -> bool {
    url.contains(".m3u8")
        || content_type
            .map(|value| value.contains("mpegurl") || value.contains("vnd.apple.mpegurl"))
            .unwrap_or(false)
        || body_starts_with_manifest(body_prefix)
}

fn body_starts_with_manifest(body_prefix: &[u8]) -> bool {
    let body = body_prefix.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(body_prefix);
    let text = String::from_utf8_lossy(body);
    text.trim_start().starts_with("#EXTM3U")
}

fn rewrite_manifest(source_url: &str, body: &[u8]) -> Result<Vec<u8>, String> {
    let source = String::from_utf8(body.to_vec()).map_err(|error| error.to_string())?;
    let base_url = Url::parse(source_url).map_err(|error| error.to_string())?;
    let rewritten = source
        .lines()
        .map(|line| rewrite_manifest_line(line, &base_url))
        .collect::<Result<Vec<_>, _>>()?
        .join("\n");

    Ok(rewritten.into_bytes())
}

fn rewrite_manifest_line(line: &str, base_url: &Url) -> Result<String, String> {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return Ok(String::new());
    }

    if trimmed.starts_with('#') {
        return rewrite_tag_uri(line, base_url);
    }

    let resolved = resolve_manifest_url(base_url, trimmed)?;
    encode_proxy_url(resolved.as_ref())
}

fn rewrite_tag_uri(line: &str, base_url: &Url) -> Result<String, String> {
    let Some(prefix_index) = line.find("URI=\"") else {
        return Ok(line.to_string());
    };

    let value_start = prefix_index + 5;
    let remaining = &line[value_start..];
    let Some(value_end_offset) = remaining.find('"') else {
        return Ok(line.to_string());
    };

    let value_end = value_start + value_end_offset;
    let original = &line[value_start..value_end];
    let resolved = resolve_manifest_url(base_url, original)?;
    let proxied = encode_proxy_url(resolved.as_ref())?;

    Ok(format!("{}{}{}", &line[..value_start], proxied, &line[value_end..]))
}

fn resolve_manifest_url(base_url: &Url, candidate: &str) -> Result<Url, String> {
    Url::parse(candidate)
        .or_else(|_| base_url.join(candidate))
        .map_err(|error| error.to_string())
}

fn add_header<R: std::io::Read>(response: &mut Response<R>, name: &str, value: &str) -> Result<(), String> {
    let header = Header::from_bytes(name.as_bytes(), value.as_bytes())
        .map_err(|_| format!("Invalid response header: {name}"))?;
    response.add_header(header);
    Ok(())
}

fn build_error_http_response(status: u16, message: &str) -> Response<std::io::Cursor<Vec<u8>>> {
    let mut response = Response::from_data(message.as_bytes().to_vec()).with_status_code(StatusCode(status));
    let _ = add_header(&mut response, "Content-Type", "text/plain; charset=utf-8");
    let _ = add_header(&mut response, "Cache-Control", "no-store");
    let _ = add_header(&mut response, "Access-Control-Allow-Origin", "*");
    response
}
