#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET_USER="${SUDO_USER:-$USER}"

# Build as the non-root user first so cargo/rustup toolchain and target ownership stay clean.
if [[ $EUID -eq 0 ]]; then
    if [[ -n "${SUDO_USER:-}" ]]; then
        echo "Building release binaries as $SUDO_USER..."
        sudo -u "$SUDO_USER" -H bash -lc "cd '$LINUX_DIR' && cargo build --workspace --release"
    else
        echo "Building release binaries..."
        (cd "$LINUX_DIR" && cargo build --workspace --release)
    fi
else
    echo "Building release binaries..."
    (cd "$LINUX_DIR" && cargo build --workspace --release)
    exec sudo -E "$0" "$@"
fi

DAEMON_BIN="$LINUX_DIR/target/release/steamtimerd"
GUI_BIN="$LINUX_DIR/target/release/steamtimer"

if [[ ! -x "$DAEMON_BIN" || ! -x "$GUI_BIN" ]]; then
    echo "Release binaries missing; aborting install." >&2
    exit 1
fi

echo "Ensuring 'steamtimer' group exists..."
if ! getent group steamtimer >/dev/null; then
    groupadd --system steamtimer
fi

ADDED_GROUP=0
if [[ "$TARGET_USER" != "root" ]] && ! id -nG "$TARGET_USER" | tr ' ' '\n' | grep -qx steamtimer; then
    usermod -aG steamtimer "$TARGET_USER"
    ADDED_GROUP=1
fi

echo "Stopping existing daemon (if running)..."
systemctl stop steamtimer-watchdog.timer 2>/dev/null || true
systemctl stop steamtimerd.service 2>/dev/null || true

echo "Installing binaries to /usr/local/bin..."
install -m 0755 "$DAEMON_BIN" /usr/local/bin/steamtimerd
install -m 0755 "$GUI_BIN" /usr/local/bin/steamtimer

echo "Configuring target user in /etc/steamtimer/env..."
install -d -m 0755 /etc/steamtimer
if [[ "$TARGET_USER" != "root" ]]; then
    if [[ -f /etc/steamtimer/env ]]; then
        grep -v '^STEAMTIMER_TARGET_USER=' /etc/steamtimer/env > /etc/steamtimer/env.tmp || true
        printf 'STEAMTIMER_TARGET_USER=%s\n' "$TARGET_USER" >> /etc/steamtimer/env.tmp
        mv /etc/steamtimer/env.tmp /etc/steamtimer/env
    else
        printf 'STEAMTIMER_TARGET_USER=%s\n' "$TARGET_USER" > /etc/steamtimer/env
    fi
    chmod 0644 /etc/steamtimer/env
fi

echo "Installing systemd units..."
install -m 0644 "$SCRIPT_DIR/steamtimerd.service" /etc/systemd/system/steamtimerd.service
install -m 0644 "$SCRIPT_DIR/steamtimer-watchdog.service" /etc/systemd/system/steamtimer-watchdog.service
install -m 0644 "$SCRIPT_DIR/steamtimer-watchdog.timer" /etc/systemd/system/steamtimer-watchdog.timer

echo "Installing desktop entries..."
install -D -m 0644 "$SCRIPT_DIR/steamtimer.desktop" /usr/share/applications/steamtimer.desktop
install -D -m 0644 "$SCRIPT_DIR/steamtimer-autostart.desktop" /etc/xdg/autostart/steamtimer.desktop

systemctl daemon-reload
systemctl enable --now steamtimerd.service
systemctl enable --now steamtimer-watchdog.timer

echo ""
echo "Done! steamtimerd is running."
if [[ $ADDED_GROUP -eq 1 ]]; then
    echo "NOTE: Added '$TARGET_USER' to group 'steamtimer'. Log out and back in (or run 'newgrp steamtimer') so the UI can talk to /run/steamtimer/steamtimer.sock."
fi
echo "Sway tip: add 'exec steamtimer --hidden' to ~/.config/sway/config to auto-start notifications/tray on login."
echo "Waybar tip: use 'steamtimer status --waybar' in a custom module."
