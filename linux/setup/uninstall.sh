#!/usr/bin/env bash
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    exec sudo "$0" "$@"
fi

echo "Stopping and disabling systemd units..."
systemctl disable --now steamtimer-watchdog.timer 2>/dev/null || true
systemctl stop steamtimer-watchdog.service 2>/dev/null || true
systemctl disable --now steamtimerd.service 2>/dev/null || true

echo "Removing unit files, binaries, and desktop entries..."
rm -f /etc/systemd/system/steamtimerd.service \
      /etc/systemd/system/steamtimer-watchdog.service \
      /etc/systemd/system/steamtimer-watchdog.timer \
      /usr/local/bin/steamtimerd \
      /usr/local/bin/steamtimer \
      /usr/share/applications/steamtimer.desktop \
      /etc/xdg/autostart/steamtimer.desktop

systemctl daemon-reload

echo "Uninstalled Steam Timer. Usage history in /var/lib/steamtimer was retained."
