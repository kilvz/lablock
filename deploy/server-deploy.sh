#!/usr/bin/env bash
set -euo pipefail

# LabLock Server Deploy Script
# Run on your build machine to build & deploy to the server

SERVER_USER="root"
SERVER_HOST="${1:-}"
SERVER_PATH="/opt/lablock"
BINARY_NAME="LabLock.Server"
CONFIG="Release"
RID="linux-x64"

if [ -z "$SERVER_HOST" ]; then
  echo "Usage: $0 <server-host>"
  echo "Example: $0 lablock.example.com"
  exit 1
fi

echo "=== Building LabLock Server ==="

# Build
dotnet publish src/LabLock.Server/LabLock.Server.csproj \
  --configuration $CONFIG \
  --runtime $RID \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  -o ./publish/server

echo "Build complete."

# Copy to server
echo "Deploying to $SERVER_HOST..."
rsync -avz --delete \
  ./publish/server/ \
  "$SERVER_USER@$SERVER_HOST:$SERVER_PATH/"

# Restart service
ssh "$SERVER_USER@$SERVER_HOST" "systemctl restart lablock-server"

echo ""
echo "=== Deploy Complete ==="
echo "Server: $SERVER_HOST"
echo "Path:   $SERVER_PATH"
