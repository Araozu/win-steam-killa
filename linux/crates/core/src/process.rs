use crate::state::GameRecord;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GameSight {
    pub name: String,
    pub pid: i32,
    pub path: String,
}

#[derive(Debug, Clone, Default)]
pub struct DetailedSnapshot {
    pub playing: bool,
    pub dota_running: bool,
    pub games: Vec<GameSight>,
    pub clients: Vec<String>,
    pub matched_orphans: Vec<String>,
}

/// Processes explicitly excluded from termination even if spawned from a Steam
/// tree or sandbox (matches Windows `Killer.IgnoredPlayProcesses`).
pub fn is_excluded_from_kill(name: &str) -> bool {
    let lower = name.to_ascii_lowercase();
    matches!(lower.as_str(), "syncthing" | "conhost" | "hardwareupdater")
}

/// Names ignored when recording/remembering game executables so Steam runtime,
/// sandbox wrappers, shells, and system helpers never poison orphan tracking.
pub fn is_ignored_play_process(name: &str) -> bool {
    if is_excluded_from_kill(name) {
        return true;
    }
    let lower = name.to_ascii_lowercase();
    matches!(
        lower.as_str(),
        "bash"
            | "sh"
            | "dash"
            | "zsh"
            | "bwrap"
            | "pv-adverb"
            | "pv-bwrap"
            | "srt-logger"
            | "srt-bwrap"
            | "reaper"
            | "steam.sh"
            | "steam-runtime-l"
            | "steam-runtime-launch-client"
            | "steam-runtime-launcher-service"
            | "steamwebhelper"
            | "steamwebhelper_"
            | "gameoverlayui"
            | "steamservice"
            | "steam"
            | "xdg-dbus-proxy"
            | "xdg-open"
            | "crashpad_handler"
            | "chrome_crashpad"
            | "pressure-vessel"
            | "wineserver"
            | "wine64-preloader"
            | "wine-preloader"
            | "services.exe"
            | "winedevice.exe"
            | "plugplay.exe"
            | "svchost.exe"
            | "rpcss.exe"
            | "explorer.exe"
            | "tabtip.exe"
            | "conhost.exe"
            | "start.exe"
            | "fossilize_repla"
            | "fossilize_replay"
            | "gamemoderun"
            | "mangohud"
            | "python3"
            | "python"
    )
}

pub trait PlayProcesses: Send + Sync {
    fn snapshot(&self, seen_games: &mut Vec<String>, history: &[GameRecord]) -> DetailedSnapshot;
    fn kill_play_set(&self, seen_games: &[String], history: &[GameRecord]) -> usize;
    fn kill_dota(&self) -> usize;
}
