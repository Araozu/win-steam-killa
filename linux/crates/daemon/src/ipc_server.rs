use std::ffi::CString;
use std::fs;
use std::io::{self, ErrorKind};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::UnixStream as StdUnixStream;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use steamtimer_core::engine::TimerEngine;
use steamtimer_core::protocol::{IpcRequest, IpcResponse};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
use tokio::net::{UnixListener, UnixStream};
use tokio::sync::Semaphore;
use tracing::{debug, info};

use crate::workout_sync::WorkoutSyncWorker;

const MAX_REQUEST_BYTES: usize = 8192;
const IO_TIMEOUT: Duration = Duration::from_secs(15);
const MAX_CONCURRENT_CONNS: usize = 16;

pub struct IpcServer {
    socket_path: PathBuf,
    listener: UnixListener,
    engine: Arc<TimerEngine>,
    workouts: Arc<WorkoutSyncWorker>,
    permits: Arc<Semaphore>,
}

impl IpcServer {
    /// Bind and permission the Unix socket synchronously at startup so systemd
    /// immediately fails if IPC cannot be established or another instance is active.
    pub fn bind(
        socket_path: PathBuf,
        socket_group: Option<&str>,
        socket_mode: u32,
        engine: Arc<TimerEngine>,
        workouts: Arc<WorkoutSyncWorker>,
    ) -> io::Result<Self> {
        if let Some(parent) = socket_path.parent() {
            fs::create_dir_all(parent)?;
        }

        if socket_path.exists() {
            // Refuse to unlink an actively-listening socket owned by another daemon instance.
            match StdUnixStream::connect(&socket_path) {
                Ok(_) => {
                    return Err(io::Error::new(
                        ErrorKind::AddrInUse,
                        format!(
                            "Another steamtimerd is already listening on {}",
                            socket_path.display()
                        ),
                    ));
                }
                Err(err)
                    if matches!(
                        err.kind(),
                        ErrorKind::ConnectionRefused | ErrorKind::NotFound
                    ) =>
                {
                    fs::remove_file(&socket_path)?;
                }
                Err(err) => {
                    return Err(io::Error::new(
                        err.kind(),
                        format!(
                            "Existing socket {} cannot be verified ({err}); refusing to unlink",
                            socket_path.display()
                        ),
                    ));
                }
            }
        }

        let listener = UnixListener::bind(&socket_path)?;

        if let Some(group_name) = socket_group {
            let gid = lookup_gid(group_name).ok_or_else(|| {
                let _ = fs::remove_file(&socket_path);
                io::Error::new(
                    ErrorKind::NotFound,
                    format!("Socket group '{group_name}' does not exist"),
                )
            })?;
            let c_path = CString::new(socket_path.to_string_lossy().as_bytes()).map_err(|e| {
                let _ = fs::remove_file(&socket_path);
                io::Error::new(ErrorKind::InvalidInput, e)
            })?;
            let rc = unsafe { libc::chown(c_path.as_ptr(), u32::MAX, gid) };
            if rc != 0 {
                let err = io::Error::last_os_error();
                let _ = fs::remove_file(&socket_path);
                return Err(io::Error::new(
                    err.kind(),
                    format!(
                        "Failed to chgrp {} to '{}' (gid {gid}): {err}",
                        socket_path.display(),
                        group_name
                    ),
                ));
            }
        }

        if let Err(err) = fs::set_permissions(&socket_path, fs::Permissions::from_mode(socket_mode))
        {
            let _ = fs::remove_file(&socket_path);
            return Err(io::Error::new(
                err.kind(),
                format!(
                    "Failed to chmod {} to {:o}: {err}",
                    socket_path.display(),
                    socket_mode
                ),
            ));
        }

        info!("Listening on Unix socket {}", socket_path.display());
        Ok(Self {
            socket_path,
            listener,
            engine,
            workouts,
            permits: Arc::new(Semaphore::new(MAX_CONCURRENT_CONNS)),
        })
    }

    pub async fn run(self: Arc<Self>) -> io::Result<()> {
        loop {
            let permit = self
                .permits
                .clone()
                .acquire_owned()
                .await
                .map_err(|_| io::Error::other("IPC concurrency semaphore closed"))?;
            let (stream, _addr) = self.listener.accept().await?;
            let server = self.clone();
            tokio::spawn(async move {
                let _permit = permit;
                server.handle_one(stream).await;
            });
        }
    }

    async fn handle_one(&self, stream: UnixStream) {
        let deadline = Instant::now() + IO_TIMEOUT;
        let _ = tokio::time::timeout(IO_TIMEOUT, self.handle_one_inner(stream, deadline)).await;
    }

    async fn handle_one_inner(&self, stream: UnixStream, deadline: Instant) {
        let (read_half, mut write_half) = stream.into_split();
        let mut reader = BufReader::new(read_half.take((MAX_REQUEST_BYTES + 1) as u64));
        let mut line = String::new();
        match reader.read_line(&mut line).await {
            Ok(0) => return,
            Ok(_) => {}
            Err(err) => {
                debug!("ipc read failed: {err}");
                return;
            }
        }
        if line.len() > MAX_REQUEST_BYTES || line.trim().is_empty() {
            return;
        }

        let req = serde_json::from_str::<IpcRequest>(line.trim()).ok();
        let resp = match req.as_ref().map(|r| r.cmd.trim()) {
            Some("workout-status") => self.workout_reply(None),
            Some("workout-save") => {
                let r = req.as_ref().unwrap();
                let err = self.workouts.save(
                    r.workout_base_url.as_deref(),
                    r.workout_api_token.as_deref(),
                );
                self.workout_reply(err)
            }
            Some("workout-sync") => self.workout_reply(self.workouts.request_sync()),
            _ => {
                let engine = self.engine.clone();
                match tokio::task::spawn_blocking(move || {
                    engine.handle_with_deadline(req.as_ref(), Some(deadline))
                })
                .await
                {
                    Ok(r) => r,
                    Err(_) => IpcResponse {
                        ok: false,
                        error: "internal engine error".into(),
                        ..IpcResponse::default()
                    },
                }
            }
        };

        if let Ok(mut encoded) = serde_json::to_string(&resp) {
            encoded.push('\n');
            let _ = write_half.write_all(encoded.as_bytes()).await;
            let _ = write_half.flush().await;
        }
    }

    fn workout_reply(&self, err: Option<String>) -> IpcResponse {
        IpcResponse {
            ok: err.is_none(),
            error: err.unwrap_or_default(),
            workout_sync: Some(self.workouts.status()),
            ..IpcResponse::default()
        }
    }

    pub fn socket_path(&self) -> &Path {
        &self.socket_path
    }
}

fn lookup_gid(group_name: &str) -> Option<u32> {
    let c_name = CString::new(group_name).ok()?;
    unsafe {
        let grp = libc::getgrnam(c_name.as_ptr());
        if grp.is_null() {
            None
        } else {
            Some((*grp).gr_gid)
        }
    }
}

pub fn cleanup_socket(path: &Path) {
    let _ = fs::remove_file(path);
}
