use std::sync::mpsc::{self, Sender as StdSender};
use std::thread;

use async_channel::Sender;
use ksni::blocking::TrayMethods;
use ksni::menu::StandardItem;
use ksni::{MenuItem, OfflineReason, ToolTip, Tray};
use steamtimer_core::limits;
use steamtimer_core::view_state::TrayLevel;
use tracing::debug;

#[derive(Debug, Clone)]
pub enum TrayCommand {
    Open,
    Recheck,
    ForceClose,
    Borrow,
    Quit,
}

enum TrayUpdate {
    Status(String, TrayLevel),
    Borrow(String, bool),
    Shutdown,
}

struct SteamTimerTray {
    tx: Sender<TrayCommand>,
    tooltip_text: String,
    level: TrayLevel,
    borrow_label: String,
    borrow_enabled: bool,
}

impl Tray for SteamTimerTray {
    fn id(&self) -> String {
        "steamtimer".to_string()
    }

    fn title(&self) -> String {
        "Steam Timer".to_string()
    }

    fn icon_name(&self) -> String {
        match self.level {
            TrayLevel::Blocked => "dialog-error-symbolic".to_string(),
            TrayLevel::Low => "dialog-warning-symbolic".to_string(),
            TrayLevel::Normal | TrayLevel::Unknown => "security-high-symbolic".to_string(),
        }
    }

    fn tool_tip(&self) -> ToolTip {
        ToolTip {
            title: "Steam Timer".to_string(),
            description: self.tooltip_text.clone(),
            ..Default::default()
        }
    }

    fn watcher_offline(&self, reason: OfflineReason) -> bool {
        debug!("StatusNotifierWatcher offline ({reason:?}); keeping tray ready for watcher startup");
        true
    }

    fn activate(&mut self, _x: i32, _y: i32) {
        let _ = self.tx.try_send(TrayCommand::Open);
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        vec![
            StandardItem {
                label: "Open".into(),
                activate: Box::new(|this: &mut Self| {
                    let _ = this.tx.try_send(TrayCommand::Open);
                }),
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: "Recheck now".into(),
                activate: Box::new(|this: &mut Self| {
                    let _ = this.tx.try_send(TrayCommand::Recheck);
                }),
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: "Force close Steam + games".into(),
                activate: Box::new(|this: &mut Self| {
                    let _ = this.tx.try_send(TrayCommand::ForceClose);
                }),
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
            StandardItem {
                label: self.borrow_label.clone(),
                enabled: self.borrow_enabled,
                activate: Box::new(|this: &mut Self| {
                    let _ = this.tx.try_send(TrayCommand::Borrow);
                }),
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
            StandardItem {
                label: "Quit tray (stops notifications)".into(),
                activate: Box::new(|this: &mut Self| {
                    let _ = this.tx.try_send(TrayCommand::Quit);
                }),
                ..Default::default()
            }
            .into(),
        ]
    }
}

/// Non-blocking tray controller: owns a dedicated background thread for D-Bus
/// registration and updates so the GTK main thread never blocks, and uses
/// `assume_sni_available(true)` so a late-starting Waybar/tray automatically
/// picks up the Steam Timer icon when `StatusNotifierWatcher` appears.
pub struct TrayController {
    update_tx: StdSender<TrayUpdate>,
    last_text: String,
    last_level: Option<TrayLevel>,
    last_borrow_label: String,
    last_borrow_enabled: bool,
}

impl TrayController {
    pub fn spawn(cmd_tx: Sender<TrayCommand>) -> Self {
        let (update_tx, update_rx) = mpsc::channel::<TrayUpdate>();
        thread::spawn(move || {
            let tray = SteamTimerTray {
                tx: cmd_tx,
                tooltip_text: "Steam Timer — waiting for the service".into(),
                level: TrayLevel::Unknown,
                borrow_label: limits::borrow_button_text(0),
                borrow_enabled: false,
            };
            let handle = match tray.assume_sni_available(true).spawn() {
                Ok(h) => Some(h),
                Err(err) => {
                    debug!("StatusNotifierItem tray unavailable (continuing without tray): {err}");
                    None
                }
            };

            while let Ok(msg) = update_rx.recv() {
                match msg {
                    TrayUpdate::Status(text, level) => {
                        if let Some(ref h) = handle {
                            h.update(move |t| {
                                t.tooltip_text = text;
                                t.level = level;
                            });
                        }
                    }
                    TrayUpdate::Borrow(label, enabled) => {
                        if let Some(ref h) = handle {
                            h.update(move |t| {
                                t.borrow_label = label;
                                t.borrow_enabled = enabled;
                            });
                        }
                    }
                    TrayUpdate::Shutdown => {
                        if let Some(ref h) = handle {
                            h.shutdown();
                        }
                        break;
                    }
                }
            }
        });

        Self {
            update_tx,
            last_text: String::new(),
            last_level: None,
            last_borrow_label: limits::borrow_button_text(0),
            last_borrow_enabled: false,
        }
    }

    pub fn update_status(&mut self, text: &str, level: TrayLevel) {
        if self.last_text == text && self.last_level == Some(level) {
            return;
        }
        self.last_text = text.to_string();
        self.last_level = Some(level);
        let _ = self
            .update_tx
            .send(TrayUpdate::Status(self.last_text.clone(), level));
    }

    pub fn update_borrow(&mut self, borrow_count: i32, enabled: bool) {
        let label = limits::borrow_button_text(borrow_count);
        if self.last_borrow_label == label && self.last_borrow_enabled == enabled {
            return;
        }
        self.last_borrow_label = label.clone();
        self.last_borrow_enabled = enabled;
        let _ = self.update_tx.send(TrayUpdate::Borrow(label, enabled));
    }

    pub fn shutdown(&self) {
        let _ = self.update_tx.send(TrayUpdate::Shutdown);
    }
}
