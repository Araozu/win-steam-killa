use std::time::Instant;

use chrono::{Datelike, Duration, NaiveDateTime};

use crate::clock::LogicalDay;
use crate::limits;
use crate::protocol::IpcResponse;

pub const DEFAULT_PROBLEM: &str = "Service unreachable — is steamtimerd running?";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayLevel {
    Unknown,
    Normal,
    Low,
    Blocked,
}

#[derive(Debug, Clone, Copy)]
pub struct TimerValues {
    pub remaining: f64,
    pub session_elapsed: f64,
    pub session_max: f64,
    pub dota_remaining: f64,
    pub playable: f64,
    pub cooldown_remaining: f64,
    pub session_active: bool,
}

pub struct TimerViewState {
    last: Option<IpcResponse>,
    received_at: Option<Instant>,
    service_available: bool,
    problem: String,
}

impl Default for TimerViewState {
    fn default() -> Self {
        Self {
            last: None,
            received_at: None,
            service_available: true,
            problem: DEFAULT_PROBLEM.to_string(),
        }
    }
}

impl TimerViewState {
    pub fn last(&self) -> Option<&IpcResponse> {
        self.last.as_ref()
    }

    pub fn service_available(&self) -> bool {
        self.service_available
    }

    pub fn problem(&self) -> &str {
        &self.problem
    }

    pub fn elapsed_seconds(&self) -> f64 {
        self.received_at
            .map(|t| t.elapsed().as_secs_f64())
            .unwrap_or(0.0)
    }

    pub fn is_current(&self) -> bool {
        self.service_available && self.last.is_some() && self.elapsed_seconds() <= 30.0
    }

    pub fn update(&mut self, response: IpcResponse) -> bool {
        if let Some(ref prev) = self.last
            && response.sequence > 0
            && response.service_instance == prev.service_instance
            && response.sequence <= prev.sequence
        {
            return false;
        }
        self.last = Some(response);
        self.received_at = Some(Instant::now());
        self.service_available = true;
        true
    }

    pub fn mark_unavailable(&mut self, problem: Option<&str>) {
        self.service_available = false;
        self.problem = match problem {
            Some(p) if !p.trim().is_empty() => p.to_string(),
            _ => DEFAULT_PROBLEM.to_string(),
        };
    }

    pub fn project(&self, policy_now: NaiveDateTime) -> Option<TimerValues> {
        let r = self.last.as_ref()?;
        let elapsed = self.elapsed_seconds();
        let session_max = if r.session_max_seconds > 0 {
            r.session_max_seconds as f64
        } else {
            limits::SESSION_MAX_SECS as f64
        };
        let mut session_elapsed = r.session_elapsed_seconds.clamp(0.0, session_max);
        let mut remaining = r.remaining_seconds.max(0.0);
        let mut dota_remaining = r.dota_remaining_seconds.max(0.0);
        let window_left = r
            .window_ends_local
            .map(|end| ((end - policy_now).num_milliseconds() as f64 / 1000.0).max(0.0))
            .unwrap_or(f64::INFINITY);

        let active = r.running && r.steam_running;
        let accruing = active && !r.in_cooldown && !r.outside_play_hours && !r.enforcing;
        if accruing {
            let mut played = elapsed
                .min(30.0)
                .min(remaining.min(session_max - session_elapsed));
            if let Some(window_end) = r.window_ends_local {
                let w_cap =
                    ((window_end - policy_now).num_milliseconds() as f64 / 1000.0 + elapsed).max(0.0);
                played = played.min(w_cap);
            }
            remaining = (remaining - played).max(0.0);
            session_elapsed += played;
            if r.dota_running && !r.dota_enforcing && !r.dota_forbidden {
                dota_remaining = (dota_remaining - played).max(0.0);
            }
        }

        let session_left = if active {
            session_max - session_elapsed
        } else {
            session_max
        };
        let mut playable = session_left.min(remaining).min(window_left);
        if active && r.dota_running {
            playable = playable.min(if r.dota_enforcing || r.dota_forbidden {
                0.0
            } else {
                dota_remaining
            });
        }
        if r.enforcing || r.in_cooldown || r.outside_play_hours {
            playable = 0.0;
        }

        Some(TimerValues {
            remaining,
            session_elapsed,
            session_max,
            dota_remaining,
            playable: playable.max(0.0),
            cooldown_remaining: (r.cooldown_remaining_seconds - elapsed).max(0.0),
            session_active: active,
        })
    }
}

pub fn describe_tray(state: &TimerViewState, policy_now: NaiveDateTime) -> (String, TrayLevel) {
    if !state.service_available() {
        return (state.problem().to_string(), TrayLevel::Unknown);
    }
    let Some(r) = state.last() else {
        return (
            "Steam Timer — waiting for the service".to_string(),
            TrayLevel::Unknown,
        );
    };
    if !state.is_current() {
        return (
            "Service status is stale — last known values".to_string(),
            TrayLevel::Unknown,
        );
    }

    let values = state.project(policy_now).unwrap();
    if r.outside_play_hours || !limits::is_play_allowed(policy_now) {
        return (
            format!(
                "Outside play hours — back {}",
                limits::format_clock(limits::next_play_allowed_local(policy_now))
            ),
            TrayLevel::Blocked,
        );
    }
    if r.enforcing || values.remaining <= 0.0 {
        return (
            format!("Steam blocked until {}", limits::RESET_CLOCK_TEXT),
            TrayLevel::Blocked,
        );
    }
    if r.in_cooldown {
        return (
            format!("Cooldown: {} left", format_hm_floor(values.cooldown_remaining)),
            TrayLevel::Blocked,
        );
    }

    let mut text = if values.session_active {
        format!("Playing: {} left", format_hm_floor(values.playable))
    } else {
        "Ready".to_string()
    };
    if let Some(end) = r.window_ends_local {
        text.push_str(&format!(" • until {}", limits::format_clock(end)));
    }
    if r.dota_forbidden {
        text.push_str(" • no Dota today");
    } else if r.dota_enforcing {
        text.push_str(" • Dota done");
    }

    let level = if values.playable <= 0.0 {
        TrayLevel::Blocked
    } else if values.playable <= 30.0 * 60.0 {
        TrayLevel::Low
    } else {
        TrayLevel::Normal
    };
    (text, level)
}

pub fn tomorrow_remaining(r: &IpcResponse, policy_now: NaiveDateTime) -> f64 {
    let tomorrow_base = if r.tomorrow_base_seconds > 0 {
        r.tomorrow_base_seconds as f64
    } else {
        let tomorrow = LogicalDay::parse(&r.logical_day)
            .map(|d| d + Duration::days(1))
            .unwrap_or_else(|| policy_now.date() + Duration::days(1));
        limits::daily_base_seconds_for(tomorrow.weekday()) as f64
    };
    (tomorrow_base - f64::from(r.borrow_count) * limits::BORROW_COST_SECS as f64).max(0.0)
}

pub fn format_hm_floor(total_seconds: f64) -> String {
    if !total_seconds.is_finite() {
        return "—".to_string();
    }
    let secs = total_seconds.max(0.0) as i64;
    let h = secs / 3600;
    let m = (secs % 3600) / 60;
    format!("{h}h {m:02}m")
}

pub fn format_max(total_seconds: f64) -> String {
    if !total_seconds.is_finite() {
        return "—".to_string();
    }
    let mins = (total_seconds.max(0.0) / 60.0).round() as i64;
    let h = mins / 60;
    let m = mins % 60;
    if m == 0 {
        format!("{h}h")
    } else {
        format!("{h}h {m:02}m")
    }
}

pub fn format_duration(total_seconds: f64) -> String {
    if !total_seconds.is_finite() {
        return "—".to_string();
    }
    if total_seconds <= 0.0 {
        return "0s".to_string();
    }
    if total_seconds < 60.0 {
        return format!("{}s", total_seconds.ceil() as i64);
    }
    if total_seconds < 3600.0 {
        let mut m = (total_seconds / 60.0).floor() as i64;
        let mut s = (total_seconds - (m as f64) * 60.0).ceil() as i64;
        if s == 60 {
            m += 1;
            s = 0;
        }
        return format!("{m}m {s:02}s");
    }
    format_hm_floor(total_seconds)
}

pub fn describe_seen(r: &IpcResponse) -> String {
    let mut bits = Vec::new();
    if !r.live_clients.is_empty() {
        bits.push(format!("client: {}", r.live_clients.join(", ")));
    }
    if !r.live_games.is_empty() {
        bits.push(format!("games: {}", r.live_games.join(", ")));
    }
    if !r.matched_orphans.is_empty() {
        bits.push(format!("orphans: {}", r.matched_orphans.join(", ")));
    }
    if bits.is_empty() {
        "nothing running".to_string()
    } else {
        bits.join("; ")
    }
}
