use std::io::{BufRead, BufReader, ErrorKind, Write};
use std::os::unix::net::UnixStream;
use std::path::Path;
use std::time::Duration;

use steamtimer_core::protocol::{IpcRequest, IpcResponse};

pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(5);
pub const ACTION_TIMEOUT: Duration = Duration::from_secs(15);

#[derive(Debug, Clone)]
pub struct IpcReply {
    pub response: Option<IpcResponse>,
    pub problem: String,
}

impl IpcReply {
    fn err(problem: impl Into<String>) -> Self {
        Self {
            response: None,
            problem: problem.into(),
        }
    }
}

pub fn send_request(socket_path: &Path, req: &IpcRequest, timeout: Duration) -> IpcReply {
    let stream = match UnixStream::connect(socket_path) {
        Ok(s) => s,
        Err(err) => {
            let msg = match err.kind() {
                ErrorKind::NotFound | ErrorKind::ConnectionRefused => {
                    "Service unreachable — is steamtimerd running?".to_string()
                }
                ErrorKind::PermissionDenied => {
                    "Permission denied connecting to steamtimerd — add your user to the 'steamtimer' group and re-login.".to_string()
                }
                _ => format!("Couldn't connect to steamtimerd: {err}"),
            };
            return IpcReply::err(msg);
        }
    };

    let _ = stream.set_read_timeout(Some(timeout));
    let _ = stream.set_write_timeout(Some(timeout));

    let Ok(mut payload) = serde_json::to_string(req) else {
        return IpcReply::err("Failed to encode request.");
    };
    payload.push('\n');

    let mut writer = match stream.try_clone() {
        Ok(w) => w,
        Err(err) => return IpcReply::err(format!("Socket error: {err}")),
    };
    if let Err(err) = writer.write_all(payload.as_bytes()).and_then(|_| writer.flush()) {
        return IpcReply::err(format!("Failed sending command to steamtimerd: {err}"));
    }

    let mut reader = BufReader::new(stream);
    let mut line = String::new();
    match reader.read_line(&mut line) {
        Ok(0) => IpcReply::err("steamtimerd closed the connection without replying."),
        Ok(_) => match serde_json::from_str::<IpcResponse>(line.trim()) {
            Ok(resp) => IpcReply {
                response: Some(resp),
                problem: String::new(),
            },
            Err(err) => IpcReply::err(format!("Unreadable reply from steamtimerd: {err}")),
        },
        Err(err) if err.kind() == ErrorKind::WouldBlock || err.kind() == ErrorKind::TimedOut => {
            IpcReply::err("Timed out waiting for steamtimerd.")
        }
        Err(err) => IpcReply::err(format!("Failed reading reply from steamtimerd: {err}")),
    }
}
