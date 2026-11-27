mod ipc_server;
mod linux_proc;
mod workout_sync;

use std::ffi::CString;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use clap::Parser;
use steamtimer_core::engine::{CHECK_INTERVAL_SECS, TimerEngine};
use steamtimer_core::limits::{
    DEFAULT_SOCKET_PATH, DEFAULT_STATE_FILE, DEFAULT_WORKOUT_ARCHIVE, DEFAULT_WORKOUT_CONFIG,
};
use steamtimer_core::state::StateStore;
use tracing::{error, info};

use crate::ipc_server::{IpcServer, cleanup_socket};
use crate::linux_proc::LinuxPlayProcesses;
use crate::workout_sync::{WorkoutConnectionSettings, WorkoutConnectionStore, WorkoutSyncWorker};

#[derive(Debug, Parser)]
#[command(name = "steamtimerd", about = "Self-control Steam limiter daemon for Linux")]
struct Args {
    /// Unix socket path for client IPC
    #[arg(long, env = "STEAMTIMER_SOCKET", default_value = DEFAULT_SOCKET_PATH)]
    socket: PathBuf,

    /// Path to persistent timer state JSON
    #[arg(long, env = "STEAMTIMER_STATE_FILE", default_value = DEFAULT_STATE_FILE)]
    state_file: PathBuf,

    /// Path to local workout archive JSON
    #[arg(long, env = "STEAMTIMER_WORKOUT_ARCHIVE", default_value = DEFAULT_WORKOUT_ARCHIVE)]
    workout_archive: PathBuf,

    /// Path to saved workout receiver connection JSON
    #[arg(long, env = "STEAMTIMER_WORKOUT_CONFIG", default_value = DEFAULT_WORKOUT_CONFIG)]
    workout_config: PathBuf,

    /// Group ownership applied to the Unix socket (e.g. "steamtimer")
    #[arg(long, env = "STEAMTIMER_SOCKET_GROUP", default_value = "steamtimer")]
    socket_group: String,

    /// Octal permissions mode for the Unix socket
    #[arg(long, env = "STEAMTIMER_SOCKET_MODE", default_value = "0660")]
    socket_mode: String,

    /// Restrict process detection/enforcement to this Linux username (recommended)
    #[arg(long, env = "STEAMTIMER_TARGET_USER")]
    target_user: Option<String>,

    /// Restrict process detection/enforcement to this numeric Linux UID
    #[arg(long, env = "STEAMTIMER_TARGET_UID")]
    target_uid: Option<u32>,
}

fn resolve_target_uid(args: &Args) -> Result<Option<u32>, String> {
    if let Some(uid) = args.target_uid {
        if uid == 0 {
            return Err("Refusing to target UID 0 (root).".into());
        }
        return Ok(Some(uid));
    }
    if let Some(ref user) = args.target_user
        && !user.trim().is_empty()
    {
        let c_user = CString::new(user.trim()).map_err(|_| "Invalid target_user".to_string())?;
        let uid = unsafe {
            let pw = libc::getpwnam(c_user.as_ptr());
            if pw.is_null() {
                None
            } else {
                Some((*pw).pw_uid)
            }
        }
        .ok_or_else(|| format!("Target user '{}' not found", user.trim()))?;
        if uid == 0 {
            return Err("Refusing to target root user.".into());
        }
        return Ok(Some(uid));
    }
    Ok(None)
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .init();

    let args = Args::parse();
    let socket_mode = u32::from_str_radix(args.socket_mode.trim_start_matches("0o"), 8)
        .map_err(|_| format!("Invalid octal socket mode: {}", args.socket_mode))?;
    let socket_group = if args.socket_group.trim().is_empty() {
        None
    } else {
        Some(args.socket_group.as_str())
    };
    let target_uid = resolve_target_uid(&args)?;

    info!(
        "steamtimerd starting (target_uid={:?})...",
        target_uid
    );

    let store = StateStore::new(Some(args.state_file.clone()));
    let processes = Arc::new(LinuxPlayProcesses::new(target_uid));
    let engine = match TimerEngine::new(store, processes, None) {
        Ok(e) => Arc::new(e),
        Err(err) => {
            error!("{err}");
            return Err(err.into());
        }
    };

    let workout_store = Arc::new(WorkoutConnectionStore::new(
        args.workout_config.clone(),
        WorkoutConnectionSettings::from_env(),
    ));
    let workouts = Arc::new(WorkoutSyncWorker::new(
        engine.clone(),
        workout_store,
        args.workout_archive.clone(),
    ));

    // Bind & permission IPC socket synchronously BEFORE reporting readiness/starting ticks.
    let ipc = Arc::new(IpcServer::bind(
        args.socket.clone(),
        socket_group,
        socket_mode,
        engine.clone(),
        workouts.clone(),
    )?);
    let socket_path_owned = ipc.socket_path().to_path_buf();

    let workout_task = tokio::spawn(workouts.clone().run());
    let mut ipc_task = tokio::spawn(async move { ipc.run().await });

    // Immediate tick on startup so reboot-resume kills fast if past deadline.
    {
        let eng = engine.clone();
        let _ = tokio::task::spawn_blocking(move || {
            for (title, body) in eng.tick() {
                info!("[event] {title} — {body}");
            }
        })
        .await;
    }

    let mut ticker = tokio::time::interval(Duration::from_secs(CHECK_INTERVAL_SECS as u64));
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    ticker.tick().await;

    let shutdown = shutdown_signal();
    tokio::pin!(shutdown);

    let mut exit_err: Option<Box<dyn std::error::Error>> = None;

    loop {
        tokio::select! {
            _ = &mut shutdown => {
                info!("Shutdown signal received, stopping steamtimerd.");
                break;
            }
            ipc_res = &mut ipc_task => {
                match ipc_res {
                    Ok(Ok(())) => {
                        error!("IPC server stopped unexpectedly.");
                        exit_err = Some("IPC server stopped unexpectedly".into());
                    }
                    Ok(Err(err)) => {
                        error!("IPC server exited with error: {err}");
                        exit_err = Some(err.into());
                    }
                    Err(err) => {
                        error!("IPC server task panicked: {err}");
                        exit_err = Some(err.into());
                    }
                }
                break;
            }
            _ = ticker.tick() => {
                let eng = engine.clone();
                let _ = tokio::task::spawn_blocking(move || {
                    for (title, body) in eng.tick() {
                        info!("[event] {title} — {body}");
                    }
                }).await;
            }
        }
    }

    ipc_task.abort();
    workout_task.abort();
    cleanup_socket(&socket_path_owned);
    if let Some(err) = exit_err {
        return Err(err);
    }
    Ok(())
}

async fn shutdown_signal() {
    let ctrl_c = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    #[cfg(unix)]
    let terminate = async {
        if let Ok(mut sig) =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
        {
            sig.recv().await;
        }
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {}
        _ = terminate => {}
    }
}
