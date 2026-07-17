#!/usr/bin/env bash
set -euo pipefail

# ── AutoRip Ubuntu Installer ──────────────────────────────────────────────────
# Installs .NET 10.0 SDK, MakeMKV CLI, HandBrake CLI, and all other
# dependencies, then builds and sets up AutoRip as a systemd service.
# ──────────────────────────────────────────────────────────────────────────────

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_NAME="autorip"
INSTALL_DIR="/opt/autorip"
DOTNET_VERSION="10.0"
LOG_FILE="/tmp/autorip-install.log"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

log()  { echo -e "${CYAN}[INFO]${NC}  $*" | tee -a "$LOG_FILE"; }
ok()   { echo -e "${GREEN}[OK]${NC}    $*" | tee -a "$LOG_FILE"; }
warn() { echo -e "${YELLOW}[WARN]${NC}  $*" | tee -a "$LOG_FILE"; }
fail() { echo -e "${RED}[FAIL]${NC}  $*" | tee -a "$LOG_FILE"; exit 1; }

> "$LOG_FILE"

# ── Preflight checks ─────────────────────────────────────────────────────────

if [[ "$(id -u)" -eq 0 ]]; then
    fail "Do not run this script as root. It will use sudo where needed."
fi

if ! command -v apt-get &>/dev/null; then
    fail "This script requires apt-get. Only Ubuntu / Debian derivatives are supported."
fi

log "AutoRip Ubuntu Installer — $(date)"
log "Log file: $LOG_FILE"
echo ""

# ── Step 1: Update apt cache ─────────────────────────────────────────────────

log "Updating package lists..."
sudo apt-get update -qq 2>&1 | tee -a "$LOG_FILE"
ok "Package lists updated."

# ── Step 2: Install .NET 10.0 SDK ────────────────────────────────────────────

log "Checking for .NET SDK $DOTNET_VERSION..."

copy_dotnet_to_usr() {
    local src="$HOME/.dotnet"
    local dst="/usr/share/dotnet"

    if [[ ! -d "$src" ]]; then
        fail "dotnet install directory not found at $src"
    fi

    sudo mkdir -p "$dst"
    sudo cp -a "$src/." "$dst/"
    sudo ln -sf "$dst/dotnet" /usr/bin/dotnet
    sudo ln -sf "$dst/dotnet" /usr/local/bin/dotnet 2>/dev/null || true

    if ! grep -q 'export DOTNET_ROOT=/usr/share/dotnet' /etc/profile.d/dotnet.sh 2>/dev/null; then
        echo 'export DOTNET_ROOT=/usr/share/dotnet' | sudo tee /etc/profile.d/dotnet.sh >/dev/null
        echo 'export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools' | sudo tee -a /etc/profile.d/dotnet.sh >/dev/null
    fi
}

if dotnet --version &>/dev/null; then
    INSTALLED_VER="$(dotnet --version)"
    ok "dotnet $INSTALLED_VER already installed."
elif [[ -x "/usr/share/dotnet/dotnet" ]]; then
    ok "Found dotnet at /usr/share/dotnet. Re-linking..."
    sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
    export DOTNET_ROOT=/usr/share/dotnet
    export PATH="$DOTNET_ROOT:$PATH"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    ok "Found dotnet in home directory. Moving to system location..."
    copy_dotnet_to_usr
else
    log "Installing .NET SDK $DOTNET_VERSION..."
    log "Downloading dotnet-install script..."

    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh

    bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --install-dir "$HOME/.dotnet" 2>&1 | tee -a "$LOG_FILE"
    rm -f /tmp/dotnet-install.sh

    if [[ ! -x "$HOME/.dotnet/dotnet" ]]; then
        fail ".NET SDK installation failed."
    fi

    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
    ok ".NET SDK installed: $(dotnet --version)"

    log "Moving dotnet to /usr/share/dotnet for service use..."
    copy_dotnet_to_usr
fi

export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
log "dotnet version: $(dotnet --version)"
echo ""

# ── Step 3: Install system packages ──────────────────────────────────────────

SYSTEM_PACKAGES=(
    handbrake-cli
    mkvtoolnix
    tesseract-ocr
    tesseract-ocr-eng
    ffmpeg
    genisoimage
    build-essential
    pkg-config
    python3
    python3-pip
    python3-venv
    libssl-dev
    zlib1g-dev
    libc6-dev
    libavcodec-dev
    libavformat-dev
    libavutil-dev
    libswscale-dev
    libswresample-dev
)

log "Installing system packages: ${SYSTEM_PACKAGES[*]}..."
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y "${SYSTEM_PACKAGES[@]}" 2>&1 | tee -a "$LOG_FILE"
ok "System packages installed."
echo ""

# ── Step 3b: Install subtitle OCR tools ───────────────────────────────────────

log "Installing subtitle OCR tools (vobsub2srt, pgsrip)..."

# vobsub2srt: VobSub (DVD) → SRT
VOBSUB2SRT_BIN="/usr/local/bin/vobsub2srt"
if command -v vobsub2srt &>/dev/null; then
    ok "vobsub2srt already installed: $(command -v vobsub2srt)"
else
    log "Building vobsub2srt from source..."
    VOBSUB2SRT_SRC="/tmp/vobsub2srt-build"
    rm -rf "$VOBSUB2SRT_SRC"
    git clone --depth 1 https://github.com/iltf20/VobSub2SRT.git "$VOBSUB2SRT_SRC" 2>&1 | tee -a "$LOG_FILE"
    (
        cd "$VOBSUB2SRT_SRC"
        ./configure 2>&1 | tee -a "$LOG_FILE"
        make 2>&1 | tee -a "$LOG_FILE"
        sudo make install 2>&1 | tee -a "$LOG_FILE"
    ) || warn "vobsub2srt build failed — VobSub OCR will be unavailable."
    rm -rf "$VOBSUB2SRT_SRC"

    if command -v vobsub2srt &>/dev/null; then
        ok "vobsub2srt installed: $(command -v vobsub2srt)"
    else
        warn "vobsub2srt not in PATH. VobSub subtitle OCR will be skipped."
    fi
fi

# pgsrip: PGS (Blu-ray) → SRT — Python package
if command -v pgsrip &>/dev/null; then
    ok "pgsrip already installed: $(command -v pgsrip)"
else
    log "Installing pgsrip (Python PGS OCR tool)..."
    if sudo pip3 install --break-system-packages pgsrip 2>&1 | tee -a "$LOG_FILE"; then
        ok "pgsrip installed: $(command -v pgsrip)"
    else
        warn "pgsrip install failed — PGS subtitle OCR will be skipped."
    fi
fi
echo ""

# ── Step 4: Install MakeMKV CLI ──────────────────────────────────────────────

MAKMKV_BUILD_DIR="/tmp/makemkv-build"

if command -v makemkvcon &>/dev/null; then
    ok "makemkvcon already installed: $(which makemkvcon)"
else
    log "MakeMKV CLI not found. Building from source..."
    log "(This compiles makemkv-oss and installs makemkv-bin from the official site.)"

    sudo apt-get install -y build-essential pkg-config libc6-dev libssl-dev \
        libexpat1-dev libavcodec-dev libgl1-mesa-dev libqt4-dev qtbase5-dev \
        libfdk-aac-dev 2>&1 | tee -a "$LOG_FILE"

    rm -rf "$MAKMKV_BUILD_DIR"
    mkdir -p "$MAKMKV_BUILD_DIR"

    # ── Build and install makemkv-oss (open-source part) ─────────────────

    log "Downloading makemkv-oss..."
    MAKMKV_OSS_URL="https://www.makemkv.com/download/makemkv-oss-1.17.9.tar.gz"
    curl -fsSL "$MAKMKV_OSS_URL" -o "$MAKMKV_BUILD_DIR/makemkv-oss.tar.gz"

    tar -xzf "$MAKMKV_BUILD_DIR/makemkv-oss.tar.gz" -C "$MAKMKV_BUILD_DIR"
    MAKMKV_OSS_DIR=$(find "$MAKMKV_BUILD_DIR" -maxdepth 1 -type d -name 'makemkv-oss-*' | head -1)

    (
        cd "$MAKMKV_OSS_DIR"
        ./configure 2>&1 | tee -a "$LOG_FILE"
        make -j"$(nproc)" 2>&1 | tee -a "$LOG_FILE"
        sudo make install 2>&1 | tee -a "$LOG_FILE"
    )
    ok "makemkv-oss compiled and installed."

    # ── Build and install makemkv-bin (closed-source binary part) ────────

    log "Downloading makemkv-bin..."
    MAKMKV_BIN_URL="https://www.makemkv.com/download/makemkv-bin-1.17.9.tar.gz"
    curl -fsSL "$MAKMKV_BIN_URL" -o "$MAKMKV_BUILD_DIR/makemkv-bin.tar.gz"

    tar -xzf "$MAKMKV_BUILD_DIR/makemkv-bin.tar.gz" -C "$MAKMKV_BUILD_DIR"
    MAKMKV_BIN_DIR=$(find "$MAKMKV_BUILD_DIR" -maxdepth 1 -type d -name 'makemkv-bin-*' | head -1)

    (
        cd "$MAKMKV_BIN_DIR"
        echo "/tmp/makemkv-tmp" > /tmp/makemkv-bin-answer
        echo "yes" >> /tmp/makemkv-bin-answer
        cat /tmp/makemkv-bin-answer | sudo make install 2>&1 | tee -a "$LOG_FILE"
        rm -f /tmp/makemkv-bin-answer
    )
    ok "makemkv-bin installed."

    # Finalize with ldconfig
    sudo ldconfig

    if command -v makemkvcon &>/dev/null; then
        ok "makemkvcon installed: $(which makemkvcon)"
    else
        warn "makemkvcon not found in PATH after build. Check $LOG_FILE."
        warn "You may need to run: sudo ldconfig && hash -r"
    fi
fi
echo ""

# ── Step 5: Verify all binaries ──────────────────────────────────────────────

declare -A REQUIRED_BINS
REQUIRED_BINS=(
    [dotnet]=".NET SDK"
    [makemkvcon]="MakeMKV CLI"
    [HandBrakeCLI]="HandBrake CLI"
    [mkvextract]="MKVToolNix"
    [tesseract]="Tesseract OCR"
    [ffmpeg]="FFmpeg"
    [eject]="util-linux (eject)"
    [blkid]="util-linux (blkid)"
    [udevadm]="systemd (udevadm)"
    [isoinfo]="genisoimage"
    [volname]="genisoimage"
)

log "Verifying installed tools..."
ALL_OK=true
for bin in "${!REQUIRED_BINS[@]}"; do
    if command -v "$bin" &>/dev/null; then
        ok "${REQUIRED_BINS[$bin]} ($bin)"
    else
        warn "${REQUIRED_BINS[$bin]} ($bin) — NOT FOUND"
        ALL_OK=false
    fi
done

if [[ "$ALL_OK" == "false" ]]; then
    warn "Some tools are missing. AutoRip may not function correctly."
else
    ok "All dependencies present."
fi
echo ""

# ── Step 6: Build AutoRip ────────────────────────────────────────────────────

log "Building AutoRip..."

CSPROJ_FILE=$(find "$REPO_DIR" -maxdepth 4 -name 'AutoRip.csproj' -print -quit 2>/dev/null)

if [[ -z "$CSPROJ_FILE" || ! -f "$CSPROJ_FILE" ]]; then
    fail "AutoRip.csproj not found under $REPO_DIR. Are you running from the repo root?"
fi

PROJECT_DIR="$(dirname "$CSPROJ_FILE")"
log "Found project at $PROJECT_DIR"

sudo rm -rf "$INSTALL_DIR"
sudo mkdir -p "$INSTALL_DIR"
sudo chown "$USER:$USER" "$INSTALL_DIR"

dotnet publish "$CSPROJ_FILE" \
    --configuration Release \
    --output "$INSTALL_DIR" \
    -p:PublishSingleFile=false \
    2>&1 | tee -a "$LOG_FILE"

sudo chown -R root:root "$INSTALL_DIR"
sudo mkdir -p "$INSTALL_DIR/Data"
sudo chown "$USER:$USER" "$INSTALL_DIR/Data"

ok "AutoRip published to $INSTALL_DIR"
echo ""

# ── Step 7: Install systemd service ──────────────────────────────────────────

SERVICE_FILE="/etc/systemd/system/$SERVICE_NAME.service"
WAS_RUNNING=false

if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    WAS_RUNNING=true
fi

if [[ -d /run/systemd/system ]]; then
    log "Writing systemd service..."

    DOTNET_BIN="$(which dotnet)"
    DOTNET_ROOT_DIR="${DOTNET_ROOT:-/usr/share/dotnet}"

    sudo tee "$SERVICE_FILE" >/dev/null <<SYSTEMDEOF
[Unit]
Description=AutoRip — Automated DVD/Blu-ray Ripping Service
After=network.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR
ExecStart=$DOTNET_BIN $INSTALL_DIR/AutoRip.dll
Restart=always
RestartSec=5
Environment=DOTNET_ROOT=$DOTNET_ROOT_DIR
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5139
Environment=PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# Security hardening
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
SYSTEMDEOF

    sudo systemctl daemon-reload
    ok "Service file written to $SERVICE_FILE"

    if [[ "$WAS_RUNNING" == "true" ]]; then
        log "Restarting autorip service..."
        sudo systemctl restart "$SERVICE_NAME" 2>&1 | tee -a "$LOG_FILE"
        ok "Service restarted."
    else
        log "Enabling and starting autorip service..."
        sudo systemctl enable --now "$SERVICE_NAME" 2>&1 | tee -a "$LOG_FILE"
        ok "Service started."
    fi
else
    warn "systemd not running. Skipping service creation."
fi

echo ""
LOCAL_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
[[ -z "$LOCAL_IP" ]] && LOCAL_IP="<your-device-ip>"

echo -e "${YELLOW}────────────────────────────────────────────────────────────────${NC}"
echo -e "${YELLOW}  Open:  http://${LOCAL_IP}:5139${NC}"
echo -e "${YELLOW}  Logs:  sudo journalctl -fu $SERVICE_NAME${NC}"
echo -e "${YELLOW}  Stop:  sudo systemctl stop $SERVICE_NAME${NC}"
echo -e "${YELLOW}────────────────────────────────────────────────────────────────${NC}"

echo ""
ok "Installation complete."
