mod ipc_client;
mod notifier;
mod tray;
mod window;
mod workout_view;

use std::cell::RefCell;
use std::path::{Path, PathBuf};
use std::rc::Rc;

use clap::{Parser, Subcommand};
use gtk4::prelude::*;
use libadwaita as adw;
use serde_json::json;
use steamtimer_core::clock::PolicyClock;
use steamtimer_core::limits::DEFAULT_SOCKET_PATH;
use steamtimer_core::protocol::IpcRequest;
use steamtimer_core::view_state::{
    TimerViewState, TrayLevel, describe_tray, format_duration, format_hm_floor,
};

use crate::ipc_client::{ACTION_TIMEOUT, DEFAULT_TIMEOUT, send_request};
use crate::window::MainUi;

#[derive(Debug, Parser)]
#[command(name = "steamtimer", about = "Steam Timer desktop app & CLI for Linux")]
struct Cli {
    /// Unix socket path for steamtimerd
    #[arg(long, env = "STEAMTIMER_SOCKET", default_value = DEFAULT_SOCKET_PATH)]
    socket: PathBuf,

    /// Start hidden in background/tray (for login autostart)
    #[arg(long)]
    hidden: bool,

    #[command(subcommand)]
    command: Option<Command>,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Print current timer status
    Status {
        /// Output raw JSON response from steamtimerd
        #[arg(long)]
        json: bool,
        /// Output Waybar custom module JSON
        #[arg(long)]
        waybar: bool,
    },
    /// Ask steamtimerd to recheck running processes
    Recheck,
    /// Borrow 10m from tomorrow
    Borrow,
    /// Force-close Steam + games immediately
    Kill,
}

fn main() -> std::process::ExitCode {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("warn")),
        )
        .init();

    let cli = Cli::parse();
    if let Some(cmd) = cli.command {
        return run_cli(&cli.socket, cmd);
    }

    run_gui(cli.socket, cli.hidden)
}

fn run_cli(socket: &Path, cmd: Command) -> std::process::ExitCode {
    match cmd {
        Command::Status { json: raw_json, waybar } => {
            let reply = send_request(
                socket,
                &IpcRequest {
                    cmd: "status".into(),
                    ..Default::default()
                },
                DEFAULT_TIMEOUT,
            );
            let Some(resp) = reply.response else {
                if waybar {
                    println!(
                        "{}",
                        json!({
                            "text": "Steam: offline",
                            "tooltip": reply.problem,
                            "class": "unknown"
                        })
                    );
                    return std::process::ExitCode::SUCCESS;
                }
                eprintln!("{}", reply.problem);
                return std::process::ExitCode::FAILURE;
            };
            if raw_json {
                println!(
                    "{}",
                    serde_json::to_string_pretty(&resp).unwrap_or_else(|_| "{}".into())
                );
                return std::process::ExitCode::SUCCESS;
            }
            let mut vs = TimerViewState::default();
            vs.update(resp.clone());
            let now = PolicyClock::now();
            let (summary, level) = describe_tray(&vs, now);
            if waybar {
                let class = match level {
                    TrayLevel::Blocked => "blocked",
                    TrayLevel::Low => "low",
                    TrayLevel::Normal => "normal",
                    TrayLevel::Unknown => "unknown",
                };
                let tooltip = format!(
                    "{summary}\nDaily left: {}\nDota left: {}",
                    format_hm_floor(resp.remaining_seconds),
                    format_duration(resp.dota_remaining_seconds),
                );
                println!(
                    "{}",
                    json!({
                        "text": summary,
                        "tooltip": tooltip,
                        "class": class
                    })
                );
                return std::process::ExitCode::SUCCESS;
            }
            println!("{summary}");
            println!(
                "Daily: {} left of {} (used {})",
                format_hm_floor(resp.remaining_seconds),
                format_hm_floor(resp.budget_seconds as f64),
                format_hm_floor(resp.used_seconds),
            );
            println!(
                "Dota 2: {} left of {} ({})",
                format_duration(resp.dota_remaining_seconds),
                format_duration(resp.dota_budget_seconds),
                if resp.dota_forbidden {
                    "forbidden Tue/Thu"
                } else if resp.dota_weekend_budget {
                    "weekend budget"
                } else {
                    "weekday workout budget"
                }
            );
            std::process::ExitCode::SUCCESS
        }
        Command::Recheck => run_action_cli(socket, "recheck", DEFAULT_TIMEOUT),
        Command::Borrow => run_action_cli(socket, "borrow", ACTION_TIMEOUT),
        Command::Kill => run_action_cli(socket, "kill", ACTION_TIMEOUT),
    }
}

fn run_action_cli(
    socket: &Path,
    cmd: &str,
    timeout: std::time::Duration,
) -> std::process::ExitCode {
    let request_id = if matches!(cmd, "borrow" | "kill") {
        Some(uuid::Uuid::new_v4().simple().to_string())
    } else {
        None
    };
    let reply = send_request(
        socket,
        &IpcRequest {
            cmd: cmd.into(),
            request_id,
            ..Default::default()
        },
        timeout,
    );
    match reply.response {
        Some(resp) if resp.ok => {
            if !resp.action_note.is_empty() {
                println!("{}", resp.action_note);
            } else {
                println!("OK");
            }
            std::process::ExitCode::SUCCESS
        }
        Some(resp) => {
            eprintln!("{}", resp.error);
            std::process::ExitCode::FAILURE
        }
        None => {
            eprintln!("{}", reply.problem);
            std::process::ExitCode::FAILURE
        }
    }
}

fn run_gui(socket_path: PathBuf, start_hidden: bool) -> std::process::ExitCode {
    let app = adw::Application::builder()
        .application_id("dev.araoz.SteamTimer")
        .build();

    app.connect_startup(|_| {
        let provider = gtk4::CssProvider::new();
        provider.load_from_string(include_str!("style.css"));
        if let Some(display) = gtk4::gdk::Display::default() {
            gtk4::style_context_add_provider_for_display(
                &display,
                &provider,
                gtk4::STYLE_PROVIDER_PRIORITY_APPLICATION,
            );
        }
    });

    let ui_cell: Rc<RefCell<Option<MainUi>>> = Rc::new(RefCell::new(None));
    let first_activate = Rc::new(RefCell::new(true));

    app.connect_activate(move |app| {
        let mut ui_borrow = ui_cell.borrow_mut();
        let is_first = {
            let mut f = first_activate.borrow_mut();
            let was = *f;
            *f = false;
            was
        };

        let ui = ui_borrow.get_or_insert_with(|| MainUi::build(app, socket_path.clone()));
        if !(is_first && start_hidden) {
            ui.show_main();
        }
    });

    // Pass empty args to GTK since clap already parsed CLI arguments.
    let exit_code = app.run_with_args::<&str>(&[]);
    if exit_code.value() == 0 {
        std::process::ExitCode::SUCCESS
    } else {
        std::process::ExitCode::FAILURE
    }
}
