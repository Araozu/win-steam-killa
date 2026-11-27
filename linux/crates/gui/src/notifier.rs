use std::sync::mpsc::{self, Sender};
use std::thread;

use notify_rust::{Notification, Timeout, Urgency};
use steamtimer_core::clock::PolicyClock;
use steamtimer_core::limits;
use steamtimer_core::protocol::IpcResponse;
use steamtimer_core::view_state::format_hm_floor;
use tracing::debug;

pub struct Notifier {
    tx: Sender<(String, String, bool)>,
    seen_day: Option<String>,
    seen_budget_key: Option<String>,
    warned: bool,
    enforce_told: bool,
    seen_session_key: Option<String>,
    session_warned: bool,
    seen_cooldown_key: Option<String>,
    cooldown_told: bool,
    was_cooldown: bool,
    seen_dota_key: Option<String>,
    dota_warned: bool,
    dota_told: bool,
    seen_forbidden_day: Option<String>,
    forbidden_told: bool,
    seen_window_key: Option<String>,
    window_told: bool,
    window_warn_key: Option<String>,
}

impl Default for Notifier {
    fn default() -> Self {
        let (tx, rx) = mpsc::channel::<(String, String, bool)>();
        thread::spawn(move || {
            while let Ok((title, body, urgent)) = rx.recv() {
                let mut n = Notification::new();
                n.appname("Steam Timer")
                    .summary(&title)
                    .body(&body)
                    .icon("security-high-symbolic")
                    .timeout(Timeout::Milliseconds(8000));
                if urgent {
                    n.urgency(Urgency::Critical);
                }
                if let Err(err) = n.show() {
                    debug!("Desktop notification failed ({title}): {err}");
                }
            }
        });
        Self {
            tx,
            seen_day: None,
            seen_budget_key: None,
            warned: false,
            enforce_told: false,
            seen_session_key: None,
            session_warned: false,
            seen_cooldown_key: None,
            cooldown_told: false,
            was_cooldown: false,
            seen_dota_key: None,
            dota_warned: false,
            dota_told: false,
            seen_forbidden_day: None,
            forbidden_told: false,
            seen_window_key: None,
            window_told: false,
            window_warn_key: None,
        }
    }
}

impl Notifier {
    pub fn send(&self, title: &str, body: &str, urgent: bool) {
        let _ = self
            .tx
            .send((title.to_string(), body.to_string(), urgent));
    }

    pub fn process_status(&mut self, resp: &IpcResponse) {
        let Some(server_now_utc) = resp.server_now_utc else {
            return;
        };
        let server_now = PolicyClock::from_utc(server_now_utc);

        if self.seen_day.as_deref() != Some(&resp.logical_day) {
            self.seen_day = Some(resp.logical_day.clone());
            self.seen_session_key = None;
            self.session_warned = false;
            self.seen_cooldown_key = None;
            self.cooldown_told = false;
        }

        let budget_key = format!("{}|{}", resp.logical_day, resp.budget_seconds);
        if self.seen_budget_key.as_deref() != Some(&budget_key) {
            self.seen_budget_key = Some(budget_key);
            self.warned = false;
            self.enforce_told = false;
        }

        let dota_key = format!(
            "{}|{}|{:.0}",
            resp.dota_week_start, resp.dota_weekend_budget, resp.dota_budget_seconds
        );
        if self.seen_dota_key.as_deref() != Some(&dota_key) {
            self.seen_dota_key = Some(dota_key);
            self.dota_warned = false;
            self.dota_told = false;
        }

        if self.seen_forbidden_day.as_deref() != Some(&resp.logical_day) {
            self.seen_forbidden_day = Some(resp.logical_day.clone());
            self.forbidden_told = false;
        }

        if resp.outside_play_hours {
            let wkey = resp
                .next_play_allowed_local
                .map(|d| d.format("%Y-%m-%d %H:%M").to_string())
                .unwrap_or_default();
            if self.seen_window_key.as_deref() != Some(&wkey) {
                self.seen_window_key = Some(wkey);
                self.window_told = false;
            }
            if !self.window_told {
                self.window_told = true;
                let back = resp
                    .next_play_allowed_local
                    .map(limits::format_clock)
                    .unwrap_or_else(|| "—".into());
                let extra = if resp.enforcing || resp.remaining_seconds <= 0.0 {
                    " Daily budget also used up."
                } else {
                    ""
                };
                self.send(
                    "Outside play hours",
                    &format!(
                        "Steam blocked ({}, weekends free). Back at {back}.{extra}",
                        limits::PLAY_HOURS_SUMMARY
                    ),
                    false,
                );
            }
            self.was_cooldown = false;
            return;
        }

        if resp.enforcing || resp.remaining_seconds <= 0.0 {
            if !self.enforce_told {
                self.enforce_told = true;
                self.send(
                    "Steam time is up",
                    &format!("Daily budget used. Blocked until {}.", limits::RESET_CLOCK_TEXT),
                    true,
                );
            }
            self.was_cooldown = false;
            return;
        }

        if resp.in_cooldown {
            let key = format!(
                "{}|cd|{}",
                resp.logical_day,
                resp.cooldown_until_utc
                    .map(|u| u.timestamp())
                    .unwrap_or(0)
            );
            if self.seen_cooldown_key.as_deref() != Some(&key) {
                self.seen_cooldown_key = Some(key);
                self.cooldown_told = false;
            }
            if !self.cooldown_told {
                self.cooldown_told = true;
                let until = resp
                    .cooldown_until_utc
                    .map(|u| {
                        format!(
                            " Next session after {}.",
                            limits::format_clock(PolicyClock::from_utc(u))
                        )
                    })
                    .unwrap_or_default();
                self.send(
                    "Cooldown — Steam blocked",
                    &format!(
                        "{} until the next session.{until}",
                        format_hm_floor(resp.cooldown_remaining_seconds)
                    ),
                    false,
                );
            }
            self.was_cooldown = true;
            return;
        }

        if self.was_cooldown {
            self.was_cooldown = false;
            self.send("Cooldown over", "You can play again.", false);
        }

        let playing = resp.running && resp.steam_running;
        let warn_before = limits::WARN_BEFORE_SECS as f64;
        let session_ending = playing
            && resp.session_remaining_seconds < resp.remaining_seconds
            && resp.session_remaining_seconds <= warn_before
            && resp.session_remaining_seconds > 0.0;
        let dota_low = resp.dota_running
            && !resp.dota_forbidden
            && !resp.dota_enforcing
            && resp.dota_remaining_seconds <= warn_before
            && resp.dota_remaining_seconds > 0.0;

        if playing && resp.remaining_seconds <= warn_before && !self.warned {
            self.warned = true;
            self.send(
                "Steam closes in 15 minutes",
                &format!(
                    "{} of today's budget left. Save your game.",
                    format_hm_floor(resp.remaining_seconds)
                ),
                true,
            );
        }

        if session_ending {
            let key = format!(
                "{}|{}",
                resp.logical_day,
                resp.start_utc.map(|u| u.timestamp()).unwrap_or(0)
            );
            if self.seen_session_key.as_deref() != Some(&key) {
                self.seen_session_key = Some(key);
                self.session_warned = false;
            }
            if !self.session_warned {
                self.session_warned = true;
                self.send(
                    "Session ending",
                    &format!(
                        "This session ends in {}, then a cooldown. Save your game.",
                        format_hm_floor(resp.session_remaining_seconds)
                    ),
                    true,
                );
            }
        } else {
            self.seen_session_key = None;
            self.session_warned = false;
        }

        if playing && let Some(window_end) = resp.window_ends_local {
            let window_left = (window_end - server_now).num_milliseconds() as f64 / 1000.0;
            let key = window_end.format("%Y-%m-%d %H:%M").to_string();
            if self.window_warn_key.as_deref() != Some(&key)
                && window_left > 0.0
                && window_left <= warn_before
                && window_left < resp.remaining_seconds
                && window_left < resp.session_remaining_seconds
            {
                self.window_warn_key = Some(key);
                self.send(
                    "Play window closes soon",
                    &format!(
                        "Steam closes at {} when play hours end ({} left). Save your game.",
                        limits::format_clock(window_end),
                        limits::format_hm_ceil(window_left)
                    ),
                    true,
                );
            }
        }

        if resp.dota_forbidden {
            if !self.forbidden_told {
                self.forbidden_told = true;
                self.send(
                    "Dota 2 forbidden on Tue / Thu",
                    "No Dota today — back tomorrow. Steam + other games continue.",
                    false,
                );
            }
        } else if resp.dota_enforcing {
            if !self.dota_told {
                self.dota_told = true;
                if !resp.dota_weekend_budget && resp.dota_budget_seconds <= 0.0 {
                    self.send(
                        "Dota 2 needs a workout",
                        "No workout credit this week yet, so weekday Dota is blocked. Other games continue.",
                        false,
                    );
                } else {
                    self.send(
                        "Dota 2 time is up",
                        "The current Dota allowance is used. Other games continue.",
                        false,
                    );
                }
            }
        } else if dota_low && !self.dota_warned {
            self.dota_warned = true;
            self.send(
                "Dota 2 closes in 15 minutes",
                &format!(
                    "{} of the current Dota budget left. Other games are unaffected.",
                    format_hm_floor(resp.dota_remaining_seconds)
                ),
                true,
            );
        }
    }
}
