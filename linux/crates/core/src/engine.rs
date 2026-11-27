use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use chrono::{DateTime, Datelike, Duration, Utc, Weekday};
use tracing::{info, warn};
use uuid::Uuid;

use crate::clock::{LogicalDay, PolicyClock};
use crate::limits;
use crate::process::{DetailedSnapshot, PlayProcesses, is_ignored_play_process};
use crate::protocol::{IpcRequest, IpcResponse};
use crate::state::{CURRENT_STATE_VERSION, GameRecord, StateStore, StateStoreError, TimerState};
use crate::workout::WorkoutCreditTotals;

pub const CHECK_INTERVAL_SECS: i64 = 60;
const MAX_TICK_CREDIT_SECS: f64 = (CHECK_INTERVAL_SECS + 15) as f64;
const EPSILON_SECONDS: f64 = 1e-3;
const MAX_RECENT_REQUEST_IDS: usize = 32;

fn reached(used: f64, cap: f64) -> bool {
    used + EPSILON_SECONDS >= cap
}

fn left(cap: f64, used: f64) -> f64 {
    if cap - used <= EPSILON_SECONDS {
        0.0
    } else {
        (cap - used).max(0.0)
    }
}

pub type ClockFn = Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>;

struct EngineInner {
    state: Option<TimerState>,
    last_window_note_key: String,
    window_warn_key: String,
    sequence: i64,
    recent_mutations: VecDeque<(String, IpcResponse)>,
}

pub struct TimerEngine {
    store: StateStore,
    utc_now: ClockFn,
    processes: Arc<dyn PlayProcesses>,
    instance: String,
    inner: Mutex<EngineInner>,
}

impl TimerEngine {
    pub fn new(
        store: StateStore,
        processes: Arc<dyn PlayProcesses>,
        utc_now: Option<ClockFn>,
    ) -> Result<Self, StateStoreError> {
        let utc_now: ClockFn = utc_now.unwrap_or_else(|| Arc::new(Utc::now));
        let mut state = store.load()?;

        if let Some(ref st) = state
            && st.version != CURRENT_STATE_VERSION
        {
            info!("Discarding unsupported v{} timer state.", st.version);
            state = None;
            store.clear();
        }

        let now_utc = (utc_now)();
        let today = LogicalDay::for_utc(now_utc);
        if let Some(ref st) = state
            && st.logical_day != today
        {
            let rolled = Self::new_day_state(&today, now_utc, Some(st));
            store.save(&rolled)?;
            state = Some(rolled);
        }
        if let Some(ref mut st) = state {
            st.last_tick_utc = Some(now_utc);
        }

        Ok(Self {
            store,
            utc_now,
            processes,
            instance: Uuid::new_v4().simple().to_string(),
            inner: Mutex::new(EngineInner {
                state,
                last_window_note_key: String::new(),
                window_warn_key: String::new(),
                sequence: 0,
                recent_mutations: VecDeque::new(),
            }),
        })
    }

    fn base_seconds_for(logical_day: &str) -> i64 {
        LogicalDay::parse(logical_day)
            .map(limits::daily_base_seconds_for_date)
            .unwrap_or_else(|| limits::daily_base_seconds_for(Weekday::Mon))
    }

    fn effective_of(st: &TimerState) -> f64 {
        ((st.base_budget_seconds + st.carry_in_seconds + st.borrowed_seconds
            - st.advance_deduction_seconds) as f64)
            .max(0.0)
    }

    fn is_dota_weekend(logical_day: &str) -> bool {
        LogicalDay::parse(logical_day)
            .map(|d| limits::is_weekend(d.weekday()))
            .unwrap_or(false)
    }

    fn dota_used_of(st: &TimerState, weekend: bool) -> f64 {
        if weekend {
            st.dota_weekend_used_seconds
        } else {
            st.dota_weekday_used_seconds
        }
    }

    fn dota_carryover_of(st: &TimerState) -> f64 {
        left(
            st.dota_weekday_earned_seconds as f64,
            st.dota_weekday_used_seconds,
        )
    }

    fn effective_dota_of(st: &TimerState, weekend: bool) -> f64 {
        if weekend {
            limits::DOTA_WEEKEND_BASE_SECS as f64
                + st.dota_weekend_earned_seconds as f64
                + Self::dota_carryover_of(st)
        } else {
            st.dota_weekday_earned_seconds as f64
        }
    }

    fn tomorrow_base_for(logical_day: &str) -> i64 {
        LogicalDay::parse(logical_day)
            .map(|d| limits::daily_base_seconds_for_date(d + Duration::days(1)))
            .unwrap_or_else(|| limits::daily_base_seconds_for(Weekday::Mon))
    }

    fn adjacent_carry(old: Option<&TimerState>, today: &str) -> i64 {
        let Some(old) = old else { return 0 };
        let (Some(old_date), Some(today_date)) =
            (LogicalDay::parse(&old.logical_day), LogicalDay::parse(today))
        else {
            return 0;
        };
        if old_date != today_date - Duration::days(1) {
            return 0;
        }
        let effective = Self::effective_of(old);
        let leftover = (effective - old.used_seconds).max(0.0);
        leftover.min(limits::MAX_CARRYOVER_SECS as f64) as i64
    }

    fn new_day_state(today: &str, now_utc: DateTime<Utc>, old: Option<&TimerState>) -> TimerState {
        let week = limits::week_start_for_logical_day(today);
        let deduction = match old.and_then(|o| {
            Some((
                LogicalDay::parse(&o.logical_day)?,
                LogicalDay::parse(today)?,
                o.borrow_count,
            ))
        }) {
            Some((old_date, today_date, count)) if old_date == today_date - Duration::days(1) => {
                i64::from(count) * limits::BORROW_COST_SECS
            }
            _ => 0,
        };

        // Preserve an active cooldown across midnight rollover so late-night
        // sessions cannot skip their cooldown at 00:00.
        let active_cooldown = old
            .and_then(|o| o.cooldown_until_utc)
            .filter(|&until| until > now_utc);
        let last_session_secs = if active_cooldown.is_some() {
            old.map(|o| o.last_session_seconds).unwrap_or(0.0)
        } else {
            0.0
        };

        let mut st = TimerState {
            version: CURRENT_STATE_VERSION,
            logical_day: today.to_string(),
            base_budget_seconds: Self::base_seconds_for(today),
            carry_in_seconds: Self::adjacent_carry(old, today),
            advance_deduction_seconds: deduction,
            borrowed_seconds: 0,
            borrow_count: 0,
            last_session_seconds: last_session_secs,
            last_tick_utc: Some(now_utc),
            cooldown_until_utc: active_cooldown,
            dota_week_start: week.clone(),
            seen_games: old.map(|o| o.seen_games.clone()).unwrap_or_default(),
            game_history: old.map(|o| o.game_history.clone()).unwrap_or_default(),
            workout_last_sync_utc: old.and_then(|o| o.workout_last_sync_utc),
            ..TimerState::default()
        };

        if let Some(old) = old
            && old.dota_week_start == week
        {
            st.dota_weekday_used_seconds = old.dota_weekday_used_seconds;
            st.dota_weekend_used_seconds = old.dota_weekend_used_seconds;
            st.dota_weekday_earned_seconds = old.dota_weekday_earned_seconds;
            st.dota_weekend_earned_seconds = old.dota_weekend_earned_seconds;
            if Self::is_dota_weekend(&old.logical_day) == Self::is_dota_weekend(today) {
                st.dota_warned = old.dota_warned;
                st.dota_notified = old.dota_notified;
            }
        }
        st
    }

    fn save_state(&self, st: &TimerState) -> Result<(), StateStoreError> {
        self.store.save(st)
    }

    fn save_state_best_effort(&self, st: &TimerState) {
        if let Err(err) = self.store.save(st) {
            warn!("Failed to save timer state: {err}");
        }
    }

    /// Persist observed game executable identities (name + path) on first sight
    /// so orphan detection and kill enforcement always share the exact same path record.
    fn record_sighted_games(
        st: &mut TimerState,
        snap: &DetailedSnapshot,
        now_utc: DateTime<Utc>,
        accrue_seconds: f64,
    ) -> bool {
        let mut changed = false;
        let mut grouped: HashMap<String, (String, String)> = HashMap::new();
        for g in &snap.games {
            if g.name.is_empty() || is_ignored_play_process(&g.name) {
                continue;
            }
            let key = g.name.to_ascii_lowercase();
            let entry = grouped
                .entry(key)
                .or_insert_with(|| (g.name.clone(), String::new()));
            if entry.1.is_empty() && !g.path.is_empty() {
                entry.1 = g.path.clone();
            }
        }
        for (_key, (name, path)) in grouped {
            if let Some(rec) = st
                .game_history
                .iter_mut()
                .find(|r| r.name.eq_ignore_ascii_case(&name))
            {
                if accrue_seconds > 0.0 {
                    rec.total_seconds += accrue_seconds;
                    changed = true;
                }
                rec.last_seen_utc = Some(now_utc);
                if !path.is_empty() && rec.path != path {
                    rec.path = path;
                    changed = true;
                }
            } else {
                st.game_history.push(GameRecord {
                    name,
                    path,
                    total_seconds: accrue_seconds,
                    last_seen_utc: Some(now_utc),
                });
                changed = true;
            }
        }
        if Self::trim_seen_games(st) {
            changed = true;
        }
        Self::trim_history(st);
        changed
    }

    fn ensure_day<'a>(
        &self,
        inner: &'a mut EngineInner,
        today: &str,
        now_utc: DateTime<Utc>,
    ) -> &'a mut TimerState {
        let needs_new = inner
            .state
            .as_ref()
            .map(|s| s.logical_day != today)
            .unwrap_or(true);
        if needs_new {
            let next = Self::new_day_state(today, now_utc, inner.state.as_ref());
            self.save_state_best_effort(&next);
            info!(
                "New logical day {}, base {}s + carry {}s.",
                today, next.base_budget_seconds, next.carry_in_seconds
            );
            inner.state = Some(next);
        }
        let st = inner.state.as_mut().unwrap();
        if st.base_budget_seconds <= 0 {
            st.base_budget_seconds = Self::base_seconds_for(today);
            self.save_state_best_effort(st);
        }
        st
    }

    fn ensure_week(&self, st: &mut TimerState, today: &str) {
        let week = limits::week_start_for_logical_day(today);
        if st.dota_week_start == week {
            return;
        }
        st.dota_week_start = week;
        st.dota_weekday_used_seconds = 0.0;
        st.dota_weekend_used_seconds = 0.0;
        st.dota_weekday_earned_seconds = 0;
        st.dota_weekend_earned_seconds = 0;
        st.dota_warned = false;
        st.dota_notified = false;
        self.save_state_best_effort(st);
    }

    pub fn current_week_and_day(&self) -> (String, String) {
        let now_utc = (self.utc_now)();
        let today = LogicalDay::for_utc(now_utc);
        let week = limits::week_start_for_logical_day(&today);
        (week, today)
    }

    pub fn apply_workout_earned(
        &self,
        week_start_logical_day: &str,
        earned: WorkoutCreditTotals,
        now_utc: DateTime<Utc>,
    ) -> Result<(), StateStoreError> {
        let mut inner = self.inner.lock().unwrap();
        let today = LogicalDay::for_utc(now_utc);
        let st = self.ensure_day(&mut inner, &today, now_utc);
        self.ensure_week(st, &today);
        if st.dota_week_start != week_start_logical_day {
            return Ok(());
        }

        let mut candidate = st.clone();
        let weekend = Self::is_dota_weekend(&today);
        let before = Self::effective_dota_of(&candidate, weekend);
        candidate.dota_weekday_earned_seconds = earned.weekday_seconds.max(0);
        candidate.dota_weekend_earned_seconds = earned.weekend_seconds.max(0);
        if Self::effective_dota_of(&candidate, weekend) > before {
            candidate.dota_warned = false;
            candidate.dota_notified = false;
        }
        candidate.workout_last_sync_utc = Some(now_utc);
        // Persist BEFORE committing in-memory so failed writes never claim success.
        self.save_state(&candidate)?;
        *st = candidate;
        Ok(())
    }

    pub fn get_workout_credit_status(&self) -> (Option<DateTime<Utc>>, i64) {
        let inner = self.inner.lock().unwrap();
        let Some(ref st) = inner.state else {
            return (None, 0);
        };
        let today = LogicalDay::for_utc((self.utc_now)());
        let earned = if st.dota_week_start == limits::week_start_for_logical_day(&today) {
            st.dota_weekday_earned_seconds + st.dota_weekend_earned_seconds
        } else {
            0
        };
        (st.workout_last_sync_utc, earned)
    }

    pub fn handle(&self, req: Option<&IpcRequest>) -> IpcResponse {
        self.handle_with_deadline(req, None)
    }

    pub fn handle_with_deadline(
        &self,
        req: Option<&IpcRequest>,
        deadline: Option<Instant>,
    ) -> IpcResponse {
        let mut inner = self.inner.lock().unwrap();
        let now_utc = (self.utc_now)();
        let now_local = PolicyClock::from_utc(now_utc);
        let today = LogicalDay::for_policy(now_local);

        if let Some(dl) = deadline
            && Instant::now() >= dl
        {
            inner.sequence += 1;
            return IpcResponse {
                ok: false,
                error: "Request timed out before execution.".into(),
                service_instance: self.instance.clone(),
                sequence: inner.sequence,
                server_now_utc: Some(now_utc),
                next_reset_local: Some(LogicalDay::next_reset(now_local)),
                logical_day: today,
                ..IpcResponse::default()
            };
        }

        // Idempotent replay for retried mutating commands with a request_id.
        if let Some(req_id) = req
            .and_then(|r| r.request_id.as_deref())
            .filter(|id| !id.trim().is_empty())
            && let Some((_, cached)) = inner
                .recent_mutations
                .iter()
                .find(|(id, _)| id == req_id)
        {
            return cached.clone();
        }

        inner.sequence += 1;
        let mut resp = IpcResponse {
            service_instance: self.instance.clone(),
            sequence: inner.sequence,
            server_now_utc: Some(now_utc),
            next_reset_local: Some(LogicalDay::next_reset(now_local)),
            logical_day: today.clone(),
            ..IpcResponse::default()
        };

        let Some(req) = req else {
            return Self::fail("empty request", resp);
        };
        let cmd = req.cmd.trim();
        if cmd.is_empty() {
            return Self::fail("empty request", resp);
        }

        let is_mutating = matches!(
            cmd,
            "borrow" | "advance" | "kill" | "stop" | "forceclose" | "force-close"
        );
        let req_id_owned = req
            .request_id
            .as_deref()
            .filter(|id| !id.trim().is_empty())
            .map(str::to_string);

        let st = self.ensure_day(&mut inner, &today, now_utc);
        self.ensure_week(st, &today);

        let result = match cmd {
            "status" => {
                self.fill(&mut resp, st, now_utc, true, None, None, None);
                resp
            }
            "recheck" | "probe" => {
                self.fill(&mut resp, st, now_utc, true, None, None, None);
                resp.action_note = Self::build_recheck_note(&resp);
                resp
            }
            "kill" | "stop" | "forceclose" | "force-close" => {
                self.force_close(st, now_utc, resp)
            }
            "start" => {
                self.fill(&mut resp, st, now_utc, true, None, None, None);
                Self::fail(
                    "Manual sessions were removed — tracking is automatic now. Just launch Steam.",
                    resp,
                )
            }
            "borrow" | "advance" => self.borrow(st, now_utc, resp),
            _ => Self::fail("unknown cmd", resp),
        };

        if is_mutating && let Some(req_id) = req_id_owned {
            if inner.recent_mutations.len() >= MAX_RECENT_REQUEST_IDS {
                inner.recent_mutations.pop_front();
            }
            inner.recent_mutations.push_back((req_id, result.clone()));
        }
        result
    }

    fn fail(err: impl Into<String>, mut resp: IpcResponse) -> IpcResponse {
        resp.ok = false;
        resp.error = err.into();
        resp
    }

    fn build_recheck_note(r: &IpcResponse) -> String {
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
        let seen = if bits.is_empty() {
            "nothing running".to_string()
        } else {
            bits.join("; ")
        };
        let timer = if r.outside_play_hours {
            let back = r
                .next_play_allowed_local
                .map(limits::format_clock)
                .unwrap_or_else(|| "—".into());
            format!("outside play hours (blocked until {back}, accrues nothing)")
        } else if r.running {
            "session OPEN (accruing)".to_string()
        } else if r.in_cooldown {
            "in cooldown (blocked, accrues nothing)".to_string()
        } else {
            "no session (stopped)".to_string()
        };
        let mut dota = if r.dota_running {
            ", Dota 2 running".to_string()
        } else {
            String::new()
        };
        if r.dota_forbidden {
            dota.push_str(" (Dota blocked Tue / Thu)");
        }
        format!("Detector sees {seen}. Timer: {timer}{dota}.")
    }

    fn borrow(
        &self,
        st: &mut TimerState,
        now_utc: DateTime<Utc>,
        mut resp: IpcResponse,
    ) -> IpcResponse {
        if LogicalDay::parse(&st.logical_day)
            .map(|d| d.weekday() == Weekday::Sun)
            .unwrap_or(false)
        {
            self.fill(&mut resp, st, now_utc, true, None, None, None);
            return Self::fail("Borrow is unavailable on Sundays — enjoy the 4h budget.", resp);
        }

        let cost = limits::BORROW_COST_SECS;
        let next_num = st.borrow_count + 1;
        let eff = limits::effectiveness_for_borrow_number(next_num);
        let gain = limits::borrow_gain_seconds_for_number(next_num);
        let tomorrow_base = Self::tomorrow_base_for(&st.logical_day);
        let debt_after = i64::from(next_num) * cost;
        if debt_after > tomorrow_base {
            self.fill(&mut resp, st, now_utc, true, None, None, None);
            let rem_borrowable = (tomorrow_base - i64::from(st.borrow_count) * cost).max(0);
            return Self::fail(
                format!(
                    "Tomorrow only has {} base ({} still borrowable). Another +{} today would push tomorrow below zero.",
                    limits::format_hm_ceil(tomorrow_base as f64),
                    limits::format_hm_ceil(rem_borrowable as f64),
                    limits::format_gain(gain),
                ),
                resp,
            );
        }

        let mut candidate = st.clone();
        candidate.borrow_count += 1;
        candidate.borrowed_seconds += gain;
        candidate.warned_15m = false;
        candidate.kill_notified = false;
        if let Err(err) = self.save_state(&candidate) {
            warn!("Borrow state persist failed: {err}");
            self.fill(&mut resp, st, now_utc, true, None, None, None);
            return Self::fail(
                "Could not save borrow state to disk; no time was borrowed.",
                resp,
            );
        }
        *st = candidate;

        info!(
            "Borrow #{}: +{}s today ({} effective), -{}s tomorrow ({}).",
            st.borrow_count, gain, eff, cost, st.logical_day
        );
        self.fill(&mut resp, st, now_utc, true, None, None, None);
        resp.action_note = format!(
            "Borrowed 10m of tomorrow for +{} today ({} effective, use #{}). Tomorrow loses 10m. Cooldowns still apply.",
            limits::format_gain(gain),
            limits::format_effectiveness(eff),
            st.borrow_count
        );
        resp
    }

    fn force_close(
        &self,
        st: &mut TimerState,
        now_utc: DateTime<Utc>,
        mut resp: IpcResponse,
    ) -> IpcResponse {
        let before = self.processes.snapshot(&mut st.seen_games, &st.game_history);
        Self::record_sighted_games(st, &before, now_utc, 0.0);
        let n = self.processes.kill_play_set(&st.seen_games, &st.game_history);
        Self::trim_seen_games(st);

        let mut note = if st.running {
            let len_secs = st.session_used_seconds;
            st.running = false;
            if len_secs > 0.0 {
                let cd_secs = limits::cooldown_seconds_for_session(len_secs);
                st.cooldown_until_utc = Some(now_utc + Duration::seconds(cd_secs as i64));
                st.last_session_seconds = len_secs;
                st.session_used_seconds = 0.0;
                st.session_warned = false;
                let msg = if n > 0 {
                    format!(
                        "Closed {n} Steam process(es). Timer stopped — cooldown {} (this session ran {}).",
                        limits::format_hm_ceil(cd_secs),
                        limits::format_hm_ceil(len_secs),
                    )
                } else {
                    format!(
                        "Timer stopped (session ran {}) but no processes were found to close — cooldown {}.",
                        limits::format_hm_ceil(len_secs),
                        limits::format_hm_ceil(cd_secs),
                    )
                };
                info!(
                    "Manual force-close: killed {n}, session {}s ended, cooldown {}s.",
                    len_secs as i64, cd_secs as i64
                );
                msg
            } else {
                st.session_used_seconds = 0.0;
                st.session_warned = false;
                info!("Manual force-close: killed {n}, no session time.");
                if n > 0 {
                    format!(
                        "Closed {n} Steam process(es). No session time had accrued, so no cooldown."
                    )
                } else {
                    "No session time had accrued and nothing was running — timer already stopped."
                        .to_string()
                }
            }
        } else {
            info!("Manual force-close: killed {n}, no session open.");
            if n > 0 {
                format!(
                    "Closed {n} Steam process(es). No session was open — timer already stopped."
                )
            } else {
                "Nothing to close — Steam/games not running, timer already stopped.".to_string()
            }
        };
        if let Err(err) = self.save_state(st) {
            warn!("Force-close state persist failed: {err}");
            note.push_str(" Warning: state save failed.");
        }

        let after = self.processes.snapshot(&mut st.seen_games, &st.game_history);
        if Self::record_sighted_games(st, &after, now_utc, 0.0) {
            self.save_state_best_effort(st);
        }
        if after.playing {
            note.push_str(
                " Warning: something is still alive — hit force-close again or check the list above.",
            );
        }

        self.fill(
            &mut resp,
            st,
            now_utc,
            false,
            Some(after.playing),
            Some(after.dota_running),
            Some(&after),
        );
        resp.killed_processes = n;
        resp.action_note = note;
        resp
    }

    #[allow(clippy::too_many_arguments)]
    fn fill(
        &self,
        r: &mut IpcResponse,
        st: &mut TimerState,
        now_utc: DateTime<Utc>,
        live_check: bool,
        steam_cache: Option<bool>,
        dota_cache: Option<bool>,
        detail: Option<&DetailedSnapshot>,
    ) {
        let owned_snap;
        let snap = if live_check && steam_cache.is_none() && dota_cache.is_none() && detail.is_none()
        {
            let before = st.seen_games.len();
            owned_snap = self.processes.snapshot(&mut st.seen_games, &st.game_history);
            let history_changed = Self::record_sighted_games(st, &owned_snap, now_utc, 0.0);
            if st.seen_games.len() != before || history_changed {
                self.save_state_best_effort(st);
            }
            Some(&owned_snap)
        } else {
            detail
        };

        let (playing, dota_running) = if let Some(s) = snap {
            (s.playing, s.dota_running)
        } else {
            (steam_cache.unwrap_or(st.running), dota_cache.unwrap_or(false))
        };

        let effective = Self::effective_of(st);
        let remaining = left(effective, st.used_seconds);
        let cd_rem = st
            .cooldown_until_utc
            .map(|until| (until - now_utc).num_milliseconds() as f64 / 1000.0)
            .unwrap_or(0.0)
            .max(0.0);
        let in_cooldown = cd_rem > 0.0;
        let session_max = limits::SESSION_MAX_SECS as f64;
        let sess_elapsed = st.session_used_seconds;
        let sess_rem = left(session_max, st.session_used_seconds);
        let weekend = Self::is_dota_weekend(&st.logical_day);
        let dota_budget = Self::effective_dota_of(st, weekend);
        let dota_used = Self::dota_used_of(st, weekend);
        let dota_rem = left(dota_budget, dota_used);
        let dota_forbidden = limits::is_dota_forbidden_logical_day(&st.logical_day);
        let now_local = PolicyClock::from_utc(now_utc);
        let play_allowed = limits::is_play_allowed(now_local);

        r.outside_play_hours = !play_allowed;
        r.next_play_allowed_local = Some(limits::next_play_allowed_local(now_local));
        r.window_ends_local = limits::current_window_end_local(now_local);
        r.ok = true;
        r.logical_day = st.logical_day.clone();
        r.start_utc = st.session_start_utc;
        r.enforcing = remaining <= 0.0;
        r.budget_seconds = effective as i64;
        r.base_budget_seconds = st.base_budget_seconds;
        r.carry_in_seconds = st.carry_in_seconds;
        r.borrowed_seconds = st.borrowed_seconds;
        r.borrow_count = st.borrow_count;
        r.advance_deduction_seconds = st.advance_deduction_seconds;
        r.tomorrow_base_seconds = Self::tomorrow_base_for(&st.logical_day);
        r.next_borrow_effectiveness = limits::next_borrow_effectiveness(st.borrow_count);
        r.next_borrow_gain_seconds = limits::borrow_gain_seconds_for_number(st.borrow_count + 1);
        r.used_seconds = st.used_seconds;
        r.remaining_seconds = remaining;
        r.running = st.running;
        r.steam_running = playing;
        r.session_elapsed_seconds = sess_elapsed;
        r.session_remaining_seconds = sess_rem;
        r.last_session_seconds = st.last_session_seconds;
        r.in_cooldown = in_cooldown;
        r.cooldown_remaining_seconds = cd_rem;
        let mut cd_total = limits::cooldown_seconds_for_session(st.last_session_seconds);
        if cd_total <= 0.0 || cd_rem > cd_total {
            cd_total = cd_rem;
        }
        r.cooldown_total_seconds = cd_total;
        r.cooldown_until_utc = st.cooldown_until_utc;
        r.session_max_seconds = limits::SESSION_MAX_SECS;
        r.dota_week_start = st.dota_week_start.clone();
        r.dota_budget_seconds = dota_budget;
        r.dota_used_seconds = dota_used;
        r.dota_remaining_seconds = dota_rem;
        r.dota_running = dota_running;
        r.dota_enforcing = dota_rem <= 0.0 || dota_forbidden;
        r.dota_forbidden = dota_forbidden;
        r.dota_resets_local = Some(limits::next_weekly_reset_local(now_local));
        r.dota_weekend_budget = weekend;
        r.dota_carryover_seconds = if weekend {
            Self::dota_carryover_of(st)
        } else {
            0.0
        };
        r.workout_dota_earned_seconds = if weekend {
            st.dota_weekend_earned_seconds
        } else {
            st.dota_weekday_earned_seconds
        };
        r.workout_last_sync_utc = st.workout_last_sync_utc;
        r.live_clients = snap
            .map(|s| s.clients.iter().take(10).cloned().collect())
            .unwrap_or_default();
        r.live_games = snap
            .map(|s| {
                s.games
                    .iter()
                    .take(10)
                    .map(|g| format!("{} ({})", g.name, g.pid))
                    .collect()
            })
            .unwrap_or_default();
        r.matched_orphans = snap
            .map(|s| s.matched_orphans.iter().take(10).cloned().collect())
            .unwrap_or_default();
        r.remembered_games = st
            .seen_games
            .iter()
            .filter(|name| !is_ignored_play_process(name))
            .take(20)
            .cloned()
            .collect();
    }

    fn trim_seen_games(st: &mut TimerState) -> bool {
        if st.seen_games.len() > 50 {
            let excess = st.seen_games.len() - 50;
            st.seen_games.drain(0..excess);
            return true;
        }
        false
    }

    fn trim_history(st: &mut TimerState) {
        if st.game_history.len() > 50 {
            st.game_history
                .sort_by_key(|a| std::cmp::Reverse(a.last_seen_utc));
            st.game_history.truncate(50);
        }
    }

    fn end_session(st: &mut TimerState, now_utc: DateTime<Utc>) -> f64 {
        if !st.running {
            return 0.0;
        }
        let cd_secs = limits::cooldown_seconds_for_session(st.session_used_seconds);
        st.running = false;
        st.last_session_seconds = st.session_used_seconds;
        st.session_used_seconds = 0.0;
        st.session_warned = false;
        if cd_secs > 0.0 {
            st.cooldown_until_utc = Some(now_utc + Duration::seconds(cd_secs as i64));
        }
        cd_secs
    }

    fn until_local(utc: DateTime<Utc>) -> String {
        limits::format_clock(PolicyClock::from_utc(utc))
    }

    /// Called at startup and once per check interval (every minute).
    pub fn tick(&self) -> Vec<(String, String)> {
        let mut notes = Vec::new();
        let mut inner = self.inner.lock().unwrap();

        let now_utc = (self.utc_now)();
        let now_local = PolicyClock::from_utc(now_utc);
        let today = LogicalDay::for_policy(now_local);
        let session_max = limits::SESSION_MAX_SECS as f64;

        let mut last_window_note_key = inner.last_window_note_key.clone();
        let mut window_warn_key = inner.window_warn_key.clone();

        let st = self.ensure_day(&mut inner, &today, now_utc);
        self.ensure_week(st, &today);

        let snap = self.processes.snapshot(&mut st.seen_games, &st.game_history);
        // Record sighted game paths immediately on every tick (including blocked
        // windows) so orphan detection and kill enforcement always have paths.
        Self::record_sighted_games(st, &snap, now_utc, 0.0);
        let playing = snap.playing;
        let dota_running = snap.dota_running;

        let mut gap_secs = st
            .last_tick_utc
            .map(|prev| (now_utc - prev).num_milliseconds() as f64 / 1000.0)
            .unwrap_or(0.0)
            .clamp(0.0, MAX_TICK_CREDIT_SECS);
        st.last_tick_utc = Some(now_utc);

        let dota_weekend = limits::is_weekend(now_local.weekday());
        let play_allowed = limits::is_play_allowed(now_local);
        let next_allowed_local = limits::next_play_allowed_local(now_local);

        // Cooldown expiry transition: announce once, then continue through
        // remaining enforcement checks so exhausted daily/Dota limits still fire.
        if let Some(cd_until) = st.cooldown_until_utc
            && now_utc >= cd_until
        {
            st.cooldown_until_utc = None;
            st.session_used_seconds = 0.0;
            st.session_warned = false;
            self.save_state_best_effort(st);
            info!("Cooldown over, new session allowed.");
            if play_allowed {
                let d_left = left(Self::effective_of(st), st.used_seconds);
                notes.push((
                    "Cooldown over".into(),
                    format!(
                        "You can play again. {} left today.",
                        limits::format_hm_ceil(d_left)
                    ),
                ));
            }
        }

        let in_cooldown = st
            .cooldown_until_utc
            .map(|u| u > now_utc)
            .unwrap_or(false);

        // Weekday play windows.
        if !play_allowed {
            let ended_cd = Self::end_session(st, now_utc);
            let block_key = format!("w:{}", next_allowed_local.format("%Y-%m-%d %H:%M"));
            let first_in_block = block_key != last_window_note_key;
            if playing || dota_running {
                let n = self.processes.kill_play_set(&st.seen_games, &st.game_history);
                info!("Outside play hours, blocked launch killed {n}.");
                self.save_state_best_effort(st);
                if first_in_block {
                    last_window_note_key = block_key;
                    let back = limits::format_clock(next_allowed_local);
                    let body = if n > 0 {
                        format!(
                            "Steam blocked ({}). Back at {back}. Closed {n} process(es).",
                            limits::PLAY_HOURS_SUMMARY
                        )
                    } else {
                        format!(
                            "Steam blocked ({}). Back at {back}.",
                            limits::PLAY_HOURS_SUMMARY
                        )
                    };
                    notes.push(("Outside play hours".into(), body));
                }
            } else {
                self.save_state_best_effort(st);
                if ended_cd > 0.0 && first_in_block {
                    last_window_note_key = block_key;
                    notes.push((
                        "Play window ended".into(),
                        format!(
                            "Steam blocked until {} ({}).",
                            limits::format_clock(next_allowed_local),
                            limits::PLAY_HOURS_SUMMARY
                        ),
                    ));
                }
            }
            inner.last_window_note_key = last_window_note_key;
            inner.window_warn_key = window_warn_key;
            return notes;
        }

        // Tue / Thu: Dota 2 forbidden.
        let dota_forbidden = limits::is_dota_forbidden_logical_day(&today);
        if dota_forbidden && dota_running {
            let n = self.processes.kill_dota();
            info!("Dota forbidden day, killed {n} Dota process(es).");
            if !st.dota_forbidden_notified {
                st.dota_forbidden_notified = true;
                self.save_state_best_effort(st);
                notes.push((
                    "Dota 2 forbidden on Tue / Thu".into(),
                    "No Dota today — back tomorrow. Steam + other games continue on the daily budget."
                        .into(),
                ));
            } else {
                self.save_state_best_effort(st);
            }
        }

        let effective = Self::effective_of(st);

        // Budget already exhausted.
        if reached(st.used_seconds, effective) {
            Self::end_session(st, now_utc);
            self.save_state_best_effort(st);
            let mut body =
                "Daily budget used. Steam is already closed. Back after 00:00.".to_string();
            if playing {
                let n = self.processes.kill_play_set(&st.seen_games, &st.game_history);
                info!("Budget exhausted, enforcing kill: {n}");
                body = if n > 0 {
                    format!("Daily budget used. Closed {n} Steam process(es). Back after 00:00.")
                } else {
                    "Daily budget used. Blocked until 00:00.".to_string()
                };
            }
            if !st.kill_notified {
                st.kill_notified = true;
                self.save_state_best_effort(st);
                notes.push(("Steam time is up".into(), body));
            }
            inner.last_window_note_key = last_window_note_key;
            inner.window_warn_key = window_warn_key;
            return notes;
        }

        // Cooldown: block play, accrue nothing.
        if in_cooldown {
            let cd_until = st.cooldown_until_utc.unwrap();
            let cd_rem = ((cd_until - now_utc).num_milliseconds() as f64 / 1000.0).max(0.0);
            if playing {
                let n = self.processes.kill_play_set(&st.seen_games, &st.game_history);
                st.running = false;
                st.session_used_seconds = 0.0;
                st.session_warned = false;
                self.save_state_best_effort(st);
                info!("Cooldown active, blocked launch killed {n}.");
                let until_txt = Self::until_local(cd_until);
                let body = if n > 0 {
                    format!(
                        "Steam blocked for {} more (until {until_txt}). Closed {n} process(es).",
                        limits::format_hm_ceil(cd_rem)
                    )
                } else {
                    format!(
                        "Steam blocked for {} more (until {until_txt}).",
                        limits::format_hm_ceil(cd_rem)
                    )
                };
                notes.push(("Cooldown active".into(), body));
            } else {
                if st.running {
                    st.running = false;
                    st.session_used_seconds = 0.0;
                    st.session_warned = false;
                }
                self.save_state_best_effort(st);
            }
            inner.last_window_note_key = last_window_note_key;
            inner.window_warn_key = window_warn_key;
            return notes;
        }

        if playing {
            if !st.running {
                st.running = true;
                st.session_start_utc = Some(now_utc);
                st.session_used_seconds = 0.0;
                st.session_warned = false;
                gap_secs = 0.0;
                info!("Auto session started (steam detected).");
            }
            let daily_l = left(effective, st.used_seconds);
            let sess_l = left(session_max, st.session_used_seconds);
            gap_secs = gap_secs.min(daily_l.min(sess_l));

            st.used_seconds = (st.used_seconds + gap_secs).min(effective);
            st.session_used_seconds += gap_secs;
            if left(effective, st.used_seconds) <= 0.0 {
                st.used_seconds = effective;
            }
            if left(session_max, st.session_used_seconds) <= 0.0 {
                st.session_used_seconds = session_max;
            }

            if dota_running && !dota_forbidden {
                let eff_dota = Self::effective_dota_of(st, dota_weekend);
                let d_left = left(eff_dota, Self::dota_used_of(st, dota_weekend));
                let mut used = Self::dota_used_of(st, dota_weekend) + gap_secs.min(d_left);
                if used < eff_dota && left(eff_dota, used) <= 0.0 {
                    used = eff_dota;
                }
                if dota_weekend {
                    st.dota_weekend_used_seconds = used;
                } else {
                    st.dota_weekday_used_seconds = used;
                }
            }

            // Accrue per-game playtime.
            Self::record_sighted_games(st, &snap, now_utc, gap_secs);

            let rem = left(effective, st.used_seconds);
            let sess_rem = left(session_max, st.session_used_seconds);
            let eff_dota = Self::effective_dota_of(st, dota_weekend);
            let d_rem = left(eff_dota, Self::dota_used_of(st, dota_weekend));
            let warn_before = limits::WARN_BEFORE_SECS as f64;

            if !st.warned_15m && rem <= warn_before && rem > 0.0 {
                st.warned_15m = true;
                notes.push((
                    "Steam closes in 15 minutes".into(),
                    format!(
                        "Daily budget low. {} left, back after 00:00. Save your game.",
                        limits::format_hm_ceil(rem)
                    ),
                ));
                info!("15m budget warning sent.");
            }

            if !st.session_warned && sess_rem <= warn_before && sess_rem > 0.0 && sess_rem < rem {
                st.session_warned = true;
                notes.push((
                    "Session ends soon".into(),
                    format!(
                        "This session ends in {}, then a cooldown. Close Steam now to keep the break short.",
                        limits::format_hm_ceil(sess_rem)
                    ),
                ));
            }

            if let Some(window_end) = limits::current_window_end_local(now_local) {
                let window_rem = (window_end - now_local).num_milliseconds() as f64 / 1000.0;
                let w_key = window_end.format("%Y-%m-%d %H:%M").to_string();
                if w_key != window_warn_key
                    && window_rem <= warn_before
                    && window_rem > 0.0
                    && window_rem < rem
                    && window_rem < sess_rem
                {
                    window_warn_key = w_key;
                    notes.push((
                        "Play window closes soon".into(),
                        format!(
                            "Steam closes at {} when play hours end ({} left). Save your game.",
                            limits::format_clock(window_end),
                            limits::format_hm_ceil(window_rem)
                        ),
                    ));
                }
            }

            if !st.dota_warned && !dota_forbidden && dota_running && d_rem <= warn_before && d_rem > 0.0
            {
                st.dota_warned = true;
                notes.push((
                    "Dota 2 closes in 15 minutes".into(),
                    format!(
                        "Dota budget low. {} left. Other games are unaffected.",
                        limits::format_hm_ceil(d_rem)
                    ),
                ));
                info!("15m Dota warning sent.");
            }
            self.save_state_best_effort(st);

            // Budget hit on this tick.
            if reached(st.used_seconds, effective) {
                let n = self.processes.kill_play_set(&st.seen_games, &st.game_history);
                st.kill_notified = true;
                Self::end_session(st, now_utc);
                self.save_state_best_effort(st);
                info!("Budget hit mid-session, killed {n}.");
                let body = if n > 0 {
                    format!("Daily budget used. Closed {n} Steam process(es). Back after 00:00.")
                } else {
                    "Daily budget used. Blocked until 00:00.".to_string()
                };
                notes.push(("Steam time is up".into(), body));
                inner.last_window_note_key = last_window_note_key;
                inner.window_warn_key = window_warn_key;
                return notes;
            }

            // Dota budget hit: kill only Dota.
            if !dota_forbidden && reached(Self::dota_used_of(st, dota_weekend), eff_dota) {
                let first = !st.dota_notified;
                if dota_running {
                    let n = self.processes.kill_dota();
                    info!("Dota budget hit, killed {n} Dota process(es).");
                    if first {
                        st.dota_notified = true;
                        self.save_state_best_effort(st);
                        let body = if n > 0 {
                            format!(
                                "Dota budget used. Closed {n} Dota process(es). Steam + other games continue on the daily budget."
                            )
                        } else {
                            "Dota budget used. Other games continue.".to_string()
                        };
                        notes.push(("Dota 2 time is up".into(), body));
                    } else {
                        self.save_state_best_effort(st);
                    }
                } else if first {
                    st.dota_notified = true;
                    self.save_state_best_effort(st);
                    notes.push((
                        "Dota 2 time is up".into(),
                        "Dota budget used. Other games continue on the daily budget.".into(),
                    ));
                } else {
                    self.save_state_best_effort(st);
                }
            }

            // Session max reached.
            if reached(st.session_used_seconds, session_max) {
                let n = self.processes.kill_play_set(&st.seen_games, &st.game_history);
                let cd = Self::end_session(st, now_utc);
                let until_txt = st
                    .cooldown_until_utc
                    .map(Self::until_local)
                    .unwrap_or_else(|| "—".into());
                info!(
                    "Session max reached, killed {n}. Cooldown until {until_txt}. Budget left: {rem}s."
                );
                self.save_state_best_effort(st);
                let body = if n > 0 {
                    format!(
                        "Closed {n} Steam process(es). {} of today's budget left — next session after {until_txt} ({} cooldown).",
                        limits::format_hm_ceil(rem),
                        limits::format_hm_ceil(cd)
                    )
                } else {
                    format!(
                        "Session done ({} max). {} left — next session after {until_txt}.",
                        limits::format_hm_ceil(session_max),
                        limits::format_hm_ceil(rem)
                    )
                };
                notes.push(("Session limit reached".into(), body));
                inner.last_window_note_key = last_window_note_key;
                inner.window_warn_key = window_warn_key;
                return notes;
            }
        } else if st.running {
            let had_play = st.session_used_seconds > 0.0;
            let cd = Self::end_session(st, now_utc);
            if had_play {
                self.save_state_best_effort(st);
                let until_txt = st
                    .cooldown_until_utc
                    .map(Self::until_local)
                    .unwrap_or_else(|| "—".into());
                let rem = left(effective, st.used_seconds);
                info!(
                    "Session ended by close ({}s), cooldown until {until_txt}. Used {}s today.",
                    st.last_session_seconds as i64, st.used_seconds as i64
                );
                notes.push((
                    "Session ended — cooldown started".into(),
                    format!(
                        "{} of today's budget left. Next session after {until_txt} ({} cooldown).",
                        limits::format_hm_ceil(rem),
                        limits::format_hm_ceil(cd)
                    ),
                ));
            } else {
                self.save_state_best_effort(st);
                info!(
                    "Steam closed, session paused. Used {}s today.",
                    st.used_seconds as i64
                );
            }
        } else {
            self.save_state_best_effort(st);
        }

        inner.last_window_note_key = last_window_note_key;
        inner.window_warn_key = window_warn_key;
        notes
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use chrono::NaiveDate;
    use tempfile::TempDir;

    use super::*;

    struct FakeProcesses {
        playing: Mutex<bool>,
        dota: Mutex<bool>,
        kill_play_calls: AtomicUsize,
        kill_dota_calls: AtomicUsize,
    }

    impl FakeProcesses {
        fn new() -> Self {
            Self {
                playing: Mutex::new(false),
                dota: Mutex::new(false),
                kill_play_calls: AtomicUsize::new(0),
                kill_dota_calls: AtomicUsize::new(0),
            }
        }

        fn set(&self, playing: bool, dota: bool) {
            *self.playing.lock().unwrap() = playing;
            *self.dota.lock().unwrap() = dota;
        }
    }

    impl PlayProcesses for FakeProcesses {
        fn snapshot(&self, _seen_games: &mut Vec<String>, _history: &[GameRecord]) -> DetailedSnapshot {
            let playing = *self.playing.lock().unwrap();
            let dota = *self.dota.lock().unwrap();
            DetailedSnapshot {
                playing: playing || dota,
                dota_running: dota,
                clients: if playing { vec!["steam (100)".into()] } else { vec![] },
                games: vec![],
                matched_orphans: vec![],
            }
        }

        fn kill_play_set(&self, _seen_games: &[String], _history: &[GameRecord]) -> usize {
            self.kill_play_calls.fetch_add(1, Ordering::SeqCst);
            *self.playing.lock().unwrap() = false;
            *self.dota.lock().unwrap() = false;
            2
        }

        fn kill_dota(&self) -> usize {
            self.kill_dota_calls.fetch_add(1, Ordering::SeqCst);
            *self.dota.lock().unwrap() = false;
            1
        }
    }

    fn policy_utc(y: i32, m: u32, d: u32, hh: u32, mm: u32, ss: u32) -> DateTime<Utc> {
        let dt = NaiveDate::from_ymd_opt(y, m, d)
            .unwrap()
            .and_hms_opt(hh, mm, ss)
            .unwrap();
        PolicyClock::to_utc(dt)
    }

    fn make_engine(
        start: DateTime<Utc>,
    ) -> (TempDir, Arc<Mutex<DateTime<Utc>>>, Arc<FakeProcesses>, TimerEngine) {
        let dir = TempDir::new().unwrap();
        let clock = Arc::new(Mutex::new(start));
        let clock_clone = clock.clone();
        let procs = Arc::new(FakeProcesses::new());
        let engine = TimerEngine::new(
            StateStore::new(Some(dir.path().join("state.json"))),
            procs.clone(),
            Some(Arc::new(move || *clock_clone.lock().unwrap())),
        )
        .unwrap();
        (dir, clock, procs, engine)
    }

    #[test]
    fn weekday_outside_play_hours_blocks_and_kills() {
        let (_dir, _clock, procs, engine) = make_engine(policy_utc(2026, 11, 23, 10, 0, 0));
        procs.set(true, false);
        let notes = engine.tick();
        assert_eq!(procs.kill_play_calls.load(Ordering::SeqCst), 1);
        assert!(notes.iter().any(|(t, _)| t == "Outside play hours"));
        let status = engine.handle(Some(&IpcRequest {
            cmd: "status".into(),
            ..Default::default()
        }));
        assert!(status.outside_play_hours);
        assert_eq!(status.used_seconds, 0.0);
    }

    #[test]
    fn weekday_play_accrues_and_triggers_cooldown_on_close() {
        let (_dir, clock, procs, engine) = make_engine(policy_utc(2026, 11, 23, 20, 15, 0));
        procs.set(true, false);
        engine.tick();
        for _ in 0..12 {
            *clock.lock().unwrap() += Duration::seconds(60);
            engine.tick();
        }
        procs.set(false, false);
        *clock.lock().unwrap() += Duration::seconds(60);
        let notes = engine.tick();
        assert!(notes.iter().any(|(t, _)| t.contains("cooldown")));
        let st = engine.handle(Some(&IpcRequest {
            cmd: "status".into(),
            ..Default::default()
        }));
        assert!(st.in_cooldown);
        assert_eq!(st.cooldown_total_seconds, 900.0);
        assert_eq!(st.used_seconds, 720.0);
    }

    #[test]
    fn cooldown_persists_across_midnight_rollover_and_enforces_on_expiry() {
        // Saturday 23:20 UTC-05 -> play 30m -> close at 23:50 -> 30m cooldown until Sunday 00:20
        let (_dir, clock, procs, engine) = make_engine(policy_utc(2026, 11, 28, 23, 20, 0));
        procs.set(true, false);
        engine.tick();
        for _ in 0..30 {
            *clock.lock().unwrap() += Duration::seconds(60);
            engine.tick();
        }
        procs.set(false, false);
        *clock.lock().unwrap() += Duration::seconds(60); // 23:51
        engine.tick();

        // Roll past midnight into Sunday 00:05: cooldown MUST still be active!
        *clock.lock().unwrap() = policy_utc(2026, 11, 29, 0, 5, 0);
        let st = engine.handle(Some(&IpcRequest {
            cmd: "status".into(),
            ..Default::default()
        }));
        assert_eq!(st.logical_day, "2026-11-29");
        assert!(st.in_cooldown);

        // Cooldown expires on Tuesday during play hours while Dota is running -> must still enforce forbidden Dota!
        let (_dir2, clock2, procs2, eng2) = make_engine(policy_utc(2026, 11, 24, 20, 15, 0));
        procs2.set(true, false);
        eng2.tick();
        *clock2.lock().unwrap() += Duration::seconds(60);
        eng2.tick();
        procs2.set(false, false);
        *clock2.lock().unwrap() += Duration::seconds(60); // 20:17 -> 5m cooldown until 20:22
        eng2.tick();
        // Jump to 20:22 (exact expiry) with Dota running
        *clock2.lock().unwrap() += Duration::seconds(300);
        procs2.set(true, true);
        eng2.tick();
        assert_eq!(procs2.kill_dota_calls.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn borrow_diminishing_returns_idempotency_and_sunday_block() {
        let (_dir, _clock, _procs, engine) = make_engine(policy_utc(2026, 11, 28, 12, 0, 0));
        let req1 = IpcRequest {
            cmd: "borrow".into(),
            request_id: Some("req-1".into()),
            ..Default::default()
        };
        let r1 = engine.handle(Some(&req1));
        assert!(r1.ok);
        assert_eq!(r1.borrowed_seconds, 570);
        // Replaying same request_id must be idempotent!
        let r1_dup = engine.handle(Some(&req1));
        assert_eq!(r1_dup.borrowed_seconds, 570);
        assert_eq!(r1_dup.borrow_count, 1);

        let r2 = engine.handle(Some(&IpcRequest {
            cmd: "borrow".into(),
            request_id: Some("req-2".into()),
            ..Default::default()
        }));
        assert!(r2.ok);
        assert_eq!(r2.borrowed_seconds, 570 + 528);

        let (_dir2, _clock2, _procs2, sun_engine) = make_engine(policy_utc(2026, 11, 29, 12, 0, 0));
        let rs = sun_engine.handle(Some(&IpcRequest {
            cmd: "borrow".into(),
            ..Default::default()
        }));
        assert!(!rs.ok);
        assert!(rs.error.contains("Sundays"));
    }

    #[test]
    fn tuesday_dota_forbidden_kills_only_dota() {
        let (_dir, _clock, procs, engine) = make_engine(policy_utc(2026, 11, 24, 20, 20, 0));
        procs.set(true, true);
        let notes = engine.tick();
        assert_eq!(procs.kill_dota_calls.load(Ordering::SeqCst), 1);
        assert_eq!(procs.kill_play_calls.load(Ordering::SeqCst), 0);
        assert!(notes.iter().any(|(t, _)| t.contains("forbidden")));
    }

    #[test]
    fn workout_credit_unlocks_weekday_dota_and_carries_to_weekend() {
        let (_dir, clock, procs, engine) = make_engine(policy_utc(2026, 11, 23, 20, 15, 0));
        engine
            .apply_workout_earned(
                "2026-11-23",
                WorkoutCreditTotals {
                    weekday_seconds: 1800,
                    weekend_seconds: 0,
                },
                *clock.lock().unwrap(),
            )
            .unwrap();
        procs.set(true, true);
        engine.tick();
        for _ in 0..10 {
            *clock.lock().unwrap() += Duration::seconds(60);
            engine.tick();
        }
        procs.set(false, false);
        *clock.lock().unwrap() = policy_utc(2026, 11, 28, 12, 0, 0);
        let st = engine.handle(Some(&IpcRequest {
            cmd: "status".into(),
            ..Default::default()
        }));
        assert!(st.dota_weekend_budget);
        assert_eq!(st.dota_carryover_seconds, 1200.0);
        assert_eq!(st.dota_budget_seconds, 14400.0 + 1200.0);
    }

    #[test]
    fn corrupt_state_refuses_to_wipe() {
        let dir = TempDir::new().unwrap();
        let path = dir.path().join("state.json");
        std::fs::write(&path, "{not valid json").unwrap();
        let res = TimerEngine::new(
            StateStore::new(Some(path)),
            Arc::new(FakeProcesses::new()),
            None,
        );
        assert!(matches!(res, Err(StateStoreError::CorruptState)));
    }
}
