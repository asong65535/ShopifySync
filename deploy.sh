#!/bin/bash
set -e
SOLUTION_DIR="$HOME/projects/shopify-sync"
VM="user@w10vm"

cd "$SOLUTION_DIR"

# ── 1. Push ──────────────────────────────────────────────────────────────────
echo "==> Pushing to remote..."
git push

# ── 2. Pull and publish on VM ────────────────────────────────────────────────
echo "==> Cleaning deploy folder (preserving appsettings.local.json)..."
ssh "$VM" 'C:/msys64/usr/bin/bash.exe -c "export PATH=/c/msys64/usr/bin:/c/msys64/bin:\$PATH && cd /c/dev/ShopifySyncApp && find . -maxdepth 1 -type f ! -name appsettings.local.json -delete"'

echo "==> Publishing on VM..."
ssh "$VM" 'C:/msys64/usr/bin/bash.exe -c "export PATH=/c/msys64/usr/bin:/c/msys64/bin:\$PATH && cd /c/dev/ShopifySync && git pull --quiet && \"/c/Program Files/dotnet/dotnet\" publish ShopifySyncApp -c Release -o /c/dev/ShopifySyncApp --no-self-contained -v quiet"'

echo "==> Deploy complete. Launch the app on the VM manually."
