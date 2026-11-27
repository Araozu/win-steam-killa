use std::collections::{HashMap, HashSet};
use std::fs;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::path::Path;
use std::thread;
use std::time::{Duration, Instant};

use steamtimer_core::process::{
    DetailedSnapshot, GameSight, PlayProcesses, is_excluded_from_kill, is_ignored_play_process,
};
use steamtimer_core::state::GameRecord;
use tracing::{debug, warn};

const FLATPAK_STEAM_CGROUP_MARKER: &str = "app-flatpak-com.valvesoftware.Steam-";

#[derive(Debug, Clone)]
struct ProcInfo {
    pid: i32,
    ppid: i32,
    uid: u32,
    starttime: u64,
    comm: String,
    exe: String,
    cmdline: Vec<String>,
    cgroup: String,
    steam_env: bool,
    flatpak_steam_env: bool,
}

impl ProcInfo {
    fn exe_basename(&self) -> &str {
        if !self.exe.is_empty() {
            Path::new(&self.exe)
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or(&self.comm)
        } else {
            &self.comm
        }
    }

    fn is_wine_loader(&self) -> bool {
        let base = self.exe_basename();
        base.eq_ignore_ascii_case("wine64-preloader")
            || base.eq_ignore_ascii_case("wine-preloader")
            || base.eq_ignore_ascii_case("wine")
            || base.eq_ignore_ascii_case("wine64")
    }

    /// Unified executable identity `(name, path)` used for display, Dota classification,
    /// game recording, and orphan matching (handles both native binaries and Wine/Proton).
    fn effective_identity(&self) -> (String, String) {
        if self.is_wine_loader()
            && let Some(first) = self.cmdline.first()
            && !first.is_empty()
        {
            let fname = first.rsplit(['/', '\\']).next().unwrap_or(first);
            if !fname.is_empty() {
                return (fname.to_string(), first.clone());
            }
        }
        (self.exe_basename().to_string(), self.exe.clone())
    }

    fn display_name(&self) -> String {
        self.effective_identity().0
    }

    fn effective_path(&self) -> String {
        self.effective_identity().1
    }

    fn in_flatpak_steam_scope(&self) -> bool {
        self.cgroup.contains(FLATPAK_STEAM_CGROUP_MARKER) || self.flatpak_steam_env
    }

    /// Verified Steam client process (requires executable path or Flatpak Steam sandbox
    /// verification rather than matching an arbitrary user binary named `steam`).
    fn is_client(&self) -> bool {
        let base_l = self.exe_basename().to_ascii_lowercase();
        let exe_l = self.exe.to_ascii_lowercase();
        let verified_steam_path = exe_l.contains("/steam/")
            || exe_l.contains("/.steam/")
            || exe_l.contains("com.valvesoftware.steam")
            || exe_l.contains("/ubuntu12_32/steam")
            || exe_l.contains("/ubuntu12_64/steamwebhelper")
            || self.in_flatpak_steam_scope();

        match base_l.as_str() {
            "steam" | "steamwebhelper" | "gameoverlayui" => verified_steam_path,
            _ => false,
        }
    }

    fn is_steam(&self) -> bool {
        if self.is_client() {
            return true;
        }
        let base_l = self.exe_basename().to_ascii_lowercase();
        if base_l == "steamservice" {
            let exe_l = self.exe.to_ascii_lowercase();
            return exe_l.contains("/steam/")
                || exe_l.contains("/.steam/")
                || exe_l.contains("com.valvesoftware.steam")
                || self.in_flatpak_steam_scope();
        }
        false
    }

    /// Unified Dota 2 classification using `effective_identity()` so native Linux `dota2`,
    /// `dota.sh`, and Wine/Proton `dota2.exe` are classified identically everywhere.
    fn is_dota(&self, in_steam_context: bool) -> bool {
        if self.is_client() {
            return false;
        }
        let (name, path) = self.effective_identity();
        let name_l = name.to_ascii_lowercase();
        let path_l = path.to_ascii_lowercase();

        let is_dota_name = matches!(name_l.as_str(), "dota2" | "dota2.exe" | "dota.sh");
        if !is_dota_name {
            return false;
        }
        // Require verified Dota path or Steam context (tree, Flatpak scope, or Steam env).
        path_l.contains("dota 2 beta")
            || path_l.contains("steamapps/common/")
            || path_l.contains("steamapps\\common\\")
            || in_steam_context
            || self.in_flatpak_steam_scope()
            || self.steam_env
    }

    fn is_steam_game_path(&self) -> bool {
        let path_l = self.effective_path().to_ascii_lowercase();
        path_l.contains("steamapps/common/") || path_l.contains("steamapps\\common\\")
    }
}

/// Compare native Linux paths case-sensitively; compare Windows/Wine backslash paths
/// case-insensitively.
fn paths_match(live_path: &str, recorded_path: &str) -> bool {
    if live_path.is_empty() || recorded_path.is_empty() {
        return false;
    }
    if live_path.contains('\\') || recorded_path.contains('\\') {
        live_path.eq_ignore_ascii_case(recorded_path)
    } else {
        live_path == recorded_path
    }
}

/// Verified `pidfd` handle that prevents signaling a recycled PID after the grace period.
struct VerifiedTarget {
    pid: i32,
    pidfd: OwnedFd,
}

impl VerifiedTarget {
    fn open_verified(pid: i32, expected_starttime: u64) -> Option<Self> {
        let fd = unsafe { libc::syscall(libc::SYS_pidfd_open, pid as libc::pid_t, 0u32) };
        if fd < 0 {
            return None;
        }
        let owned = unsafe { OwnedFd::from_raw_fd(fd as i32) };
        // Re-verify /proc/<pid>/stat starttime AFTER opening pidfd to eliminate the
        // TOCTOU race between snapshot and pidfd_open.
        let current_start = read_proc_starttime(pid)?;
        if current_start != expected_starttime {
            return None;
        }
        Some(Self { pid, pidfd: owned })
    }

    fn send_signal(&self, sig: i32) -> i32 {
        unsafe {
            libc::syscall(
                libc::SYS_pidfd_send_signal,
                self.pidfd.as_raw_fd(),
                sig,
                std::ptr::null::<libc::siginfo_t>(),
                0u32,
            ) as i32
        }
    }

    fn is_alive(&self) -> bool {
        // Signal 0 checks liveness on the exact process referenced by pidfd.
        self.send_signal(0) == 0
    }
}

fn read_proc_starttime(pid: i32) -> Option<u64> {
    let stat = fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    let rparen = stat.rfind(')')?;
    let rest = stat.get(rparen + 2..)?;
    // Field 3 is state (index 0 in `rest`); field 22 is starttime (index 19 in `rest`).
    rest.split_whitespace().nth(19)?.parse::<u64>().ok()
}

pub struct LinuxPlayProcesses {
    target_uid: Option<u32>,
}

impl LinuxPlayProcesses {
    pub fn new(target_uid: Option<u32>) -> Self {
        Self { target_uid }
    }

    fn scan_proc(&self) -> Vec<ProcInfo> {
        let mut procs = Vec::new();
        let Ok(entries) = fs::read_dir("/proc") else {
            return procs;
        };
        let self_pid = std::process::id() as i32;

        for entry in entries.flatten() {
            let fname = entry.file_name();
            let Some(name_str) = fname.to_str() else {
                continue;
            };
            let Ok(pid) = name_str.parse::<i32>() else {
                continue;
            };
            if pid <= 1 || pid == self_pid {
                continue;
            }
            if let Some(info) = Self::read_proc(pid) {
                // Never touch root processes, and when scoped to a target UID ignore all other UIDs.
                if info.uid == 0 {
                    continue;
                }
                if let Some(expected_uid) = self.target_uid
                    && info.uid != expected_uid
                {
                    continue;
                }
                procs.push(info);
            }
        }
        procs
    }

    fn read_proc(pid: i32) -> Option<ProcInfo> {
        let base = format!("/proc/{pid}");
        let stat = fs::read_to_string(format!("{base}/stat")).ok()?;
        let lparen = stat.find('(')?;
        let rparen = stat.rfind(')')?;
        if rparen <= lparen {
            return None;
        }
        let stat_comm = &stat[lparen + 1..rparen];
        let rest = stat.get(rparen + 2..)?;
        let fields: Vec<&str> = rest.split_whitespace().collect();
        if fields.len() <= 19 {
            return None;
        }
        let ppid = fields[1].parse::<i32>().ok()?;
        let starttime = fields[19].parse::<u64>().ok()?;

        let status = fs::read_to_string(format!("{base}/status")).ok()?;
        let uid = status
            .lines()
            .find_map(|line| line.strip_prefix("Uid:"))
            .and_then(|vals| vals.split_whitespace().next())
            .and_then(|u| u.parse::<u32>().ok())?;

        let comm = fs::read_to_string(format!("{base}/comm"))
            .map(|s| s.trim_end_matches(['\n', '\r']).to_string())
            .unwrap_or_else(|_| stat_comm.to_string());

        let exe = fs::read_link(format!("{base}/exe"))
            .ok()
            .and_then(|p| {
                p.to_str()
                    .map(|s| s.trim_end_matches(" (deleted)").to_string())
            })
            .unwrap_or_default();

        let cmdline = fs::read(format!("{base}/cmdline"))
            .map(|bytes| {
                bytes
                    .split(|&b| b == 0)
                    .filter(|s| !s.is_empty())
                    .map(|s| String::from_utf8_lossy(s).into_owned())
                    .collect()
            })
            .unwrap_or_default();

        let cgroup = fs::read_to_string(format!("{base}/cgroup")).unwrap_or_default();

        let mut steam_env = false;
        let mut flatpak_steam_env = false;
        if let Ok(env_bytes) = fs::read(format!("{base}/environ")) {
            for entry in env_bytes.split(|&b| b == 0) {
                if entry.starts_with(b"SteamAppId=")
                    || entry.starts_with(b"SteamGameId=")
                    || entry.starts_with(b"STEAM_COMPAT_APP_ID=")
                {
                    steam_env = true;
                } else if entry == b"FLATPAK_ID=com.valvesoftware.Steam" {
                    flatpak_steam_env = true;
                }
            }
        }

        Some(ProcInfo {
            pid,
            ppid,
            uid,
            starttime,
            comm,
            exe,
            cmdline,
            cgroup,
            steam_env,
            flatpak_steam_env,
        })
    }

    fn build_parent_map(procs: &[ProcInfo]) -> HashMap<i32, i32> {
        procs.iter().map(|p| (p.pid, p.ppid)).collect()
    }

    fn has_ancestor(pid: i32, roots: &HashSet<i32>, parent_of: &HashMap<i32, i32>) -> bool {
        let mut seen = HashSet::new();
        let mut cur = pid;
        while let Some(&parent) = parent_of.get(&cur) {
            if parent <= 0 || !seen.insert(cur) {
                break;
            }
            if roots.contains(&parent) {
                return true;
            }
            cur = parent;
        }
        false
    }

    fn depth(pid: i32, parent_of: &HashMap<i32, i32>) -> usize {
        let mut d = 0usize;
        let mut cur = pid;
        while let Some(&parent) = parent_of.get(&cur) {
            if parent <= 0 || d >= 64 {
                break;
            }
            d += 1;
            cur = parent;
        }
        d
    }

    fn known_path_map(history: &[GameRecord]) -> HashMap<String, Vec<String>> {
        let mut map: HashMap<String, Vec<String>> = HashMap::new();
        for g in history {
            if g.name.trim().is_empty() || g.path.trim().is_empty() {
                continue;
            }
            let entry = map.entry(g.name.to_ascii_lowercase()).or_default();
            if !entry.iter().any(|p| paths_match(p, &g.path)) {
                entry.push(g.path.clone());
            }
        }
        map
    }

    /// Identical orphan identity matching used by BOTH snapshot detection AND kill enforcement
    /// so a detached process can never accrue time while escaping termination.
    fn matches_known_orphan(
        proc_info: &ProcInfo,
        extra_names: &HashSet<String>,
        known_paths: &HashMap<String, Vec<String>>,
    ) -> bool {
        let (name, live_path) = proc_info.effective_identity();
        if name.is_empty() || is_ignored_play_process(&name) {
            return false;
        }
        let key = name.to_ascii_lowercase();
        if !extra_names.contains(&key) {
            return false;
        }
        let Some(recorded_paths) = known_paths.get(&key) else {
            // Require a recorded verified path so an unverified bare name can neither
            // poison detection nor kill unrelated processes.
            return false;
        };
        recorded_paths
            .iter()
            .any(|recorded| paths_match(&live_path, recorded))
    }

    fn snapshot_from_procs(
        all: &[ProcInfo],
        seen_games: &mut Vec<String>,
        history: &[GameRecord],
    ) -> DetailedSnapshot {
        let parent_of = Self::build_parent_map(all);

        let mut client_roots: HashSet<i32> = HashSet::new();
        let mut clients = Vec::new();
        for p in all {
            if p.is_client() {
                client_roots.insert(p.pid);
                if clients.len() < 20 {
                    clients.push(format!("{} ({})", p.exe_basename(), p.pid));
                }
            }
        }

        let mut dota_alive = false;
        let mut games = Vec::new();
        for p in all {
            let in_steam_tree = Self::has_ancestor(p.pid, &client_roots, &parent_of);
            let in_flatpak_steam = p.in_flatpak_steam_scope();
            let in_steam_context = in_steam_tree || in_flatpak_steam || p.steam_env;

            if p.is_dota(in_steam_context) {
                dota_alive = true;
            }
            if client_roots.contains(&p.pid) || p.is_steam() {
                continue;
            }
            if !in_steam_context && !p.is_dota(false) {
                continue;
            }

            let (name, path) = p.effective_identity();
            if name.is_empty() || is_ignored_play_process(&name) {
                continue;
            }
            // In Flatpak scopes (without direct Steam tree ancestry), only record actual
            // game processes (Dota, steamapps/common path, or SteamAppId/SteamGameId env).
            if in_flatpak_steam
                && !in_steam_tree
                && !p.is_dota(true)
                && !p.is_steam_game_path()
                && !p.steam_env
            {
                continue;
            }
            games.push(GameSight {
                name,
                pid: p.pid,
                path,
            });
        }

        for g in &games {
            if !seen_games
                .iter()
                .any(|existing| existing.eq_ignore_ascii_case(&g.name))
            {
                seen_games.push(g.name.clone());
            }
        }

        let extra_names: HashSet<String> = seen_games
            .iter()
            .filter(|n| !is_ignored_play_process(n))
            .map(|n| n.to_ascii_lowercase())
            .collect();
        let known_paths = Self::known_path_map(history);

        let mut orphan_counts: HashMap<String, (String, usize)> = HashMap::new();
        if !extra_names.is_empty() {
            for p in all {
                if client_roots.contains(&p.pid) || p.is_steam() {
                    continue;
                }
                if games.iter().any(|g| g.pid == p.pid) {
                    continue;
                }
                if Self::matches_known_orphan(p, &extra_names, &known_paths) {
                    let name = p.display_name();
                    let entry = orphan_counts
                        .entry(name.to_ascii_lowercase())
                        .or_insert_with(|| (name, 0));
                    entry.1 += 1;
                }
            }
        }
        let matched_orphans: Vec<String> = orphan_counts
            .into_values()
            .take(20)
            .map(|(name, count)| format!("{name} ({count} alive)"))
            .collect();

        let playing = !client_roots.is_empty()
            || !games.is_empty()
            || dota_alive
            || !matched_orphans.is_empty();

        DetailedSnapshot {
            playing,
            dota_running: dota_alive,
            games,
            clients,
            matched_orphans,
        }
    }

    fn select_play_kill_targets(
        all: &[ProcInfo],
        seen_games: &[String],
        history: &[GameRecord],
    ) -> Vec<(i32, u64)> {
        let parent_of = Self::build_parent_map(all);

        let steam_roots: HashSet<i32> = all
            .iter()
            .filter(|p| p.is_steam() || p.is_dota(p.in_flatpak_steam_scope() || p.steam_env))
            .map(|p| p.pid)
            .collect();

        let extra: HashSet<String> = seen_games
            .iter()
            .filter(|n| !is_ignored_play_process(n))
            .map(|n| n.to_ascii_lowercase())
            .collect();
        let known_paths = Self::known_path_map(history);

        let mut targets = Vec::new();
        for p in all {
            let name = p.display_name();
            // Explicitly excluded processes (e.g. syncthing) are NEVER signaled.
            if is_excluded_from_kill(&name) || is_excluded_from_kill(&p.comm) {
                continue;
            }
            let in_steam_tree = Self::has_ancestor(p.pid, &steam_roots, &parent_of);
            let is_portal_or_env_game = p.steam_env && !is_ignored_play_process(&name);
            if steam_roots.contains(&p.pid)
                || p.in_flatpak_steam_scope()
                || in_steam_tree
                || is_portal_or_env_game
                || Self::matches_known_orphan(p, &extra, &known_paths)
            {
                targets.push((p.pid, p.starttime));
            }
        }
        targets.sort_by(|a, b| Self::depth(b.0, &parent_of).cmp(&Self::depth(a.0, &parent_of)));
        targets
    }

    fn select_dota_kill_targets(all: &[ProcInfo]) -> Vec<(i32, u64)> {
        let parent_of = Self::build_parent_map(all);
        let client_roots: HashSet<i32> = all
            .iter()
            .filter(|p| p.is_client())
            .map(|p| p.pid)
            .collect();

        let dota_roots: HashSet<i32> = all
            .iter()
            .filter(|p| {
                let in_ctx = Self::has_ancestor(p.pid, &client_roots, &parent_of)
                    || p.in_flatpak_steam_scope()
                    || p.steam_env;
                p.is_dota(in_ctx)
            })
            .map(|p| p.pid)
            .collect();
        if dota_roots.is_empty() {
            return Vec::new();
        }

        let mut targets = Vec::new();
        for p in all {
            let name = p.display_name();
            if p.is_client() || is_excluded_from_kill(&name) || is_excluded_from_kill(&p.comm) {
                continue;
            }
            if dota_roots.contains(&p.pid) || Self::has_ancestor(p.pid, &dota_roots, &parent_of) {
                targets.push((p.pid, p.starttime));
            }
        }
        targets.sort_by(|a, b| Self::depth(b.0, &parent_of).cmp(&Self::depth(a.0, &parent_of)));
        targets
    }

    fn kill_verified_targets(selected: Vec<(i32, u64)>) -> usize {
        if selected.is_empty() {
            return 0;
        }

        let mut seen = HashSet::new();
        let mut verified = Vec::new();
        for (pid, starttime) in selected {
            if !seen.insert(pid) {
                continue;
            }
            if let Some(target) = VerifiedTarget::open_verified(pid, starttime) {
                verified.push(target);
            }
        }
        if verified.is_empty() {
            return 0;
        }

        for target in &verified {
            let _ = target.send_signal(libc::SIGTERM);
        }

        let deadline = Instant::now() + Duration::from_secs(4);
        while Instant::now() < deadline {
            if verified.iter().all(|t| !t.is_alive()) {
                break;
            }
            thread::sleep(Duration::from_millis(100));
        }

        let mut killed = 0usize;
        for target in &verified {
            if target.is_alive() {
                if target.send_signal(libc::SIGKILL) == 0 {
                    killed += 1;
                } else {
                    debug!("pidfd SIGKILL failed for pid {}", target.pid);
                }
            } else {
                killed += 1;
            }
        }
        killed
    }
}

impl PlayProcesses for LinuxPlayProcesses {
    fn snapshot(&self, seen_games: &mut Vec<String>, history: &[GameRecord]) -> DetailedSnapshot {
        let all = self.scan_proc();
        Self::snapshot_from_procs(&all, seen_games, history)
    }

    fn kill_play_set(&self, seen_games: &[String], history: &[GameRecord]) -> usize {
        let all = self.scan_proc();
        let selected = Self::select_play_kill_targets(&all, seen_games, history);
        let killed = Self::kill_verified_targets(selected);
        if killed > 0 {
            debug!("kill_play_set terminated {killed} process(es)");
        }
        killed
    }

    fn kill_dota(&self) -> usize {
        let all = self.scan_proc();
        let selected = Self::select_dota_kill_targets(&all);
        let had_roots = !selected.is_empty();
        let killed = Self::kill_verified_targets(selected);
        if killed == 0 && had_roots {
            warn!("kill_dota found Dota roots but terminated 0 processes");
        }
        killed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn proc_fixture(pid: i32, ppid: i32, comm: &str, exe: &str) -> ProcInfo {
        ProcInfo {
            pid,
            ppid,
            uid: 1000,
            starttime: 1000 + pid as u64,
            comm: comm.into(),
            exe: exe.into(),
            cmdline: vec![exe.into()],
            cgroup: String::new(),
            steam_env: false,
            flatpak_steam_env: false,
        }
    }

    #[test]
    fn syncthing_in_steam_subtree_or_flatpak_scope_is_never_killed() {
        let mut steam = proc_fixture(100, 1, "steam", "/home/u/.local/share/Steam/ubuntu12_32/steam");
        steam.cgroup = "0::/user.slice/app-flatpak-com.valvesoftware.Steam-1.scope".into();
        let mut syncthing = proc_fixture(101, 100, "syncthing", "/usr/bin/syncthing");
        syncthing.cgroup = steam.cgroup.clone();

        let targets = LinuxPlayProcesses::select_play_kill_targets(&[steam, syncthing], &[], &[]);
        assert_eq!(targets.len(), 1);
        assert_eq!(targets[0].0, 100);
    }

    #[test]
    fn wine_proton_dota2_is_classified_detected_and_killed_as_dota() {
        let steam = proc_fixture(100, 1, "steam", "/home/u/.local/share/Steam/ubuntu12_32/steam");
        let mut wine_dota = proc_fixture(200, 100, "wine64-preloade", "/usr/bin/wine64-preloader");
        wine_dota.cmdline = vec![
            r"Z:\home\u\.local\share\Steam\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe"
                .into(),
        ];
        wine_dota.steam_env = true;

        let mut seen = Vec::new();
        let snap = LinuxPlayProcesses::snapshot_from_procs(&[steam.clone(), wine_dota.clone()], &mut seen, &[]);
        assert!(snap.playing);
        assert!(snap.dota_running);
        assert!(snap.games.iter().any(|g| g.name == "dota2.exe"));

        let dota_targets = LinuxPlayProcesses::select_dota_kill_targets(&[steam, wine_dota]);
        assert_eq!(dota_targets.len(), 1);
        assert_eq!(dota_targets[0].0, 200);
    }

    #[test]
    fn reparented_native_game_uses_consistent_identity_for_detection_and_kill() {
        let game = proc_fixture(
            300,
            1, // reparented to PID 1 after Steam exited
            "factorio",
            "/home/u/.local/share/Steam/steamapps/common/Factorio/bin/x64/factorio",
        );

        // Without a recorded path in history, neither detection nor kill fires.
        let mut seen = vec!["factorio".into()];
        let snap_unverified =
            LinuxPlayProcesses::snapshot_from_procs(std::slice::from_ref(&game), &mut seen, &[]);
        assert!(!snap_unverified.playing);
        let kill_unverified =
            LinuxPlayProcesses::select_play_kill_targets(std::slice::from_ref(&game), &seen, &[]);
        assert!(kill_unverified.is_empty());

        // With recorded verified path, BOTH detection and kill match consistently.
        let history = vec![GameRecord {
            name: "factorio".into(),
            path: "/home/u/.local/share/Steam/steamapps/common/Factorio/bin/x64/factorio".into(),
            total_seconds: 0.0,
            last_seen_utc: None,
        }];
        let snap_verified = LinuxPlayProcesses::snapshot_from_procs(
            std::slice::from_ref(&game),
            &mut seen,
            &history,
        );
        assert!(snap_verified.playing);
        assert_eq!(snap_verified.matched_orphans.len(), 1);
        let kill_verified = LinuxPlayProcesses::select_play_kill_targets(&[game], &seen, &history);
        assert_eq!(kill_verified.len(), 1);
        assert_eq!(kill_verified[0].0, 300);
    }

    #[test]
    fn flatpak_portal_child_without_steam_scope_detected_and_killed_via_steam_env() {
        // Reparented under flatpak-portal with no Steam cgroup scope, only SteamAppId env.
        let mut portal_game = proc_fixture(
            400,
            50,
            "celeste",
            "/home/u/.var/app/com.valvesoftware.Steam/data/Steam/steamapps/common/Celeste/celeste",
        );
        portal_game.steam_env = true;

        let mut seen = Vec::new();
        let snap = LinuxPlayProcesses::snapshot_from_procs(
            std::slice::from_ref(&portal_game),
            &mut seen,
            &[],
        );
        assert!(snap.playing);
        assert_eq!(snap.games.len(), 1);
        assert_eq!(snap.games[0].name, "celeste");

        let targets = LinuxPlayProcesses::select_play_kill_targets(&[portal_game], &seen, &[]);
        assert_eq!(targets.len(), 1);
        assert_eq!(targets[0].0, 400);
    }

    #[test]
    fn unrelated_binary_named_steam_is_ignored() {
        let decoy = proc_fixture(500, 1, "steam", "/usr/local/bin/my-tools/steam");
        let mut seen = Vec::new();
        let snap =
            LinuxPlayProcesses::snapshot_from_procs(std::slice::from_ref(&decoy), &mut seen, &[]);
        assert!(!snap.playing);
        let targets = LinuxPlayProcesses::select_play_kill_targets(&[decoy], &[], &[]);
        assert!(targets.is_empty());
    }
}
