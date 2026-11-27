use std::cell::RefCell;
use std::path::PathBuf;
use std::rc::Rc;

use chrono::Local;
use gtk4::glib;
use gtk4::prelude::*;
use libadwaita as adw;
use libadwaita::prelude::*;
use steamtimer_core::protocol::{IpcRequest, WorkoutSyncStatus};
use steamtimer_core::view_state::format_hm_floor;

use crate::ipc_client::{DEFAULT_TIMEOUT, IpcReply, send_request};

#[derive(Default)]
struct WorkoutUiState {
    status: Option<WorkoutSyncStatus>,
    note: Option<String>,
    problem: Option<String>,
    open: bool,
    busy: bool,
    polling: bool,
    loaded_settings: bool,
    revision: u64,
    poll_source: Option<glib::SourceId>,
}

#[derive(Clone)]
pub struct WorkoutSyncPage {
    pub page: adw::NavigationPage,
    socket_path: PathBuf,
    url_row: adw::EntryRow,
    token_row: adw::PasswordEntryRow,
    save_btn: gtk4::Button,
    last_success_label: gtk4::Label,
    earned_credit_label: gtk4::Label,
    sync_btn: gtk4::Button,
    spinner: gtk4::Spinner,
    feedback_label: gtk4::Label,
    state: Rc<RefCell<WorkoutUiState>>,
}

impl WorkoutSyncPage {
    pub fn new(socket_path: PathBuf) -> Self {
        let toolbar_view = adw::ToolbarView::new();
        let header = adw::HeaderBar::new();
        toolbar_view.add_top_bar(&header);

        let content = gtk4::Box::new(gtk4::Orientation::Vertical, 14);
        content.set_margin_top(14);
        content.set_margin_bottom(18);
        content.set_margin_start(14);
        content.set_margin_end(14);

        // Receiver connection group
        let conn_group = adw::PreferencesGroup::builder()
            .title("Receiver connection")
            .build();

        let url_row = adw::EntryRow::builder()
            .title("Receiver URL")
            .build();

        let token_row = adw::PasswordEntryRow::builder()
            .title("Bearer token")
            .build();

        conn_group.add(&url_row);
        conn_group.add(&token_row);

        let save_btn = gtk4::Button::builder()
            .label("Save settings")
            .halign(gtk4::Align::Start)
            .sensitive(false)
            .margin_top(8)
            .build();
        conn_group.add(&save_btn);
        content.append(&conn_group);

        // Sync status card
        let status_card = gtk4::Box::new(gtk4::Orientation::Vertical, 8);
        status_card.add_css_class("card-box");

        let status_title = gtk4::Label::builder()
            .label("Sync status")
            .xalign(0.0)
            .build();
        status_title.add_css_class("card-title");
        status_card.append(&status_title);

        let last_success_label = gtk4::Label::builder()
            .label("Last successful sync · —")
            .xalign(0.0)
            .wrap(true)
            .build();
        status_card.append(&last_success_label);

        let earned_credit_label = gtk4::Label::builder()
            .label("Dota credit earned this week · —")
            .xalign(0.0)
            .wrap(true)
            .build();
        status_card.append(&earned_credit_label);

        let sync_row = gtk4::Box::new(gtk4::Orientation::Horizontal, 10);
        sync_row.set_margin_top(4);
        let sync_btn = gtk4::Button::builder()
            .label("Sync now")
            .sensitive(false)
            .build();
        sync_btn.add_css_class("suggested-action");
        let spinner = gtk4::Spinner::new();
        spinner.set_visible(false);
        sync_row.append(&sync_btn);
        sync_row.append(&spinner);
        status_card.append(&sync_row);

        let feedback_label = gtk4::Label::builder()
            .label("")
            .xalign(0.0)
            .wrap(true)
            .build();
        feedback_label.add_css_class("detail-text");
        status_card.append(&feedback_label);

        content.append(&status_card);

        let clamp = adw::Clamp::builder()
            .maximum_size(520)
            .child(&content)
            .build();
        let scroll = gtk4::ScrolledWindow::builder()
            .hscrollbar_policy(gtk4::PolicyType::Never)
            .child(&clamp)
            .build();
        toolbar_view.set_content(Some(&scroll));

        let page = adw::NavigationPage::builder()
            .title("Workout sync")
            .tag("workout-sync")
            .child(&toolbar_view)
            .build();

        let this = Self {
            page,
            socket_path,
            url_row,
            token_row,
            save_btn,
            last_success_label,
            earned_credit_label,
            sync_btn,
            spinner,
            feedback_label,
            state: Rc::new(RefCell::new(WorkoutUiState::default())),
        };

        // Wire signals
        {
            let w = this.clone();
            this.url_row.connect_changed(move |_| {
                if w.state.borrow().open {
                    w.render();
                }
            });
        }
        {
            let w = this.clone();
            this.token_row.connect_changed(move |_| {
                if w.state.borrow().open {
                    w.render();
                }
            });
        }
        {
            let w = this.clone();
            this.save_btn.connect_clicked(move |_| {
                let req = IpcRequest {
                    cmd: "workout-save".into(),
                    workout_base_url: Some(w.url_row.text().to_string()),
                    workout_api_token: Some(w.token_row.text().to_string()),
                    ..Default::default()
                };
                w.run_command(req);
            });
        }
        {
            let w = this.clone();
            this.sync_btn.connect_clicked(move |_| {
                w.run_command(IpcRequest {
                    cmd: "workout-sync".into(),
                    ..Default::default()
                });
            });
        }
        {
            let w = this.clone();
            this.page.connect_showing(move |_| w.open_screen());
        }
        {
            let w = this.clone();
            this.page.connect_hidden(move |_| w.close_screen());
        }

        this
    }

    pub fn open_screen(&self) {
        {
            let mut st = self.state.borrow_mut();
            if let Some(src) = st.poll_source.take() {
                src.remove();
            }
            st.open = true;
            st.loaded_settings = false;
            st.status = None;
            st.note = None;
            st.problem = None;
            st.revision += 1;
        }
        self.url_row.set_text("");
        self.token_row.set_text("");
        self.render();
        self.refresh();

        let w = self.clone();
        let src = glib::timeout_add_seconds_local(3, move || {
            if !w.state.borrow().open {
                return glib::ControlFlow::Break;
            }
            w.refresh();
            glib::ControlFlow::Continue
        });
        self.state.borrow_mut().poll_source = Some(src);
    }

    pub fn close_screen(&self) {
        let mut st = self.state.borrow_mut();
        st.open = false;
        st.revision += 1;
        if let Some(src) = st.poll_source.take() {
            src.remove();
        }
        drop(st);
        self.token_row.set_text("");
    }

    fn refresh(&self) {
        let rev = {
            let mut st = self.state.borrow_mut();
            if !st.open || st.busy || st.polling {
                return;
            }
            st.polling = true;
            st.revision
        };

        let sock = self.socket_path.clone();
        let (tx, rx) = async_channel::bounded::<IpcReply>(1);
        std::thread::spawn(move || {
            let reply = send_request(
                &sock,
                &IpcRequest {
                    cmd: "workout-status".into(),
                    ..Default::default()
                },
                DEFAULT_TIMEOUT,
            );
            let _ = tx.send_blocking(reply);
        });

        let w = self.clone();
        glib::spawn_future_local(async move {
            let reply = rx.recv().await.ok();
            {
                let mut st = w.state.borrow_mut();
                st.polling = false;
                if !st.open || st.revision != rev {
                    return;
                }
            }
            if let Some(reply) = reply {
                w.apply_reply(&reply);
            }
        });
    }

    fn run_command(&self, req: IpcRequest) {
        let rev = {
            let mut st = self.state.borrow_mut();
            if st.busy {
                return;
            }
            st.busy = true;
            st.note = None;
            st.problem = None;
            st.revision += 1;
            st.revision
        };
        self.render();

        let cmd = req.cmd.clone();
        let sock = self.socket_path.clone();
        let (tx, rx) = async_channel::bounded::<IpcReply>(1);
        std::thread::spawn(move || {
            let reply = send_request(&sock, &req, DEFAULT_TIMEOUT);
            let _ = tx.send_blocking(reply);
        });

        let w = self.clone();
        glib::spawn_future_local(async move {
            let reply = rx.recv().await.ok();
            let valid = {
                let mut st = w.state.borrow_mut();
                st.busy = false;
                st.open && st.revision == rev
            };
            if !valid {
                return;
            }
            if let Some(reply) = reply {
                if let Some(ref resp) = reply.response
                    && resp.ok
                    && resp.workout_sync.is_some()
                {
                    if cmd == "workout-save" {
                        w.state.borrow_mut().loaded_settings = false;
                        w.token_row.set_text("");
                        w.state.borrow_mut().note = Some("Settings saved.".into());
                    }
                } else {
                    let msg = reply
                        .response
                        .as_ref()
                        .map(|r| r.error.clone())
                        .filter(|e| !e.is_empty())
                        .unwrap_or_else(|| reply.problem.clone());
                    w.state.borrow_mut().note = Some(msg);
                }
                w.apply_reply(&reply);
            } else {
                w.render();
            }
        });
    }

    fn apply_reply(&self, reply: &IpcReply) {
        let mut set_url: Option<String> = None;
        {
            let mut st = self.state.borrow_mut();
            if let Some(ws) = reply.response.as_ref().and_then(|r| r.workout_sync.clone()) {
                st.problem = None;
                if !st.loaded_settings {
                    st.loaded_settings = true;
                    set_url = Some(ws.base_url.clone());
                }
                st.status = Some(ws);
            } else {
                st.status = None;
                st.problem = Some(if reply.response.is_none() {
                    reply.problem.clone()
                } else {
                    "Update steamtimerd to use workout settings.".into()
                });
            }
        }
        if let Some(url) = set_url {
            self.url_row.set_text(&url);
        }
        self.render();
    }

    fn render(&self) {
        let st = self.state.borrow();
        let syncing = st.status.as_ref().map(|s| s.syncing).unwrap_or(false);
        let can_edit = st.status.is_some() && !st.busy && !syncing;
        let url_text = self.url_row.text().to_string();
        let token_text = self.token_row.text().to_string();
        let current_url = st
            .status
            .as_ref()
            .map(|s| s.base_url.as_str())
            .unwrap_or("");
        let edited = url_text.trim() != current_url || !token_text.is_empty();
        let has_token = st.status.as_ref().map(|s| s.has_token).unwrap_or(false);

        self.url_row.set_sensitive(can_edit);
        self.token_row.set_sensitive(can_edit);
        self.save_btn
            .set_sensitive(can_edit && edited && !url_text.trim().is_empty());
        self.sync_btn
            .set_sensitive(can_edit && has_token && !current_url.trim().is_empty() && !edited);

        self.token_row.set_title(if has_token {
            "Bearer token (saved — enter to replace)"
        } else {
            "Bearer token"
        });

        let last_sync_txt = match st.status.as_ref().and_then(|s| s.last_success_utc) {
            Some(dt) => {
                let local = dt.with_timezone(&Local);
                format!("Last successful sync · {}", local.format("%Y-%m-%d %H:%M"))
            }
            None => "Last successful sync · —".into(),
        };
        self.last_success_label.set_label(&last_sync_txt);

        let earned_txt = match st.status.as_ref() {
            Some(s) => format!(
                "Dota credit earned this week · {}",
                format_hm_floor(s.earned_seconds as f64)
            ),
            None => "Dota credit earned this week · —".into(),
        };
        self.earned_credit_label.set_label(&earned_txt);

        let active_spin = syncing || st.busy;
        self.spinner.set_spinning(active_spin);
        self.spinner.set_visible(active_spin);

        let feedback = if syncing {
            "Syncing workouts…".to_string()
        } else if let Some(ref p) = st.problem {
            p.clone()
        } else if let Some(ref n) = st.note {
            n.clone()
        } else if let Some(ref r) = st.status.as_ref().and_then(|s| s.last_result.clone()) {
            r.clone()
        } else if st.status.is_none() {
            "Connecting to steamtimerd…".to_string()
        } else if has_token {
            String::new()
        } else {
            "Save the receiver URL and token to enable sync.".to_string()
        };
        self.feedback_label.set_label(&feedback);
    }
}
