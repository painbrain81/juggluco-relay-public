#!/bin/bash
# Installs or updates juggluco-relay. Run it on the server from the uploaded folder:
#   bash install.sh
# Never touches the configuration (/opt/juggluco-relay/appsettings.json) or the data
# (/var/lib/juggluco-relay). The previous binary is kept as JugglucoRelay.prev for rolling back.
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"
DEST=/opt/juggluco-relay

# Service account, created on the first install
if ! id juggluco >/dev/null 2>&1; then
    sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin juggluco
fi

sudo install -d -o root -g juggluco -m 0750 "$DEST"

if sudo test -f "$DEST/JugglucoRelay"; then
    sudo systemctl stop juggluco-relay 2>/dev/null || true
    sudo cp -p "$DEST/JugglucoRelay" "$DEST/JugglucoRelay.prev"
fi
sudo install -o root -g juggluco -m 0750 "$SRC/JugglucoRelay" "$DEST/JugglucoRelay"

FIRST=0
if ! sudo test -f "$DEST/appsettings.json"; then
    # Holds the passwords: readable only by root and the service group
    sudo install -o root -g juggluco -m 0640 "$SRC/appsettings.server.json" "$DEST/appsettings.json"
    FIRST=1
fi

sudo install -o root -g root -m 0644 "$SRC/juggluco-relay.service" /etc/systemd/system/juggluco-relay.service
sudo systemctl daemon-reload
sudo systemctl enable juggluco-relay >/dev/null

if [ "$FIRST" = 1 ]; then
    echo
    echo "First install. Set the labels and passwords in $DEST/appsettings.json:"
    echo "  sudo nano $DEST/appsettings.json"
    echo "then start it with:"
    echo "  sudo systemctl start juggluco-relay && journalctl -u juggluco-relay -f"
else
    sudo systemctl start juggluco-relay
    echo "Updated and restarted. To roll back:"
    echo "  sudo systemctl stop juggluco-relay && sudo cp -p $DEST/JugglucoRelay.prev $DEST/JugglucoRelay && sudo systemctl start juggluco-relay"
fi
