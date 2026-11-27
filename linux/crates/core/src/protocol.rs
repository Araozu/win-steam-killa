use chrono::{DateTime, NaiveDateTime, Utc};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct IpcRequest {
    pub cmd: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub request_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub workout_base_url: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub workout_api_token: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutSyncStatus {
    pub base_url: String,
    pub has_token: bool,
    pub syncing: bool,
    pub last_success_utc: Option<DateTime<Utc>>,
    pub earned_seconds: i64,
    pub last_result: Option<String>,
}

/// Fields named `*_local` are policy-clock times (UTC-05), not the PC's time zone.
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct IpcResponse {
    pub ok: bool,
    #[serde(default)]
    pub error: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub workout_sync: Option<WorkoutSyncStatus>,
    #[serde(default)]
    pub service_instance: String,
    #[serde(default)]
    pub sequence: i64,
    #[serde(default)]
    pub logical_day: String,
    #[serde(default)]
    pub start_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub enforcing: bool,
    #[serde(default)]
    pub server_now_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub next_reset_local: Option<NaiveDateTime>,
    #[serde(default)]
    pub budget_seconds: i64,
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
    pub tomorrow_base_seconds: i64,
    #[serde(default)]
    pub next_borrow_effectiveness: f64,
    #[serde(default)]
    pub next_borrow_gain_seconds: i64,
    #[serde(default)]
    pub used_seconds: f64,
    #[serde(default)]
    pub remaining_seconds: f64,
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub steam_running: bool,
    #[serde(default)]
    pub session_elapsed_seconds: f64,
    #[serde(default)]
    pub session_remaining_seconds: f64,
    #[serde(default)]
    pub last_session_seconds: f64,
    #[serde(default)]
    pub in_cooldown: bool,
    #[serde(default)]
    pub cooldown_remaining_seconds: f64,
    #[serde(default)]
    pub cooldown_total_seconds: f64,
    #[serde(default)]
    pub cooldown_until_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub session_max_seconds: i64,
    #[serde(default)]
    pub dota_week_start: String,
    #[serde(default)]
    pub dota_budget_seconds: f64,
    #[serde(default)]
    pub dota_used_seconds: f64,
    #[serde(default)]
    pub dota_remaining_seconds: f64,
    #[serde(default)]
    pub dota_running: bool,
    #[serde(default)]
    pub dota_enforcing: bool,
    #[serde(default)]
    pub dota_resets_local: Option<NaiveDateTime>,
    #[serde(default)]
    pub dota_weekend_budget: bool,
    #[serde(default)]
    pub dota_carryover_seconds: f64,
    #[serde(default)]
    pub workout_dota_earned_seconds: i64,
    #[serde(default)]
    pub workout_last_sync_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    pub dota_forbidden: bool,
    #[serde(default)]
    pub outside_play_hours: bool,
    #[serde(default)]
    pub next_play_allowed_local: Option<NaiveDateTime>,
    #[serde(default)]
    pub window_ends_local: Option<NaiveDateTime>,
    #[serde(default)]
    pub live_clients: Vec<String>,
    #[serde(default)]
    pub live_games: Vec<String>,
    #[serde(default)]
    pub matched_orphans: Vec<String>,
    #[serde(default)]
    pub remembered_games: Vec<String>,
    #[serde(default)]
    pub killed_processes: usize,
    #[serde(default)]
    pub action_note: String,
}
