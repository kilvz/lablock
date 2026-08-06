#!/usr/bin/env bash
set -euo pipefail

# LabLock Server Setup — Ubuntu / Debian
# Run as root on the server machine

SERVICE_NAME="lablock-server"
INSTALL_DIR="/opt/lablock"
BINARY_NAME="LabLock.Server"
ASPECTS_CORE_VERSION="8.0"

if [ "$EUID" -ne 0 ]; then
  echo "Run as root: sudo $0"
  exit 1
fi

echo "=== LabLock Server Setup ==="

# --- Create user ---
if ! id -u lablock &>/dev/null; then
  useradd --system --no-create-home --shell /usr/sbin/nologin lablock
  echo "Created system user: lablock"
fi

# --- Create directories ---
mkdir -p "$INSTALL_DIR/data"
echo "Created $INSTALL_DIR"

# --- Copy binary ---
if [ -f "./publish/server/$BINARY_NAME" ]; then
  cp -r ./publish/server/* "$INSTALL_DIR/"
  echo "Copied binaries from ./publish/server/"
elif [ -f "./publish/$BINARY_NAME" ]; then
  cp -r ./publish/* "$INSTALL_DIR/"
  echo "Copied binaries from ./publish/"
elif [ -f "./$BINARY_NAME" ]; then
  cp ./* "$INSTALL_DIR/"
  echo "Copied binaries from current directory"
else
  echo "WARNING: No publish directory found. Place the published app in $INSTALL_DIR/ manually."
fi

chown -R lablock:lablock "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR/data"
chmod +x "$INSTALL_DIR/$BINARY_NAME" 2>/dev/null || true

# --- Create systemd unit ---
cat > /etc/systemd/system/$SERVICE_NAME.service << 'EOF'
[Unit]
Description=LabLock Server
After=network.target

[Service]
Type=simple
User=lablock
WorkingDirectory=/opt/lablock
ExecStart=/opt/lablock/LabLock.Server --server
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000

[Install]
WantedBy=multi-user.target
EOF

echo "Created systemd unit: $SERVICE_NAME"

# --- Enable & start ---
systemctl daemon-reload
systemctl enable $SERVICE_NAME
systemctl restart $SERVICE_NAME

echo ""
echo "=== Setup Complete ==="
echo "Service:   $SERVICE_NAME"
echo "Directory: $INSTALL_DIR"
echo "Endpoint:  http://$(hostname -I | awk '{print $1}'):5000"
echo ""
echo "Check status: sudo systemctl status $SERVICE_NAME"
echo "View logs:    sudo journalctl -u $SERVICE_NAME -f"
