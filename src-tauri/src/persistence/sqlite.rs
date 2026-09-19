use std::path::Path;

use chrono::Utc;
use rusqlite::{params, Connection, OptionalExtension};
use thiserror::Error;
use uuid::Uuid;

use crate::models::{ContinueWatchingItem, FavoriteItem, SaveProfileRequest, XtreamProfile};

pub const INITIAL_MIGRATION: &str = r#"
CREATE TABLE IF NOT EXISTS profiles (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    server_url TEXT NOT NULL,
    username TEXT NOT NULL,
    password TEXT NOT NULL,
    status TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    max_connections INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS favorites (
    id TEXT PRIMARY KEY,
    profile_id TEXT,
    content_id TEXT NOT NULL,
    title TEXT NOT NULL,
    kind TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS resume_history (
    id TEXT PRIMARY KEY,
    profile_id TEXT,
    content_id TEXT,
    title TEXT NOT NULL,
    kind TEXT NOT NULL,
    detail TEXT NOT NULL,
    progress_percent INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);
"#;

#[derive(Debug, Error)]
pub enum DatabaseError {
    #[error("database I/O failed: {0}")]
    Io(#[from] std::io::Error),
    #[error("database query failed: {0}")]
    Sql(#[from] rusqlite::Error),
    #[error("profile not found")]
    ProfileNotFound,
}

#[derive(Debug, Clone)]
pub struct StoredProfileCredentials {
    pub server_url: String,
    pub username: String,
    pub password: String,
}

pub fn ensure_database(path: &Path) -> Result<(), DatabaseError> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }

    let connection = Connection::open(path)?;
    connection.execute_batch(INITIAL_MIGRATION)?;
    remove_demo_rows(&connection)?;
    Ok(())
}

pub fn list_profiles(path: &Path) -> Result<Vec<XtreamProfile>, DatabaseError> {
    let connection = Connection::open(path)?;
    let mut statement = connection.prepare(
        "SELECT id, name, server_url, username, status, expires_at, max_connections
         FROM profiles
         ORDER BY updated_at DESC, name ASC",
    )?;

    let rows = statement.query_map([], |row| {
        Ok(XtreamProfile {
            id: row.get(0)?,
            name: row.get(1)?,
            server_url: row.get(2)?,
            username: row.get(3)?,
            status: row.get(4)?,
            expires_at: row.get(5)?,
            max_connections: row.get(6)?,
        })
    })?;

    rows.collect::<Result<Vec<_>, _>>().map_err(DatabaseError::from)
}

pub fn get_profile_credentials(
    path: &Path,
    profile_id: &str,
) -> Result<StoredProfileCredentials, DatabaseError> {
    let connection = Connection::open(path)?;
    connection
        .query_row(
            "SELECT server_url, username, password
             FROM profiles WHERE id = ?1",
            [profile_id],
            |row| {
                Ok(StoredProfileCredentials {
                    server_url: row.get(0)?,
                    username: row.get(1)?,
                    password: row.get(2)?,
                })
            },
        )
        .optional()?
        .ok_or(DatabaseError::ProfileNotFound)
}

pub fn save_profile(
    path: &Path,
    request: &SaveProfileRequest,
    status: &str,
    expires_at: &str,
    max_connections: i64,
) -> Result<XtreamProfile, DatabaseError> {
    let connection = Connection::open(path)?;
    let id = request.id.clone().unwrap_or_else(|| Uuid::new_v4().to_string());
    let now = Utc::now().to_rfc3339();

    connection.execute(
        "INSERT INTO profiles (
            id, name, server_url, username, password, status, expires_at, max_connections, created_at, updated_at
         ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?9)
         ON CONFLICT(id) DO UPDATE SET
            name = excluded.name,
            server_url = excluded.server_url,
            username = excluded.username,
            password = excluded.password,
            status = excluded.status,
            expires_at = excluded.expires_at,
            max_connections = excluded.max_connections,
            updated_at = excluded.updated_at",
        params![
            id,
            request.name,
            request.server_url,
            request.username,
            request.password,
            status,
            expires_at,
            max_connections,
            now,
        ],
    )?;

    Ok(XtreamProfile {
        id,
        name: request.name.clone(),
        server_url: request.server_url.clone(),
        username: request.username.clone(),
        status: status.to_string(),
        expires_at: expires_at.to_string(),
        max_connections,
    })
}

pub fn list_favorites(path: &Path) -> Result<Vec<FavoriteItem>, DatabaseError> {
    let connection = Connection::open(path)?;
    let mut statement = connection.prepare(
        "SELECT id, content_id, title, kind
         FROM favorites
         ORDER BY created_at DESC",
    )?;

    let rows = statement.query_map([], |row| {
        Ok(FavoriteItem {
            id: row.get(0)?,
            content_id: row.get(1)?,
            title: row.get(2)?,
            kind: row.get(3)?,
        })
    })?;

    rows.collect::<Result<Vec<_>, _>>().map_err(DatabaseError::from)
}

pub fn list_resume_history(path: &Path) -> Result<Vec<ContinueWatchingItem>, DatabaseError> {
    let connection = Connection::open(path)?;
    let mut statement = connection.prepare(
        "SELECT id, content_id, title, kind, detail, progress_percent
         FROM resume_history
         ORDER BY updated_at DESC",
    )?;

    let rows = statement.query_map([], |row| {
        Ok(ContinueWatchingItem {
            id: row.get(0)?,
            content_id: row.get(1)?,
            title: row.get(2)?,
            kind: row.get(3)?,
            detail: row.get(4)?,
            progress_percent: row.get(5)?,
        })
    })?;

    rows.collect::<Result<Vec<_>, _>>().map_err(DatabaseError::from)
}

fn remove_demo_rows(connection: &Connection) -> Result<(), DatabaseError> {
    connection.execute(
        "DELETE FROM favorites WHERE profile_id IN ('primary', 'backup') OR id IN ('fav-1', 'fav-2')",
        [],
    )?;
    connection.execute(
        "DELETE FROM resume_history WHERE profile_id IN ('primary', 'backup') OR id IN ('cw-1', 'cw-2')",
        [],
    )?;
    connection.execute(
        "DELETE FROM profiles WHERE id IN ('primary', 'backup') OR server_url IN ('https://iptv.example.com', 'https://backup.example.net')",
        [],
    )?;

    Ok(())
}

pub fn database_name() -> &'static str {
    "xtream-player.sqlite"
}

pub fn add_favorite(
    path: &Path,
    profile_id: &str,
    content_id: &str,
    title: &str,
    kind: &str,
) -> Result<FavoriteItem, DatabaseError> {
    let connection = Connection::open(path)?;
    let existing_id: Option<String> = connection
        .query_row(
            "SELECT id FROM favorites WHERE profile_id = ?1 AND content_id = ?2",
            params![profile_id, content_id],
            |row| row.get(0),
        )
        .optional()?;

    if let Some(id) = existing_id {
        return Ok(FavoriteItem {
            id,
            content_id: content_id.to_string(),
            title: title.to_string(),
            kind: kind.to_string(),
        });
    }

    let id = Uuid::new_v4().to_string();
    connection.execute(
        "INSERT INTO favorites (id, profile_id, content_id, title, kind, created_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![id, profile_id, content_id, title, kind, Utc::now().to_rfc3339()],
    )?;

    Ok(FavoriteItem {
        id,
        content_id: content_id.to_string(),
        title: title.to_string(),
        kind: kind.to_string(),
    })
}

pub fn remove_favorite(path: &Path, favorite_id: &str) -> Result<(), DatabaseError> {
    let connection = Connection::open(path)?;
    connection.execute("DELETE FROM favorites WHERE id = ?1", [favorite_id])?;
    Ok(())
}

pub fn save_resume_checkpoint(
    path: &Path,
    profile_id: &str,
    content_id: Option<&str>,
    title: &str,
    kind: &str,
    detail: &str,
    progress_percent: i64,
) -> Result<ContinueWatchingItem, DatabaseError> {
    let connection = Connection::open(path)?;
    let existing_id: Option<String> = if let Some(content_id) = content_id {
        connection
            .query_row(
                "SELECT id FROM resume_history WHERE profile_id = ?1 AND content_id = ?2",
                params![profile_id, content_id],
                |row| row.get(0),
            )
            .optional()?
    } else {
        None
    };

    let id = existing_id.unwrap_or_else(|| Uuid::new_v4().to_string());
    connection.execute(
        "INSERT INTO resume_history (id, profile_id, content_id, title, kind, detail, progress_percent, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
         ON CONFLICT(id) DO UPDATE SET
           title = excluded.title,
           kind = excluded.kind,
           detail = excluded.detail,
           progress_percent = excluded.progress_percent,
           updated_at = excluded.updated_at,
           content_id = excluded.content_id,
           profile_id = excluded.profile_id",
        params![
            id,
            profile_id,
            content_id,
            title,
            kind,
            detail,
            progress_percent,
            Utc::now().to_rfc3339(),
        ],
    )?;

    Ok(ContinueWatchingItem {
        id,
        content_id: content_id.map(|value| value.to_string()),
        title: title.to_string(),
        kind: kind.to_string(),
        detail: detail.to_string(),
        progress_percent,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profile_request(id: Option<String>, name: &str, username: &str) -> SaveProfileRequest {
        SaveProfileRequest {
            id,
            name: name.to_string(),
            server_url: format!("https://{name}.example.com"),
            username: username.to_string(),
            password: format!("{username}-password"),
        }
    }

    #[test]
    fn creates_multiple_profiles_and_updates_only_the_requested_profile() {
        let path = std::env::temp_dir().join(format!(
            "xtream-player-profile-test-{}.sqlite",
            Uuid::new_v4()
        ));

        ensure_database(&path).expect("database should initialize");

        let first = save_profile(
            &path,
            &profile_request(None, "provider-a", "user-a"),
            "Active",
            "Unknown",
            1,
        )
        .expect("first profile should save");
        let second = save_profile(
            &path,
            &profile_request(None, "provider-b", "user-b"),
            "Active",
            "Unknown",
            2,
        )
        .expect("second profile should save");

        assert_ne!(first.id, second.id);
        assert_eq!(list_profiles(&path).expect("profiles should load").len(), 2);

        let updated = save_profile(
            &path,
            &profile_request(Some(first.id.clone()), "provider-a-updated", "user-a-new"),
            "Active",
            "2030-01-01",
            3,
        )
        .expect("existing profile should update");
        let profiles = list_profiles(&path).expect("updated profiles should load");

        assert_eq!(updated.id, first.id);
        assert_eq!(profiles.len(), 2);
        assert!(profiles.iter().any(|profile| {
            profile.id == first.id
                && profile.name == "provider-a-updated"
                && profile.username == "user-a-new"
        }));
        assert!(profiles.iter().any(|profile| {
            profile.id == second.id && profile.name == "provider-b" && profile.username == "user-b"
        }));

        drop(profiles);
        std::fs::remove_file(path).expect("test database should be removable");
    }
}
