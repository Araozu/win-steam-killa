use std::cell::RefCell;
use std::path::PathBuf;
use std::rc::Rc;

use chrono::{Datelike, Weekday};
use gtk4::glib;
use gtk4::prelude::*;
use libadwaita as adw;
use libadwaita::prelude::*;
use steamtimer_core::clock::{LogicalDay, PolicyClock};
use steamtimer_core::limits;
use steamtimer_core::protocol::{IpcRequest, IpcResponse};
use steamtimer_core::view_state::{
    TimerViewState, describe_seen, describe_tray, format_duration, format_hm_floor, format_max,
    tomorrow_remaining,
};

use crate::ipc_client::{ACTION_TIMEOUT, DEFAULT_TIMEOUT, IpcReply, send_request};
use crate::notifier::Notifier;
use crate::tray::{TrayCommand, TrayController};
use crate::workout_view::WorkoutSyncPage;

struct AppSharedState {
    view_state: TimerViewState,
    notifier: Notifier,
    tray: TrayController,
    last_note: Option<String>,
    busy: bool,
    polling: bool,
    allow_close: bool,
}

#[derive(Clone)]
pub struct MainUi {
    pub window: adw::ApplicationWindow,
    nav_view: adw::NavigationView,
    socket_path: PathBuf,
    shared: Rc<RefCell<AppSharedState>>,
    // Headline widgets
    headline_label: gtk4::Label,
    countdown_label: gtk4::Label,
    unavailable_label: gtk4::Label,
    ready_label: gtk4::Label,
    status_label: gtk4::Label,
    // Time of day
    tod_bar: gtk4::ProgressBar,
    tod_start: gtk4::Label,
    tod_now: gtk4::Label,
    tod_end: gtk4::Label,
    // Session
    sess_bar: gtk4::ProgressBar,
    sess_mid: gtk4::Label,
    sess_max: gtk4::Label,
    // Daily
    day_bar: gtk4::ProgressBar,
    day_mid: gtk4::Label,
    day_max: gtk4::Label,
    tomorrow_label: gtk4::Label,
    borrow_btn: gtk4::Button,
    // Dota
    dota_title: gtk4::Label,
    dota_bar: gtk4::ProgressBar,
    dota_mid: gtk4::Label,
    dota_max: gtk4::Label,
    dota_blocked_label: gtk4::Label,
    // Note + details
    action_note_label: gtk4::Label,
    detector_label: gtk4::Label,
    recheck_btn: gtk4::Button,
    force_btn: gtk4::Button,
    reset_btn: gtk4::Button,
    today_detail_label: gtk4::Label,
    detector_detail_label: gtk4::Label,
}

impl MainUi {
    pub fn build(app: &adw::Application, socket_path: PathBuf) -> Self {
        let (tray_tx, tray_rx) = async_channel::unbounded::<TrayCommand>();
        let tray = TrayController::spawn(tray_tx);

        let shared = Rc::new(RefCell::new(AppSharedState {
            view_state: TimerViewState::default(),
            notifier: Notifier::default(),
            tray,
            last_note: None,
            busy: false,
            polling: false,
            allow_close: false,
        }));

        let nav_view = adw::NavigationView::new();

        // ---- Timer Page ----
        let toolbar_view = adw::ToolbarView::new();
        let header = adw::HeaderBar::new();

        // Header menu with Quit action
        let menu = gtk4::gio::Menu::new();
        menu.append(Some("Workout sync"), Some("win.workout-sync"));
        menu.append(Some("Quit tray & notifications"), Some("win.quit-tray"));
        let menu_btn = gtk4::MenuButton::builder()
            .icon_name("open-menu-symbolic")
            .menu_model(&menu)
            .tooltip_text("Menu")
            .build();
        header.pack_end(&menu_btn);
        toolbar_view.add_top_bar(&header);

        let root_box = gtk4::Box::new(gtk4::Orientation::Vertical, 8);
        root_box.set_margin_top(10);
        root_box.set_margin_bottom(14);
        root_box.set_margin_start(12);
        root_box.set_margin_end(12);

        // Headline row
        let head_row = gtk4::Box::new(gtk4::Orientation::Horizontal, 8);
        let headline_label = gtk4::Label::builder()
            .label("Timer")
            .xalign(0.0)
            .hexpand(true)
            .valign(gtk4::Align::Center)
            .build();
        headline_label.add_css_class("headline-label");

        let countdown_label = gtk4::Label::builder()
            .use_markup(true)
            .label("–<span size='small' alpha='70%'>h</span> –<span size='small' alpha='70%'>m</span> –<span size='small' alpha='70%'>s</span>")
            .valign(gtk4::Align::Center)
            .build();
        countdown_label.add_css_class("countdown-label");

        let unavailable_label = gtk4::Label::builder()
            .label("Blocked")
            .valign(gtk4::Align::Center)
            .visible(false)
            .build();
        unavailable_label.add_css_class("unavailable-label");

        let ready_label = gtk4::Label::builder()
            .label("Ready")
            .valign(gtk4::Align::Center)
            .visible(false)
            .build();
        ready_label.add_css_class("ready-label");

        head_row.append(&headline_label);
        head_row.append(&countdown_label);
        head_row.append(&unavailable_label);
        head_row.append(&ready_label);
        root_box.append(&head_row);

        let status_label = gtk4::Label::builder()
            .label("Waiting for steamtimerd…")
            .xalign(0.0)
            .wrap(true)
            .build();
        status_label.add_css_class("detail-text");
        root_box.append(&status_label);

        // Limits section
        root_box.append(&section_title("Limits"));

        // Time of day card
        let (tod_card, tod_bar, tod_start, tod_now, tod_end) = make_three_label_card(
            "Time of day",
            "bar-tod",
        );
        root_box.append(&tod_card);

        // Session length card
        let (sess_card, sess_bar, sess_mid, sess_max) = make_two_label_card(
            "Session length",
            "bar-session",
            "2h",
        );
        root_box.append(&sess_card);

        // Allowances section
        root_box.append(&section_title("Allowances"));

        // Daily budget card + borrow row
        let (day_card, day_bar, day_mid, day_max) = make_two_label_card(
            "Daily budget",
            "bar-daily",
            "—",
        );
        let borrow_row = gtk4::Box::new(gtk4::Orientation::Horizontal, 8);
        borrow_row.set_margin_top(6);
        let tomorrow_label = gtk4::Label::builder()
            .label("Tomorrow: —")
            .xalign(0.0)
            .hexpand(true)
            .valign(gtk4::Align::Center)
            .build();
        tomorrow_label.add_css_class("scale-label");

        let borrow_btn = gtk4::Button::builder()
            .label("Borrow 10m")
            .sensitive(false)
            .build();
        borrow_row.append(&tomorrow_label);
        borrow_row.append(&borrow_btn);
        day_card.append(&borrow_row);
        root_box.append(&day_card);

        // Dota 2 card
        let dota_card = gtk4::Box::new(gtk4::Orientation::Vertical, 4);
        dota_card.add_css_class("card-box");
        let dota_title = gtk4::Label::builder()
            .label("Dota 2 budget")
            .xalign(0.0)
            .build();
        dota_title.add_css_class("card-title");
        let dota_bar = make_bar("bar-dota");
        let (dota_scale, dota_mid, dota_max) = make_mid_right_row("—");
        let dota_blocked_label = gtk4::Label::builder()
            .label("Dota blocked today (Tue / Thu)")
            .xalign(0.0)
            .visible(false)
            .margin_top(2)
            .build();
        dota_blocked_label.add_css_class("scale-label");
        dota_blocked_label.add_css_class("dota-blocked");

        dota_card.append(&dota_title);
        dota_card.append(&dota_bar);
        dota_card.append(&dota_scale);
        dota_card.append(&dota_blocked_label);
        root_box.append(&dota_card);

        // Action note
        let action_note_label = gtk4::Label::builder()
            .label("")
            .xalign(0.0)
            .wrap(true)
            .visible(false)
            .margin_top(4)
            .build();
        action_note_label.add_css_class("action-note");
        root_box.append(&action_note_label);

        // Details expander
        let expander = gtk4::Expander::builder()
            .label("Details")
            .expanded(false)
            .margin_top(4)
            .build();
        let details_card = gtk4::Box::new(gtk4::Orientation::Vertical, 8);
        details_card.add_css_class("card-box");
        details_card.set_margin_top(6);

        let detector_label = gtk4::Label::builder()
            .label("Detector: —")
            .xalign(0.0)
            .wrap(true)
            .build();
        details_card.append(&detector_label);

        let btn_box = gtk4::Box::new(gtk4::Orientation::Vertical, 6);
        let recheck_btn = gtk4::Button::builder().label("Recheck now").build();
        let force_btn = gtk4::Button::builder()
            .label("Force close Steam + games")
            .build();
        force_btn.add_css_class("destructive-action");
        let reset_btn = gtk4::Button::builder().label("Reset view").build();
        btn_box.append(&recheck_btn);
        btn_box.append(&force_btn);
        btn_box.append(&reset_btn);
        details_card.append(&btn_box);

        let today_detail_label = gtk4::Label::builder()
            .label("")
            .xalign(0.0)
            .wrap(true)
            .selectable(true)
            .build();
        today_detail_label.add_css_class("detail-text");
        details_card.append(&today_detail_label);

        let detector_detail_label = gtk4::Label::builder()
            .label("")
            .xalign(0.0)
            .wrap(true)
            .selectable(true)
            .build();
        detector_detail_label.add_css_class("detail-text");
        details_card.append(&detector_detail_label);

        expander.set_child(Some(&details_card));
        root_box.append(&expander);

        // Workout sync button at bottom right
        let workout_btn = gtk4::Button::builder()
            .label("Workout sync")
            .halign(gtk4::Align::End)
            .margin_top(6)
            .build();
        root_box.append(&workout_btn);

        let clamp = adw::Clamp::builder()
            .maximum_size(520)
            .child(&root_box)
            .build();
        let scroll = gtk4::ScrolledWindow::builder()
            .hscrollbar_policy(gtk4::PolicyType::Never)
            .child(&clamp)
            .build();
        toolbar_view.set_content(Some(&scroll));

        let timer_page = adw::NavigationPage::builder()
            .title("Steam Timer")
            .tag("timer")
            .child(&toolbar_view)
            .build();
        nav_view.add(&timer_page);

        let workout_page = WorkoutSyncPage::new(socket_path.clone());
        nav_view.add(&workout_page.page);

        let window = adw::ApplicationWindow::builder()
            .application(app)
            .title("Steam Timer")
            .default_width(480)
            .default_height(640)
            .content(&nav_view)
            .build();
        window.set_size_request(380, 360);

        let ui = Self {
            window,
            nav_view,
            socket_path,
            shared,
            headline_label,
            countdown_label,
            unavailable_label,
            ready_label,
            status_label,
            tod_bar,
            tod_start,
            tod_now,
            tod_end,
            sess_bar,
            sess_mid,
            sess_max,
            day_bar,
            day_mid,
            day_max,
            tomorrow_label,
            borrow_btn,
            dota_title,
            dota_bar,
            dota_mid,
            dota_max,
            dota_blocked_label,
            action_note_label,
            detector_label,
            recheck_btn,
            force_btn,
            reset_btn,
            today_detail_label,
            detector_detail_label,
        };

        // Window actions
        {
            let u = ui.clone();
            let act_ws = gtk4::gio::SimpleAction::new("workout-sync", None);
            act_ws.connect_activate(move |_, _| {
                u.nav_view.push_by_tag("workout-sync");
            });
            ui.window.add_action(&act_ws);
        }
        {
            let u = ui.clone();
            let act_quit = gtk4::gio::SimpleAction::new("quit-tray", None);
            act_quit.connect_activate(move |_, _| u.confirm_quit());
            ui.window.add_action(&act_quit);
        }

        // Button handlers
        {
            let u = ui.clone();
            workout_btn.connect_clicked(move |_| {
                u.nav_view.push_by_tag("workout-sync");
            });
        }
        {
            let u = ui.clone();
            ui.recheck_btn.connect_clicked(move |_| {
                u.send_action("recheck", DEFAULT_TIMEOUT, "Recheck", false);
            });
        }
        {
            let u = ui.clone();
            ui.force_btn.connect_clicked(move |_| {
                u.confirm_force_close(false);
            });
        }
        {
            let u = ui.clone();
            ui.borrow_btn.connect_clicked(move |_| {
                u.handle_borrow(false);
            });
        }
        {
            let u = ui.clone();
            ui.reset_btn.connect_clicked(move |_| {
                {
                    let mut st = u.shared.borrow_mut();
                    if st.busy {
                        return;
                    }
                    st.last_note = None;
                }
                u.refresh_countdown();
                u.poll_service();
            });
        }

        // Hide on close (keeps 15s notifier + tray running, re-opening steamtimer re-presents window).
        {
            let u = ui.clone();
            ui.window.connect_close_request(move |win| {
                if u.shared.borrow().allow_close {
                    return glib::Propagation::Proceed;
                }
                u.nav_view.pop_to_tag("timer");
                win.set_visible(false);
                glib::Propagation::Stop
            });
        }

        // Handle commands coming from the tray menu
        {
            let u = ui.clone();
            glib::spawn_future_local(async move {
                while let Ok(cmd) = tray_rx.recv().await {
                    match cmd {
                        TrayCommand::Open => u.show_main(),
                        TrayCommand::Recheck => {
                            u.send_action("recheck", DEFAULT_TIMEOUT, "Recheck", true)
                        }
                        TrayCommand::ForceClose => {
                            u.show_main();
                            u.confirm_force_close(true);
                        }
                        TrayCommand::Borrow => u.handle_borrow(true),
                        TrayCommand::Quit => {
                            u.show_main();
                            u.confirm_quit();
                        }
                    }
                }
            });
        }

        // 1-second UI tick (runs even while hidden to keep tray tooltip live)
        {
            let u = ui.clone();
            glib::timeout_add_seconds_local(1, move || {
                if u.shared.borrow().allow_close {
                    return glib::ControlFlow::Break;
                }
                u.refresh_countdown();
                glib::ControlFlow::Continue
            });
        }

        // 15-second service poll
        {
            let u = ui.clone();
            glib::timeout_add_seconds_local(15, move || {
                if u.shared.borrow().allow_close {
                    return glib::ControlFlow::Break;
                }
                u.poll_service();
                glib::ControlFlow::Continue
            });
        }

        ui.refresh_countdown();
        ui.poll_service();
        ui
    }

    pub fn show_main(&self) {
        self.window.set_visible(true);
        self.window.present();
    }

    fn poll_service(&self) {
        {
            let mut st = self.shared.borrow_mut();
            if st.polling || st.allow_close {
                return;
            }
            st.polling = true;
        }

        let sock = self.socket_path.clone();
        let (tx, rx) = async_channel::bounded::<IpcReply>(1);
        std::thread::spawn(move || {
            let reply = send_request(
                &sock,
                &IpcRequest {
                    cmd: "status".into(),
                    ..Default::default()
                },
                DEFAULT_TIMEOUT,
            );
            let _ = tx.send_blocking(reply);
        });

        let u = self.clone();
        glib::spawn_future_local(async move {
            let reply = rx.recv().await.ok();
            {
                let mut st = u.shared.borrow_mut();
                st.polling = false;
                if st.allow_close {
                    return;
                }
            }
            if let Some(reply) = reply {
                match reply.response {
                    Some(resp) if resp.ok => {
                        u.apply_status(resp, false, true);
                    }
                    Some(resp) => {
                        let problem = if resp.error.is_empty() {
                            reply.problem
                        } else {
                            resp.error
                        };
                        u.mark_unavailable(Some(&problem));
                    }
                    None => u.mark_unavailable(Some(&reply.problem)),
                }
            }
        });
    }

    fn apply_status(&self, resp: IpcResponse, keep_note: bool, notify: bool) {
        {
            let mut st = self.shared.borrow_mut();
            if st.allow_close {
                return;
            }
            if !st.view_state.update(resp.clone()) {
                return;
            }
            if !keep_note && !resp.action_note.trim().is_empty() {
                st.last_note = Some(resp.action_note.clone());
            }
            if notify {
                st.notifier.process_status(&resp);
            }
        }
        self.refresh_countdown();
    }

    fn mark_unavailable(&self, problem: Option<&str>) {
        {
            let mut st = self.shared.borrow_mut();
            if st.allow_close {
                return;
            }
            st.view_state.mark_unavailable(problem);
        }
        self.refresh_countdown();
    }

    fn send_action(&self, cmd: &str, timeout: std::time::Duration, label: &str, notify_result: bool) {
        {
            let mut st = self.shared.borrow_mut();
            if st.busy || st.allow_close {
                return;
            }
            st.busy = true;
        }
        self.refresh_countdown();

        let sock = self.socket_path.clone();
        let cmd_owned = cmd.to_string();
        let label_owned = label.to_string();
        let request_id = if matches!(cmd, "borrow" | "kill") {
            Some(uuid::Uuid::new_v4().simple().to_string())
        } else {
            None
        };
        let (tx, rx) = async_channel::bounded::<IpcReply>(1);
        std::thread::spawn(move || {
            let reply = send_request(
                &sock,
                &IpcRequest {
                    cmd: cmd_owned,
                    request_id,
                    ..Default::default()
                },
                timeout,
            );
            let _ = tx.send_blocking(reply);
        });

        let u = self.clone();
        glib::spawn_future_local(async move {
            let reply = rx.recv().await.ok();
            {
                u.shared.borrow_mut().busy = false;
            }
            let Some(reply) = reply else {
                u.refresh_countdown();
                return;
            };
            match reply.response {
                None => {
                    let msg = format!("{label_owned} failed — {}", reply.problem);
                    {
                        let mut st = u.shared.borrow_mut();
                        st.last_note = Some(msg.clone());
                        if notify_result {
                            st.notifier.send(&format!("{label_owned} failed"), &reply.problem, false);
                        }
                    }
                    u.mark_unavailable(Some(&reply.problem));
                }
                Some(resp) => {
                    let note = if resp.ok {
                        resp.action_note.clone()
                    } else {
                        resp.error.clone()
                    };
                    {
                        let mut st = u.shared.borrow_mut();
                        if !note.trim().is_empty() {
                            st.last_note = Some(note.clone());
                        }
                        if notify_result {
                            if resp.ok {
                                let body = if note.trim().is_empty() {
                                    "Done.".to_string()
                                } else {
                                    note.clone()
                                };
                                st.notifier.send(&label_owned, &body, false);
                            } else {
                                st.notifier.send(
                                    &format!("{label_owned} unavailable"),
                                    &resp.error,
                                    false,
                                );
                            }
                        }
                    }
                    u.apply_status(resp, true, false);
                }
            }
        });
    }

    fn handle_borrow(&self, notify_result: bool) {
        let is_sunday = {
            let st = self.shared.borrow();
            st.view_state
                .last()
                .and_then(|r| LogicalDay::parse(&r.logical_day))
                .map(|d| d.weekday() == Weekday::Sun)
                .unwrap_or(false)
        };
        if is_sunday {
            let dialog = adw::AlertDialog::builder()
                .heading("Borrow unavailable")
                .body("Borrow is unavailable on Sundays — enjoy the 4h budget.")
                .build();
            dialog.add_response("ok", "OK");
            dialog.present(Some(&self.window));
            return;
        }
        self.send_action("borrow", ACTION_TIMEOUT, "Borrow", notify_result);
    }

    fn confirm_force_close(&self, notify_result: bool) {
        let what = {
            let st = self.shared.borrow();
            match st.view_state.last() {
                Some(last)
                    if !last.live_clients.is_empty()
                        || !last.live_games.is_empty()
                        || !last.matched_orphans.is_empty() =>
                {
                    format!("The service currently sees: {}.\n\n", describe_seen(last))
                }
                _ => String::new(),
            }
        };
        let body = format!(
            "{what}Force-close Steam + detected games now? This stops the timer immediately (the normal cooldown for the time played still applies)."
        );
        let dialog = adw::AlertDialog::builder()
            .heading("Force close Steam + games?")
            .body(&body)
            .build();
        dialog.add_response("cancel", "Cancel");
        dialog.add_response("close", "Force close");
        dialog.set_response_appearance("close", adw::ResponseAppearance::Destructive);
        dialog.set_default_response(Some("cancel"));
        dialog.set_close_response("cancel");

        let u = self.clone();
        dialog.choose(&self.window, None::<&gtk4::gio::Cancellable>, move |resp| {
            if resp == "close" {
                u.send_action("kill", ACTION_TIMEOUT, "Force-close", notify_result);
            }
        });
    }

    fn confirm_quit(&self) {
        let dialog = adw::AlertDialog::builder()
            .heading("Quit Steam Timer tray?")
            .body("The daemon (steamtimerd) keeps enforcing limits in the background, but you will NOT receive the 15-minute or time-up notifications while the tray is closed.")
            .build();
        dialog.add_response("cancel", "Keep running");
        dialog.add_response("quit", "Quit");
        dialog.set_response_appearance("quit", adw::ResponseAppearance::Destructive);
        dialog.set_default_response(Some("cancel"));
        dialog.set_close_response("cancel");

        let u = self.clone();
        dialog.choose(&self.window, None::<&gtk4::gio::Cancellable>, move |resp| {
            if resp == "quit" {
                {
                    let mut st = u.shared.borrow_mut();
                    st.allow_close = true;
                    st.tray.shutdown();
                }
                if let Some(app) = u.window.application() {
                    app.quit();
                } else {
                    u.window.close();
                }
            }
        });
    }

    fn refresh_countdown(&self) {
        let now = PolicyClock::now();
        let (
            busy,
            service_available,
            is_current,
            problem,
            elapsed,
            last_note,
            last_opt,
            projected,
        ) = {
            let mut st = self.shared.borrow_mut();
            if st.allow_close {
                return;
            }
            let (tray_txt, tray_lvl) = describe_tray(&st.view_state, now);
            st.tray.update_status(&tray_txt, tray_lvl);

            let busy = st.busy;
            let is_current = st.view_state.is_current();
            let last_opt = st.view_state.last().cloned();
            if let Some(ref r) = last_opt {
                let sunday = LogicalDay::parse(&r.logical_day)
                    .map(|d| d.weekday() == Weekday::Sun)
                    .unwrap_or(false);
                let can_borrow = !busy
                    && is_current
                    && !sunday
                    && tomorrow_remaining(r, now) >= limits::BORROW_COST_SECS as f64;
                st.tray.update_borrow(r.borrow_count, can_borrow);
            } else {
                st.tray.update_borrow(0, false);
            }

            let projected = st.view_state.project(now);
            (
                busy,
                st.view_state.service_available(),
                is_current,
                st.view_state.problem().to_string(),
                st.view_state.elapsed_seconds(),
                st.last_note.clone(),
                last_opt,
                projected,
            )
        };

        // Action note
        if let Some(ref note) = last_note
            && !note.trim().is_empty()
        {
            self.action_note_label.set_label(note);
            self.action_note_label.set_visible(true);
        } else {
            self.action_note_label.set_visible(false);
        }

        // Reset default header visibilities
        self.countdown_label.set_visible(true);
        self.status_label.set_visible(true);
        self.unavailable_label.set_visible(false);
        self.ready_label.set_visible(false);
        self.headline_label.set_label("Timer");

        let default_status = if !service_available {
            format!("{problem} Values are last known.")
        } else if last_opt.is_none() {
            "Waiting for steamtimerd…".to_string()
        } else if !is_current {
            "Service status is stale — values are last known.".to_string()
        } else {
            String::new()
        };
        self.status_label.set_label(&default_status);

        self.recheck_btn.set_sensitive(!busy);
        self.force_btn.set_sensitive(!busy);
        self.reset_btn.set_sensitive(!busy);

        let (Some(r), Some(values)) = (last_opt, projected) else {
            self.countdown_label.set_markup(
                "–<span size='small' alpha='70%'>h</span> –<span size='small' alpha='70%'>m</span> –<span size='small' alpha='70%'>s</span>",
            );
            self.tod_bar.set_fraction(0.0);
            self.sess_bar.set_fraction(0.0);
            self.day_bar.set_fraction(0.0);
            self.dota_bar.set_fraction(0.0);
            self.detector_label.set_label("Detector: —");
            self.borrow_btn.set_sensitive(false);
            return;
        };

        // Borrow button + heat styling
        let tom_rem = tomorrow_remaining(&r, now);
        self.tomorrow_label
            .set_label(&format!("Tomorrow: {}", format_hm_floor(tom_rem)));
        let sunday = LogicalDay::parse(&r.logical_day)
            .map(|d| d.weekday() == Weekday::Sun)
            .unwrap_or(false);
        let can_borrow =
            !busy && is_current && !sunday && tom_rem >= limits::BORROW_COST_SECS as f64;
        self.borrow_btn.set_sensitive(can_borrow);
        let next_eff = limits::next_borrow_effectiveness(r.borrow_count);
        let next_gain = limits::borrow_gain_seconds_for_number(r.borrow_count + 1);
        self.borrow_btn.set_label(&format!(
            "Borrow 10m ({})",
            limits::format_effectiveness(next_eff)
        ));
        let tooltip = if sunday {
            "Borrow is unavailable on Sundays.".to_string()
        } else if tom_rem < limits::BORROW_COST_SECS as f64 {
            "Tomorrow has less than 10m left to borrow.".to_string()
        } else {
            format!(
                "Spend 10m of tomorrow for +{} today ({} effective). Cooldowns still apply.",
                limits::format_gain(next_gain),
                limits::format_effectiveness(next_eff)
            )
        };
        self.borrow_btn.set_tooltip_text(Some(&tooltip));
        apply_borrow_heat(&self.borrow_btn, next_eff);

        let outside_play_hours = !limits::is_play_allowed(now);
        if is_current {
            let mut blocked = outside_play_hours
                || r.outside_play_hours
                || r.enforcing
                || values.remaining <= 0.0;
            let cooling = !blocked && r.in_cooldown;
            blocked |= !cooling && values.playable <= 0.0;

            if blocked || cooling {
                self.unavailable_label
                    .set_label(if blocked { "Blocked" } else { "On cooldown" });
                self.unavailable_label.set_visible(true);
                self.countdown_label.set_visible(false);
                self.status_label.set_visible(false);
            } else if values.session_active {
                self.headline_label.set_label("Remaining");
                self.status_label.set_label("Playing");
            } else {
                self.ready_label.set_visible(true);
                self.countdown_label.set_visible(false);
                self.status_label.set_visible(false);
            }
        }

        // Countdown markup
        let total_sec = if outside_play_hours {
            0
        } else {
            values.playable.ceil().max(0.0) as i64
        };
        if is_current {
            let hh = total_sec / 3600;
            let mm = (total_sec % 3600) / 60;
            let ss = total_sec % 60;
            self.countdown_label.set_markup(&format!(
                "{hh:02}<span size='small' alpha='70%'>h</span> {mm:02}<span size='small' alpha='70%'>m</span> {ss:02}<span size='small' alpha='70%'>s</span>"
            ));
        } else {
            self.countdown_label.set_markup(
                "–<span size='small' alpha='70%'>h</span> –<span size='small' alpha='70%'>m</span> –<span size='small' alpha='70%'>s</span>",
            );
        }

        // Time of day bar
        if outside_play_hours {
            self.tod_bar.add_css_class("bar-tod-closed");
        } else {
            self.tod_bar.remove_css_class("bar-tod-closed");
        }
        self.tod_now.set_label(&limits::format_clock(now));
        if let Some(window_end) = limits::current_window_end_local(now) {
            let start_time = if window_end.time() == limits::weekday_window_1_end() {
                limits::weekday_window_1_start()
            } else {
                limits::weekday_window_2_start()
            };
            let window_start = now.date().and_time(start_time);
            self.tod_start
                .set_label(&limits::format_clock(window_start));
            self.tod_end.set_label(&limits::format_clock(window_end));
            let span = (window_end - window_start).num_seconds().max(1) as f64;
            let frac = ((now - window_start).num_seconds() as f64 / span).clamp(0.0, 1.0);
            self.tod_bar.set_fraction(frac);
        } else {
            self.tod_start.set_label("—");
            let end_txt = if outside_play_hours {
                limits::format_clock(limits::next_play_allowed_local(now))
            } else {
                "free".to_string()
            };
            self.tod_end.set_label(&end_txt);
            self.tod_bar.set_fraction(0.0);
        }

        // Session length bar
        let sess_frac = if values.session_max > 0.0 {
            (values.session_elapsed / values.session_max).clamp(0.0, 1.0)
        } else {
            0.0
        };
        self.sess_bar.set_fraction(sess_frac);
        self.sess_mid
            .set_label(&format!("{} played", format_duration(values.session_elapsed)));
        self.sess_max.set_label(&format_max(values.session_max));

        // Daily budget bar
        let daily_max = (r.budget_seconds as f64).max(0.0);
        let day_frac = if daily_max > 0.0 {
            (values.remaining / daily_max).clamp(0.0, 1.0)
        } else {
            0.0
        };
        self.day_bar.set_fraction(day_frac);
        self.day_mid.set_label(&format_hm_floor(values.remaining));
        self.day_max.set_label(&format_max(daily_max));

        // Dota 2 bar
        let dota_max = r.dota_budget_seconds.max(0.0);
        self.dota_title.set_label(if r.dota_weekend_budget {
            "Dota 2 · Weekend budget"
        } else {
            "Dota 2 · Weekday budget"
        });
        self.dota_blocked_label.set_visible(r.dota_forbidden);
        let dota_frac = if dota_max > 0.0 {
            (values.dota_remaining / dota_max).clamp(0.0, 1.0)
        } else {
            0.0
        };
        self.dota_bar.set_fraction(dota_frac);
        self.dota_mid
            .set_label(&format!("{} left", format_duration(values.dota_remaining)));
        self.dota_max
            .set_label(&format!("{} total", format_duration(dota_max)));

        // Details
        self.detector_label.set_label(if r.steam_running {
            "Detector: play detected"
        } else {
            "Detector: nothing detected"
        });

        let mut hours_line = format!(
            "\nPlay hours {} {} (weekends free; days start {}).",
            limits::PLAY_HOURS_SUMMARY,
            PolicyClock::LABEL,
            limits::RESET_CLOCK_TEXT
        );
        if r.outside_play_hours {
            if let Some(next_ok) = r.next_play_allowed_local {
                hours_line.push_str(&format!("\nBlocked until {}.", limits::format_clock(next_ok)));
            }
        } else if let Some(w_end) = r.window_ends_local {
            hours_line.push_str(&format!(
                "\nPlayable until {}.",
                limits::format_clock(w_end)
            ));
        }

        let mut today_detail = format!(
            "Budget {} total\nBase {} + carry {}",
            format_hm_floor(daily_max),
            format_hm_floor(r.base_budget_seconds as f64),
            format_hm_floor(r.carry_in_seconds as f64),
        );
        if r.borrowed_seconds > 0 {
            today_detail.push_str(&format!(
                " + borrow {}",
                format_hm_floor(r.borrowed_seconds as f64)
            ));
        }
        if r.advance_deduction_seconds > 0 {
            today_detail.push_str(&format!(
                " − advance {}",
                format_hm_floor(r.advance_deduction_seconds as f64)
            ));
        }
        today_detail.push_str(&format!(
            "\nUsed {}, left {}\n\nDota {} total\n",
            format_hm_floor(r.used_seconds),
            format_hm_floor(values.remaining),
            format_duration(dota_max),
        ));
        if r.dota_weekend_budget {
            today_detail.push_str(&format!(
                "Base {} + carry {}\n",
                format_hm_floor(limits::DOTA_WEEKEND_BASE_SECS as f64),
                format_duration(r.dota_carryover_seconds),
            ));
        }
        today_detail.push_str(&format!(
            "Earned {} from workouts\nUsed {}, left {}{hours_line}",
            format_duration(r.workout_dota_earned_seconds as f64),
            format_duration(r.dota_used_seconds),
            format_duration(values.dota_remaining),
        ));
        self.today_detail_label.set_label(&today_detail);

        let remembered = if r.remembered_games.is_empty() {
            "Remembered: none.".to_string()
        } else {
            format!("Remembered: {}.", r.remembered_games.join(", "))
        };
        let det_detail = format!(
            "Sees now: {}.\n{remembered}\nLast poll {}s ago.",
            describe_seen(&r),
            elapsed.max(0.0) as i64
        );
        self.detector_detail_label.set_label(&det_detail);
    }
}

fn apply_borrow_heat(btn: &gtk4::Button, eff: f64) {
    for i in 0..=4 {
        btn.remove_css_class(&format!("borrow-heat-{i}"));
    }
    let level = if eff >= 0.85 {
        0
    } else if eff >= 0.65 {
        1
    } else if eff >= 0.40 {
        2
    } else if eff >= 0.15 {
        3
    } else {
        4
    };
    btn.add_css_class(&format!("borrow-heat-{level}"));
}

fn section_title(text: &str) -> gtk4::Label {
    let lbl = gtk4::Label::builder().label(text).xalign(0.0).build();
    lbl.add_css_class("section-title");
    lbl
}

fn make_bar(color_class: &str) -> gtk4::ProgressBar {
    let bar = gtk4::ProgressBar::new();
    bar.add_css_class("st-bar");
    bar.add_css_class(color_class);
    bar
}

fn make_mid_right_row(initial_max: &str) -> (gtk4::CenterBox, gtk4::Label, gtk4::Label) {
    let row = gtk4::CenterBox::new();
    row.set_margin_top(2);
    let mid = gtk4::Label::builder().label("—").build();
    mid.add_css_class("scale-label");
    let right = gtk4::Label::builder().label(initial_max).build();
    right.add_css_class("scale-label");
    row.set_center_widget(Some(&mid));
    row.set_end_widget(Some(&right));
    (row, mid, right)
}

fn make_three_label_card(
    title: &str,
    bar_class: &str,
) -> (gtk4::Box, gtk4::ProgressBar, gtk4::Label, gtk4::Label, gtk4::Label) {
    let card = gtk4::Box::new(gtk4::Orientation::Vertical, 4);
    card.add_css_class("card-box");

    let lbl = gtk4::Label::builder().label(title).xalign(0.0).build();
    lbl.add_css_class("card-title");
    let bar = make_bar(bar_class);

    let row = gtk4::CenterBox::new();
    row.set_margin_top(2);
    let left = gtk4::Label::builder().label("—").build();
    left.add_css_class("scale-label");
    let mid = gtk4::Label::builder().label("—").build();
    mid.add_css_class("scale-label");
    let right = gtk4::Label::builder().label("—").build();
    right.add_css_class("scale-label");
    row.set_start_widget(Some(&left));
    row.set_center_widget(Some(&mid));
    row.set_end_widget(Some(&right));

    card.append(&lbl);
    card.append(&bar);
    card.append(&row);
    (card, bar, left, mid, right)
}

fn make_two_label_card(
    title: &str,
    bar_class: &str,
    initial_max: &str,
) -> (gtk4::Box, gtk4::ProgressBar, gtk4::Label, gtk4::Label) {
    let card = gtk4::Box::new(gtk4::Orientation::Vertical, 4);
    card.add_css_class("card-box");

    let lbl = gtk4::Label::builder().label(title).xalign(0.0).build();
    lbl.add_css_class("card-title");
    let bar = make_bar(bar_class);
    let (row, mid, right) = make_mid_right_row(initial_max);

    card.append(&lbl);
    card.append(&bar);
    card.append(&row);
    (card, bar, mid, right)
}
