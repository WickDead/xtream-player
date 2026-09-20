-- Schema v1.
--
-- Table and column names deliberately match the previous Rust app's
-- src-tauri/src/persistence/sqlite.rs where the data is the same, so the
-- one-time importer is a plain INSERT ... SELECT. Everything new is additive.
--
-- Three bugs from the old schema are fixed here and must not be reintroduced:
--   1. favorites uniqueness now includes `kind`. The old dedupe was
--      (profile_id, content_id) only, and Xtream live and VOD stream_ids come
--      from the same numeric namespace, so favoriting live channel 1234 silently
--      blocked favoriting movie 1234.
--   2. profile_id is NOT NULL everywhere. list_favorites and list_resume_history
--      queried with no WHERE profile_id, leaking rows across providers.
--   3. hidden_categories is keyed by id AND name, and is per-profile. The old
--      version lived in localStorage keyed on display name only, so it broke
--      whenever a provider renamed a category and was shared across providers.

CREATE TABLE profiles (
    id                     TEXT    NOT NULL PRIMARY KEY,
    name                   TEXT    NOT NULL,
    server_url             TEXT    NOT NULL,
    username               TEXT    NOT NULL,
    -- Ciphertext, or UTF-8 bytes when password_protection = 'plaintext'.
    password_enc           BLOB,
    -- 'dpapi-user' | 'libsecret' | 'keychain' | 'plaintext'.
    -- Per row, because a database copied to another machine cannot be decrypted
    -- and must ask for the password again rather than crash.
    password_protection    TEXT    NOT NULL DEFAULT 'plaintext',
    status                 TEXT,
    expires_at             TEXT,
    max_connections        INTEGER,
    -- 'auto' | 'ts' | 'hls'. Auto prefers ts, which libvlc handles natively;
    -- a few providers serve broken ts and good hls.
    preferred_live_format  TEXT    NOT NULL DEFAULT 'auto',
    sort_index             INTEGER NOT NULL DEFAULT 0,
    created_at             TEXT    NOT NULL,
    updated_at             TEXT    NOT NULL
);

CREATE TABLE favorites (
    id          TEXT NOT NULL PRIMARY KEY,
    profile_id  TEXT NOT NULL,
    content_id  TEXT NOT NULL,
    kind        TEXT NOT NULL,
    title       TEXT,
    sort_index  INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT NOT NULL,
    UNIQUE (profile_id, content_id, kind),
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

CREATE INDEX ix_favorites_profile_kind ON favorites (profile_id, kind);

CREATE TABLE resume_history (
    id               TEXT    NOT NULL PRIMARY KEY,
    profile_id       TEXT    NOT NULL,
    content_id       TEXT    NOT NULL,
    kind             TEXT    NOT NULL,
    title            TEXT,
    detail           TEXT,
    -- Absolute position going forward. progress_percent is still written for
    -- compatibility with rows imported from the old schema.
    position_ms      INTEGER NOT NULL DEFAULT 0,
    duration_ms      INTEGER NOT NULL DEFAULT 0,
    progress_percent INTEGER NOT NULL DEFAULT 0,
    finished         INTEGER NOT NULL DEFAULT 0,
    series_id        TEXT,
    season_number    INTEGER,
    episode_number   INTEGER,
    updated_at       TEXT    NOT NULL,
    UNIQUE (profile_id, content_id, kind),
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

-- Continue Watching reads this exact shape.
CREATE INDEX ix_resume_active ON resume_history (profile_id, finished, updated_at DESC);

CREATE TABLE hidden_categories (
    profile_id    TEXT NOT NULL,
    -- 'live' | 'vod' | 'series'
    scope         TEXT NOT NULL,
    category_id   TEXT NOT NULL,
    -- Kept so a category that changes id can still be matched, and self-healed.
    category_name TEXT,
    created_at    TEXT NOT NULL,
    PRIMARY KEY (profile_id, scope, category_id),
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

CREATE INDEX ix_hidden_name ON hidden_categories (profile_id, scope, category_name);

-- Replaces every localStorage key the old app used.
CREATE TABLE settings (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT
);

-- Per-title track overrides. Keyed on language and track NAME, never on a raw
-- elementary-stream id: Phase 0 proved ids are not stable across a track switch
-- (libvlc rebuilds the ES on HLS alternate renditions and assigns a fresh id).
CREATE TABLE playback_prefs (
    profile_id       TEXT NOT NULL,
    content_id       TEXT NOT NULL,
    audio_lang       TEXT,
    audio_track_name TEXT,
    spu_lang         TEXT,
    spu_track_name   TEXT,
    updated_at       TEXT NOT NULL,
    PRIMARY KEY (profile_id, content_id),
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

-- Live has no resume; it gets recency instead, powering a "Recent" pseudo
-- category and the last-channel hotkey.
CREATE TABLE recent_channels (
    profile_id TEXT NOT NULL,
    channel_id TEXT NOT NULL,
    title      TEXT,
    watched_at TEXT NOT NULL,
    PRIMARY KEY (profile_id, channel_id),
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

CREATE TABLE downloads (
    id               TEXT    NOT NULL PRIMARY KEY,
    profile_id       TEXT    NOT NULL,
    kind             TEXT    NOT NULL,
    content_id       TEXT    NOT NULL,
    title            TEXT    NOT NULL,
    source_url       TEXT    NOT NULL,
    target_path      TEXT    NOT NULL,
    total_bytes      INTEGER NOT NULL DEFAULT 0,
    downloaded_bytes INTEGER NOT NULL DEFAULT 0,
    -- 0 once a server is observed answering Range with 200 instead of 206, so
    -- we stop re-requesting a range it will never honour.
    supports_range   INTEGER NOT NULL DEFAULT 1,
    state            TEXT    NOT NULL,
    error            TEXT,
    created_at       TEXT    NOT NULL,
    updated_at       TEXT    NOT NULL,
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

CREATE INDEX ix_downloads_state ON downloads (state, created_at);

-- ---- EPG ------------------------------------------------------------------

CREATE TABLE epg_channel (
    profile_id       TEXT NOT NULL,
    xmltv_channel_id TEXT NOT NULL,
    display_name     TEXT,
    icon             TEXT,
    PRIMARY KEY (profile_id, xmltv_channel_id)
);

-- Times are Unix seconds UTC INTEGER, never strings: the old app kept EPG
-- start/stop as raw provider strings, which cannot be range-queried.
CREATE TABLE epg_programme (
    profile_id       TEXT    NOT NULL,
    xmltv_channel_id TEXT    NOT NULL,
    start_utc        INTEGER NOT NULL,
    stop_utc         INTEGER NOT NULL,
    title            TEXT,
    sub_title        TEXT,
    description      TEXT,
    category         TEXT,
    episode_num      TEXT,
    PRIMARY KEY (profile_id, xmltv_channel_id, start_utc)
) WITHOUT ROWID;

CREATE INDEX ix_epg_window ON epg_programme (profile_id, start_utc, stop_utc);

-- Which live channel maps to which XMLTV id, and how we worked it out, so a
-- user override survives the next refresh.
CREATE TABLE epg_channel_map (
    profile_id       TEXT    NOT NULL,
    channel_id       TEXT    NOT NULL,
    xmltv_channel_id TEXT,
    match_method     TEXT,
    is_user_override INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (profile_id, channel_id),
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);

CREATE TABLE epg_refresh (
    profile_id       TEXT NOT NULL PRIMARY KEY,
    last_refresh_utc INTEGER,
    channel_count    INTEGER,
    programme_count  INTEGER,
    source           TEXT,
    error            TEXT,
    FOREIGN KEY (profile_id) REFERENCES profiles (id) ON DELETE CASCADE
);
