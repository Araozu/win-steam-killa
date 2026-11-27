use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::Utc;
use reqwest::{Client, StatusCode, redirect};
use serde::{Deserialize, Serialize};
use steamtimer_core::engine::TimerEngine;
use steamtimer_core::protocol::WorkoutSyncStatus;
use steamtimer_core::state::atomic_write_durable;
use steamtimer_core::workout::{
    WORKOUT_PAGE_SIZE, WorkoutAcknowledgement, WorkoutArchive, WorkoutCredit, WorkoutDelivery,
};
use tokio::sync::Notify;
use tracing::warn;
use url::Url;

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutConnectionSettings {
    #[serde(default)]
    pub base_url: String,
    #[serde(default)]
    pub api_token: String,
}

impl WorkoutConnectionSettings {
    pub fn is_configured(&self) -> bool {
        !self.base_url.trim().is_empty() && !self.api_token.trim().is_empty()
    }

    pub fn from_env() -> Self {
        Self {
            base_url: std::env::var("WORKOUT_BASE_URL").unwrap_or_default(),
            api_token: std::env::var("WORKOUT_API_TOKEN").unwrap_or_default(),
        }
    }
}

pub struct WorkoutConnectionStore {
    path: PathBuf,
    current: Mutex<WorkoutConnectionSettings>,
}

impl WorkoutConnectionStore {
    pub fn new(path: PathBuf, fallback: WorkoutConnectionSettings) -> Self {
        let loaded = Self::load(&path, fallback);
        Self {
            path,
            current: Mutex::new(loaded),
        }
    }

    pub fn current(&self) -> WorkoutConnectionSettings {
        self.current.lock().unwrap().clone()
    }

    pub fn save(&self, url: Option<&str>, token: Option<&str>) -> Option<String> {
        let raw_url = url.unwrap_or("").trim();
        if raw_url.is_empty() || raw_url.len() > 512 {
            return Some(
                "Enter an HTTPS receiver URL without credentials, a query, or a fragment.".into(),
            );
        }
        let Ok(parsed) = Url::parse(raw_url) else {
            return Some(
                "Enter an HTTPS receiver URL without credentials, a query, or a fragment.".into(),
            );
        };
        if parsed.scheme() != "https"
            || !parsed.username().is_empty()
            || parsed.password().is_some()
            || parsed.query().is_some()
            || parsed.fragment().is_some()
            || parsed.host_str().is_none()
        {
            return Some(
                "Enter an HTTPS receiver URL without credentials, a query, or a fragment.".into(),
            );
        }

        let normalized_url = parsed.as_str().trim_end_matches('/').to_string();
        let trimmed_token = token.map(str::trim).unwrap_or("");
        if trimmed_token.len() > 512
            || (!trimmed_token.is_empty() && !Self::is_bearer_token(trimmed_token))
        {
            return Some("Enter the bearer token by itself, without the Bearer prefix.".into());
        }

        let mut guard = self.current.lock().unwrap();
        if trimmed_token.is_empty() && normalized_url != guard.base_url.trim_end_matches('/') {
            return Some("Enter the token for the new receiver URL.".into());
        }
        let updated_token = if trimmed_token.is_empty() {
            guard.api_token.clone()
        } else {
            trimmed_token.to_string()
        };
        if updated_token.trim().is_empty() {
            return Some("Enter the receiver's bearer token.".into());
        }

        let settings = WorkoutConnectionSettings {
            base_url: normalized_url,
            api_token: updated_token,
        };

        let write_res = serde_json::to_vec_pretty(&settings)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
            .and_then(|bytes| atomic_write_durable(&self.path, &bytes, Some(0o600), false, false));

        if let Err(err) = write_res {
            warn!("Workout settings could not be saved: {err}");
            return Some("The service couldn't save the workout settings.".into());
        }

        *guard = settings;
        None
    }

    fn is_bearer_token(token: &str) -> bool {
        let value = token.trim_end_matches('=');
        !value.is_empty()
            && value
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || "-._~+/".contains(c))
    }

    fn load(path: &Path, fallback: WorkoutConnectionSettings) -> WorkoutConnectionSettings {
        let Ok(text) = fs::read_to_string(path) else {
            return fallback;
        };
        match serde_json::from_str::<WorkoutConnectionSettings>(&text) {
            Ok(saved) if saved.is_configured() => saved,
            Ok(_) => fallback,
            Err(err) => {
                warn!("Workout settings could not be loaded: {err}");
                fallback
            }
        }
    }
}

#[derive(Debug)]
enum SyncError {
    Unauthorized,
    HttpStatus(u16),
    Timeout,
    Network(String),
    InvalidFeed(String),
    ArchiveIo(String),
}

impl SyncError {
    fn log_detail(&self) -> String {
        match self {
            SyncError::Unauthorized => "unauthorized/forbidden".into(),
            SyncError::HttpStatus(code) => format!("HTTP {code}"),
            SyncError::Timeout => "timeout".into(),
            SyncError::Network(msg) => format!("network: {msg}"),
            SyncError::InvalidFeed(msg) => format!("invalid feed: {msg}"),
            SyncError::ArchiveIo(msg) => format!("archive io: {msg}"),
        }
    }

    fn describe(&self) -> String {
        match self {
            SyncError::Unauthorized => "The receiver rejected the bearer token.".into(),
            SyncError::HttpStatus(code) => format!("The receiver returned HTTP {code}."),
            SyncError::Timeout => "The receiver didn't respond in time.".into(),
            SyncError::Network(_) => {
                "Couldn't connect to the receiver. Check its URL and your network.".into()
            }
            SyncError::InvalidFeed(_) => "The receiver sent an unreadable workout feed.".into(),
            SyncError::ArchiveIo(_) => {
                "The service couldn't update the local workout archive.".into()
            }
        }
    }
}

fn is_valid_guid_n(s: &str) -> bool {
    s.len() == 32 && s.bytes().all(|b| b.is_ascii_hexdigit())
}

async fn sync_archive(
    http: &Client,
    base_url: &str,
    api_token: &str,
    archive_path: &Path,
) -> Result<WorkoutArchive, SyncError> {
    let base = base_url.trim_end_matches('/');
    let mut archive: Option<WorkoutArchive> = None;

    loop {
        let resp = http
            .get(format!("{base}/api/workouts"))
            .bearer_auth(api_token)
            .send()
            .await
            .map_err(|e| {
                if e.is_timeout() {
                    SyncError::Timeout
                } else {
                    SyncError::Network(e.to_string())
                }
            })?;

        let status = resp.status();
        if status == StatusCode::UNAUTHORIZED || status == StatusCode::FORBIDDEN {
            return Err(SyncError::Unauthorized);
        }
        if !status.is_success() {
            return Err(SyncError::HttpStatus(status.as_u16()));
        }

        let deliveries: Vec<WorkoutDelivery> = resp
            .json()
            .await
            .map_err(|e| SyncError::InvalidFeed(e.to_string()))?;

        if deliveries.is_empty() {
            if let Some(a) = archive {
                return Ok(a);
            }
            let mut loaded = WorkoutArchive::load(archive_path);
            if loaded.prune(Utc::now()) {
                loaded.link_sensors();
                loaded
                    .save(archive_path)
                    .map_err(|e| SyncError::ArchiveIo(e.to_string()))?;
            }
            return Ok(loaded);
        }

        if deliveries.len() > WORKOUT_PAGE_SIZE
            || deliveries.iter().any(|item| !is_valid_guid_n(&item.id))
        {
            return Err(SyncError::InvalidFeed("invalid delivery batch".into()));
        }

        let a = archive.get_or_insert_with(|| WorkoutArchive::load(archive_path));
        for delivery in &deliveries {
            a.apply(&delivery.data);
        }
        a.prune(Utc::now());
        a.link_sensors();
        a.save(archive_path)
            .map_err(|e| SyncError::ArchiveIo(e.to_string()))?;

        let ack_ids: Vec<String> = deliveries.iter().map(|d| d.id.clone()).collect();
        let ack_resp = http
            .post(format!("{base}/api/workouts/ack"))
            .bearer_auth(api_token)
            .json(&WorkoutAcknowledgement { ids: ack_ids })
            .send()
            .await
            .map_err(|e| {
                if e.is_timeout() {
                    SyncError::Timeout
                } else {
                    SyncError::Network(e.to_string())
                }
            })?;

        let ack_status = ack_resp.status();
        if ack_status == StatusCode::UNAUTHORIZED || ack_status == StatusCode::FORBIDDEN {
            return Err(SyncError::Unauthorized);
        }
        if !ack_status.is_success() {
            return Err(SyncError::HttpStatus(ack_status.as_u16()));
        }

        if deliveries.len() < WORKOUT_PAGE_SIZE {
            return Ok(archive.unwrap());
        }
    }
}

struct WorkerState {
    pending: bool,
    running: bool,
    last_result: Option<String>,
}

pub struct WorkoutSyncWorker {
    engine: Arc<TimerEngine>,
    settings: Arc<WorkoutConnectionStore>,
    archive_path: PathBuf,
    http: Client,
    state: Mutex<WorkerState>,
    wake: Notify,
}

impl WorkoutSyncWorker {
    pub fn new(
        engine: Arc<TimerEngine>,
        settings: Arc<WorkoutConnectionStore>,
        archive_path: PathBuf,
    ) -> Self {
        let http = Client::builder()
            .redirect(redirect::Policy::none())
            .timeout(Duration::from_secs(30))
            .build()
            .expect("reqwest client build");

        Self {
            engine,
            settings,
            archive_path,
            http,
            state: Mutex::new(WorkerState {
                pending: false,
                running: false,
                last_result: None,
            }),
            wake: Notify::new(),
        }
    }

    pub fn status(&self) -> WorkoutSyncStatus {
        let st = self.state.lock().unwrap();
        let (last_success_utc, earned_seconds) = self.engine.get_workout_credit_status();
        let cfg = self.settings.current();
        WorkoutSyncStatus {
            base_url: cfg.base_url,
            has_token: !cfg.api_token.trim().is_empty(),
            syncing: st.running || st.pending,
            last_success_utc,
            earned_seconds,
            last_result: st.last_result.clone(),
        }
    }

    pub fn save(&self, url: Option<&str>, token: Option<&str>) -> Option<String> {
        let mut st = self.state.lock().unwrap();
        if st.running || st.pending {
            return Some("Wait for the current sync to finish before saving.".into());
        }
        let err = self.settings.save(url, token);
        if err.is_none() {
            st.last_result = None;
        }
        err
    }

    pub fn request_sync(&self) -> Option<String> {
        let mut st = self.state.lock().unwrap();
        if !self.settings.current().is_configured() {
            return Some("Save the receiver URL and token first.".into());
        }
        if !st.running && !st.pending {
            st.pending = true;
            self.wake.notify_one();
        }
        None
    }

    pub async fn run(self: Arc<Self>) {
        loop {
            let configured = {
                let mut st = self.state.lock().unwrap();
                st.pending = false;
                let ok = self.settings.current().is_configured();
                st.running = ok;
                ok
            };

            if configured {
                let cfg = self.settings.current();
                let (week, today) = self.engine.current_week_and_day();
                match sync_archive(&self.http, &cfg.base_url, &cfg.api_token, &self.archive_path)
                    .await
                {
                    Ok(archive) => {
                        let earned = WorkoutCredit::for_week(&archive, &week, &today);
                        let result_msg = match self.engine.apply_workout_earned(
                            &week,
                            earned,
                            Utc::now(),
                        ) {
                            Ok(()) => "Sync complete.".into(),
                            Err(err) => {
                                warn!("Failed to persist updated workout credit to timer state: {err}");
                                "Sync fetched workouts, but failed to save updated timer state."
                                    .into()
                            }
                        };
                        let mut st = self.state.lock().unwrap();
                        st.last_result = Some(result_msg);
                        st.running = false;
                    }
                    Err(err) => {
                        warn!("Workout credit sync failed ({})", err.log_detail());
                        let mut st = self.state.lock().unwrap();
                        st.last_result = Some(err.describe());
                        st.running = false;
                    }
                }
            }

            let _ = tokio::time::timeout(Duration::from_secs(15 * 60), self.wake.notified()).await;
        }
    }
}
