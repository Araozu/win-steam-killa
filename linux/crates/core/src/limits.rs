use chrono::{Datelike, Duration, NaiveDate, NaiveDateTime, NaiveTime, Weekday};

use crate::clock::LogicalDay;

// Non-overridable policy. To change budgets you must edit + rebuild.
pub const SESSION_MAX_SECS: i64 = 2 * 3600;
pub const COOLDOWN_ROUND_UP_SECS: i64 = 5 * 60;
pub const MAX_CARRYOVER_SECS: i64 = 30 * 60;
pub const DOTA_WEEKEND_BASE_SECS: i64 = 4 * 3600;
pub const WARN_BEFORE_SECS: i64 = 15 * 60;

pub const BORROW_COST_SECS: i64 = 10 * 60;
pub const BORROW_FIRST_EFFECTIVENESS: f64 = 0.95;
pub const BORROW_BASE_RATE: f64 = 0.07;
pub const BORROW_RATE_STEP: f64 = 0.01;
pub const BORROW_MIN_EFFECTIVENESS: f64 = 0.001;

pub const RESET_HOUR: u32 = 0;
pub const RESET_CLOCK_TEXT: &str = "00:00";

pub const DEFAULT_SOCKET_PATH: &str = "/run/steamtimer/steamtimer.sock";
pub const DEFAULT_STATE_DIR: &str = "/var/lib/steamtimer";
pub const DEFAULT_STATE_FILE: &str = "/var/lib/steamtimer/state.json";
pub const DEFAULT_WORKOUT_ARCHIVE: &str = "/var/lib/steamtimer/workouts.json";
pub const DEFAULT_WORKOUT_CONFIG: &str = "/var/lib/steamtimer/workout-connection.json";

pub const PLAY_HOURS_SUMMARY: &str = "Mon\u{2013}Fri 05:30\u{2013}06:15 + 20:15\u{2013}22:00";

pub fn weekday_window_1_start() -> NaiveTime {
    NaiveTime::from_hms_opt(5, 30, 0).unwrap()
}

pub fn weekday_window_1_end() -> NaiveTime {
    NaiveTime::from_hms_opt(6, 15, 0).unwrap()
}

pub fn weekday_window_2_start() -> NaiveTime {
    NaiveTime::from_hms_opt(20, 15, 0).unwrap()
}

pub fn weekday_window_2_end() -> NaiveTime {
    NaiveTime::from_hms_opt(22, 0, 0).unwrap()
}

/// 1-indexed press number today (1 = first borrow). Never below the floor.
pub fn effectiveness_for_borrow_number(n: i32) -> f64 {
    let n = n.max(1);
    let mut e = BORROW_FIRST_EFFECTIVENESS;
    for i in 2..=n {
        e -= BORROW_BASE_RATE + BORROW_RATE_STEP * f64::from(i - 2);
    }
    e.max(BORROW_MIN_EFFECTIVENESS)
}

pub fn borrow_gain_seconds_for_number(n: i32) -> i64 {
    let e = effectiveness_for_borrow_number(n);
    ((BORROW_COST_SECS as f64) * e).round().max(1.0) as i64
}

pub fn next_borrow_effectiveness(borrow_count_so_far: i32) -> f64 {
    effectiveness_for_borrow_number(borrow_count_so_far + 1)
}

pub fn format_effectiveness(e: f64) -> String {
    if e < 0.005 {
        "0.1%".to_string()
    } else {
        format!("{:.0}%", e * 100.0)
    }
}

/// Precise short gain: "9m 30s", "6m", "1m 48s".
pub fn format_gain(secs: i64) -> String {
    let total_secs = secs.max(0);
    let m = total_secs / 60;
    let s = total_secs % 60;
    if s == 0 {
        format!("{m}m")
    } else {
        format!("{m}m {s:02}s")
    }
}

pub fn borrow_button_text(borrow_count_so_far: i32) -> String {
    format!(
        "+10m from tomorrow ({} effective)",
        format_effectiveness(next_borrow_effectiveness(borrow_count_so_far))
    )
}

pub fn daily_base_seconds_for(dow: Weekday) -> i64 {
    match dow {
        Weekday::Sat => 150 * 60, // 2h30m
        Weekday::Sun => 240 * 60, // 4h
        _ => 90 * 60,             // Mon-Fri 1h30m
    }
}

pub fn daily_base_seconds_for_date(date: NaiveDate) -> i64 {
    daily_base_seconds_for(date.weekday())
}

pub fn is_weekend(dow: Weekday) -> bool {
    matches!(dow, Weekday::Sat | Weekday::Sun)
}

pub fn is_dota_forbidden_logical_day(logical_day: &str) -> bool {
    LogicalDay::parse(logical_day)
        .map(|d| matches!(d.weekday(), Weekday::Tue | Weekday::Thu))
        .unwrap_or(false)
}

/// Cooldown for a finished session: its length rounded up to COOLDOWN_ROUND_UP_SECS.
pub fn cooldown_seconds_for_session(session_len_secs: f64) -> f64 {
    if session_len_secs <= 0.0 {
        return 0.0;
    }
    let quantum = COOLDOWN_ROUND_UP_SECS as f64;
    (session_len_secs / quantum).ceil() * quantum
}

/// Monday (YYYY-MM-DD) of the logical week containing this logical day.
pub fn week_start_for_logical_day(logical_day: &str) -> String {
    if let Some(d) = LogicalDay::parse(logical_day) {
        let back = i64::from(d.weekday().num_days_from_monday());
        return LogicalDay::format(d - Duration::days(back));
    }
    logical_day.to_string()
}

/// Next Monday 00:00 (Dota week reset) after policy_now.
pub fn next_weekly_reset_local(policy_now: NaiveDateTime) -> NaiveDateTime {
    let logical_today = LogicalDay::date_for_policy(policy_now);
    let back = i64::from(logical_today.weekday().num_days_from_monday());
    let monday = logical_today - Duration::days(back);
    let next_monday = monday + Duration::days(7);
    let reset_time = NaiveTime::from_hms_opt(RESET_HOUR, 0, 0).unwrap();
    next_monday.and_time(reset_time)
}

/// Ceil-rounded hours+minutes format used in daemon notes.
pub fn format_hm_ceil(total_seconds: f64) -> String {
    let mins = (total_seconds.max(0.0) / 60.0).ceil() as i64;
    format!("{}h {:02}m", mins / 60, mins % 60)
}

pub fn is_play_allowed(local_now: NaiveDateTime) -> bool {
    if is_weekend(local_now.weekday()) {
        return true;
    }
    let t = local_now.time();
    (t >= weekday_window_1_start() && t < weekday_window_1_end())
        || (t >= weekday_window_2_start() && t < weekday_window_2_end())
}

pub fn current_window_end_local(local_now: NaiveDateTime) -> Option<NaiveDateTime> {
    if is_weekend(local_now.weekday()) {
        return None;
    }
    let t = local_now.time();
    let today = local_now.date();
    if t >= weekday_window_1_start() && t < weekday_window_1_end() {
        return Some(today.and_time(weekday_window_1_end()));
    }
    if t >= weekday_window_2_start() && t < weekday_window_2_end() {
        return Some(today.and_time(weekday_window_2_end()));
    }
    None
}

pub fn next_play_allowed_local(local_now: NaiveDateTime) -> NaiveDateTime {
    if is_play_allowed(local_now) {
        return local_now;
    }
    let today = local_now.date();
    let t = local_now.time();
    if t < weekday_window_1_start() {
        return today.and_time(weekday_window_1_start());
    }
    if t < weekday_window_2_start() {
        return today.and_time(weekday_window_2_start());
    }
    let tomorrow = today + Duration::days(1);
    if is_weekend(tomorrow.weekday()) {
        tomorrow.and_hms_opt(0, 0, 0).unwrap()
    } else {
        tomorrow.and_time(weekday_window_1_start())
    }
}

pub fn format_clock(local: NaiveDateTime) -> String {
    local.format("%H:%M").to_string()
}
