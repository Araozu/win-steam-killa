use std::fs::{self, File};
use std::io::Write;
#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::clock::LogicalDay;
use crate::limits::DEFAULT_STATE_FILE;

pub const CURRENT_STATE_VERSION: i32 = 8;

/// Crash-safe atomic file writer:
/// 1. Writes new content to `<path>.tmp`, applies optional Unix mode, fsyncs.
/// 2. Optionally updates `<path>.bak` atomically via `<path>.bak.tmp` + fsync + rename
///    (skipped if `preserve_existing_backup` is true so a known-good backup is never
///    clobbered by a corrupt primary).
/// 3. Renames `<path>.tmp` -> `<path>`.
/// 4. Fsyncs the parent directory so the rename is durable across power loss.
pub fn atomic_write_durable(
    path: &Path,
    bytes: &[u8],
    mode: Option<u32>,
    keep_backup: bool,
    preserve_existing_backup: bool,
) -> std::io::Result<()> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    fs::create_dir_all(parent)?;

    let mut tmp_os = path.as_os_str().to_os_string();
    tmp_os.push(".tmp");
    let tmp = PathBuf::from(tmp_os);

    let write_tmp = (|| -> std::io::Result<()> {
        let mut file = File::create(&tmp)?;
        #[cfg(unix)]
        if let Some(m) = mode {
            fs::set_permissions(&tmp, fs::Permissions::from_mode(m))?;
        }
        file.write_all(bytes)?;
        file.sync_all()?;
        Ok(())
    })();
    if let Err(err) = write_tmp {
        let _ = fs::remove_file(&tmp);
        return Err(err);
    }

    if keep_backup && !preserve_existing_backup && path.exists() {
        let mut bak_os = path.as_os_str().to_os_string();
        bak_os.push(".bak");
        let bak = PathBuf::from(&bak_os);
        let mut bak_tmp_os = bak_os;
        bak_tmp_os.push(".tmp");
        let bak_tmp = PathBuf::from(bak_tmp_os);

        let write_bak = (|| -> std::io::Result<()> {
            let existing = fs::read(path)?;
            let mut file = File::create(&bak_tmp)?;
            #[cfg(unix)]
            if let Some(m) = mode {
                fs::set_permissions(&bak_tmp, fs::Permissions::from_mode(m))?;
            }
            file.write_all(&existing)?;
            file.sync_all()?;
            fs::rename(&bak_tmp, &bak)?;
            sync_parent_dir(parent)?;
            Ok(())
        })();
        if let Err(err) = write_bak {
            let _ = fs::remove_file(&bak_tmp);
            let _ = fs::remove_file(&tmp);
            return Err(err);
        }
    }

    if let Err(err) = fs::rename(&tmp, path) {
        let _ = fs::remove_file(&tmp);
        return Err(err);
    }
    sync_parent_dir(parent)?;
    Ok(())
}

pub fn sync_parent_dir(dir: &Path) -> std::io::Result<()> {
    let d = File::open(dir)?;
    d.sync_all()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameRecord {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub path: String,
    #[serde(default)]
    pub total_seconds: f64,
    #[serde(default)]
    pub last_seen_utc: Option<DateTime<Utc>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TimerState {
    #[serde(default = "default_version")]
    pub version: i32,
    #[serde(default)]
    pub logical_day: String,
    #[serde(default)]
    pub base_budget_seconds: i64,
    #[serde(default)]
    pub carry_in_seconds: i64,
    #[serde(default)]
    pub borrowed_seconds: i64,
    #[serde(default)]
    pub borrow_count: i32,
    #[serde(default)]
    pub advance_deduction_seconds: i64,
    #[serde(default)]
    pub used_seconds: f64,
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub session_start_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub session_used_seconds: f64,
    #[serde(default)]
    pub last_session_seconds: f64,
    #[serde(default)]
    pub last_tick_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub warned_15m: bool,
    #[serde(default)]
    pub session_warned: bool,
    #[serde(default)]
    pub kill_notified: bool,
    #[serde(default)]
    pub cooldown_until_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub seen_games: Vec<String>,
    #[serde(default)]
    pub game_history: Vec<GameRecord>,
    #[serde(default)]
    pub dota_week_start: String,
    #[serde(default)]
    pub dota_weekday_used_seconds: f64,
    #[serde(default)]
    pub dota_weekend_used_seconds: f64,
    #[serde(default)]
    pub dota_weekday_earned_seconds: i64,
    #[serde(default)]
    pub dota_weekend_earned_seconds: i64,
    #[serde(default)]
    pub dota_warned: bool,
    #[serde(default)]
    pub dota_notified: bool,
    #[serde(default)]
    pub workout_last_sync_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub dota_forbidden_notified: bool,
}

fn default_version() -> i32 {
    CURRENT_STATE_VERSION
}

impl Default for TimerState {
    fn default() -> Self {
        Self {
            version: CURRENT_STATE_VERSION,
            logical_day: String::new(),
            base_budget_seconds: 0,
            carry_in_seconds: 0,
            borrowed_seconds: 0,
            borrow_count: 0,
            advance_deduction_seconds: 0,
            used_seconds: 0.0,
            running: false,
            session_start_utc: None,
            session_used_seconds: 0.0,
            last_session_seconds: 0.0,
            last_tick_utc: None,
            warned_15m: false,
            session_warned: false,
            kill_notified: false,
            cooldown_until_utc: None,
            seen_games: Vec::new(),
            game_history: Vec::new(),
            dota_week_start: String::new(),
            dota_weekday_used_seconds: 0.0,
            dota_weekend_used_seconds: 0.0,
            dota_weekday_earned_seconds: 0,
            dota_weekend_earned_seconds: 0,
            dota_warned: false,
            dota_notified: false,
            workout_last_sync_utc: None,
            dota_forbidden_notified: false,
        }
    }
}

#[derive(Debug, Error)]
pub enum StateStoreError {
    #[error("SteamTimer state and backup are unreadable; refusing to erase usage.")]
    CorruptState,
    #[error("I/O error accessing timer state: {0}")]
    Io(#[from] std::io::Error),
    #[error("JSON serialization error: {0}")]
    Json(#[from] serde_json::Error),
}

struct StoreInner {
    /// Set when `load()` had to fall back to `.bak` because primary was corrupt.
    /// The next `save()` preserves `.bak` until primary is cleanly replaced.
    primary_corrupt: bool,
}

pub struct StateStore {
    file: PathBuf,
    inner: Mutex<StoreInner>,
}

impl StateStore {
    pub fn new(path: Option<PathBuf>) -> Self {
        Self {
            file: path.unwrap_or_else(|| PathBuf::from(DEFAULT_STATE_FILE)),
            inner: Mutex::new(StoreInner {
                primary_corrupt: false,
            }),
        }
    }

    pub fn path(&self) -> &Path {
        &self.file
    }

    fn backup_path(&self) -> PathBuf {
        let mut s = self.file.as_os_str().to_os_string();
        s.push(".bak");
        PathBuf::from(s)
    }

    pub fn load(&self) -> Result<Option<TimerState>, StateStoreError> {
        let mut guard = self.inner.lock().unwrap();
        let candidates = [(self.file.clone(), false), (self.backup_path(), true)];
        let mut found = false;

        for (path, is_backup) in candidates {
            if !path.exists() {
                continue;
            }
            found = true;
            let Ok(text) = fs::read_to_string(&path) else {
                continue;
            };
            let Ok(state) = serde_json::from_str::<TimerState>(&text) else {
                continue;
            };
            if state.version != CURRENT_STATE_VERSION {
                guard.primary_corrupt = is_backup;
                return Ok(Some(state));
            }
            if LogicalDay::parse(&state.logical_day).is_some()
                && state.used_seconds.is_finite()
                && state.used_seconds >= 0.0
                && state.session_used_seconds.is_finite()
                && state.session_used_seconds >= 0.0
                && state.dota_weekday_used_seconds.is_finite()
                && state.dota_weekday_used_seconds >= 0.0
                && state.dota_weekend_used_seconds.is_finite()
                && state.dota_weekend_used_seconds >= 0.0
                && state.dota_weekday_earned_seconds >= 0
                && state.dota_weekend_earned_seconds >= 0
            {
                guard.primary_corrupt = is_backup;
                return Ok(Some(state));
            }
        }

        if found {
            return Err(StateStoreError::CorruptState);
        }
        guard.primary_corrupt = false;
        Ok(None)
    }

    pub fn save(&self, state: &TimerState) -> Result<(), StateStoreError> {
        let mut guard = self.inner.lock().unwrap();
        let bytes = serde_json::to_vec_pretty(state)?;
        atomic_write_durable(&self.file, &bytes, None, true, guard.primary_corrupt)?;
        guard.primary_corrupt = false;
        Ok(())
    }

    pub fn clear(&self) {
        let mut guard = self.inner.lock().unwrap();
        let _ = fs::remove_file(&self.file);
        let _ = fs::remove_file(self.backup_path());
        if let Some(parent) = self.file.parent() {
            let _ = sync_parent_dir(parent);
        }
        guard.primary_corrupt = false;
    }
}
