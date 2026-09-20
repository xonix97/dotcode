#!/usr/bin/env bash
# dotcode installer — Linux & macOS
# Usage: curl -fsSL https://raw.githubusercontent.com/xonix97/dotcode/main/install.sh | bash
set -euo pipefail

REPO="xonix97/dotcode"
BRANCH="main"
INSTALL_DIR="${DOTCODE_HOME:-$HOME/.dotcode}"
BIN_DIR="${DOTCODE_BIN:-$HOME/.local/bin}"

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✔\033[0m %s\n' "$1"; }
die()  { printf '  \033[31m✖ %s\033[0m\n' "$1" >&2; exit 1; }

bold "dotcode installer"

# --- .NET 9 SDK -------------------------------------------------------------
need_dotnet=1
if command -v dotnet >/dev/null 2>&1; then
    ver="$(dotnet --version 2>/dev/null || echo 0)"
    major="${ver%%.*}"
    if [ "${major:-0}" -ge 9 ] 2>/dev/null; then
        need_dotnet=0
        ok "dotnet $ver found"
    fi
fi
if [ "$need_dotnet" = 1 ]; then
    echo "  installing .NET 9 SDK…"
    if command -v apt-get >/dev/null 2>&1; then
        # Ubuntu/Debian: use dotnet install script (packages are often stale)
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet" >/dev/null
        export PATH="$HOME/.dotnet:$PATH"
    elif command -v dnf >/dev/null 2>&1; then
        sudo dnf install -y dotnet-sdk-9.0 >/dev/null
    elif command -v pacman >/dev/null 2>&1; then
        sudo pacman -S --noconfirm dotnet-sdk >/dev/null
    elif command -v brew >/dev/null 2>&1; then
        brew install dotnet-sdk >/dev/null 2>&1 || brew install --cask dotnet-sdk >/dev/null
    else
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet" >/dev/null
        export PATH="$HOME/.dotnet:$PATH"
    fi
    ok ".NET 9 SDK installed"
fi

# --- source -----------------------------------------------------------------
echo "  fetching dotcode source…"
rm -rf "$INSTALL_DIR"
mkdir -p "$(dirname "$INSTALL_DIR")"
if command -v git >/dev/null 2>&1; then
    git clone --depth 1 "https://github.com/$REPO.git" "$INSTALL_DIR" >/dev/null 2>&1 \
        || die "git clone failed"
else
    curl -fsSL "https://github.com/$REPO/archive/refs/heads/$BRANCH.tar.gz" | tar xz -C "$(dirname "$INSTALL_DIR")"
    rm -rf "$INSTALL_DIR"
    mv "$(dirname "$INSTALL_DIR")/dotcode-$BRANCH" "$INSTALL_DIR"
fi
ok "source in $INSTALL_DIR"

# --- build ------------------------------------------------------------------
echo "  building (first build downloads packages — takes a minute)…"
( cd "$INSTALL_DIR" && dotnet build DotCode.sln -v q --nologo ) >/dev/null 2>&1 \
    || die "build failed — run 'cd $INSTALL_DIR && dotnet build DotCode.sln' to see why"
ok "built"

# --- launcher ---------------------------------------------------------------
mkdir -p "$BIN_DIR"
cat > "$BIN_DIR/dotcode" <<EOF
#!/usr/bin/env bash
export DOTCODE_INSTALL="$INSTALL_DIR"
export ASPNETCORE_ENVIRONMENT=Development
set -e
\${DOTCODE_BIN:-}
"\$INSTALL_DIR/run.sh"
EOF
chmod +x "$BIN_DIR/dotcode"

cat > "$INSTALL_DIR/run.sh" <<'EOF'
#!/usr/bin/env bash
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
cleanup() { kill $(jobs -p) 2>/dev/null; }
trap cleanup EXIT
(cd "$DIR/src/DotCode.Server" && dotnet run --no-build --no-launch-profile) &
SERVER_PID=$!
until curl -fsS http://127.0.0.1:4096/global/health >/dev/null 2>&1; do
    sleep 0.5
    kill -0 "$SERVER_PID" 2>/dev/null || exit 1
done
(cd "$DIR/src/DotCode.Web/DotCode.Web" && dotnet run --no-build --no-launch-profile --urls http://localhost:5131) &
WEB_PID=$!
echo
echo "  ● dotcode web UI: http://localhost:5131   (Ctrl+C to stop)"
command -v xdg-open >/dev/null 2>&1 && (sleep 2 && xdg-open http://localhost:5131) >/dev/null 2>&1 &
command -v open     >/dev/null 2>&1 && (sleep 2 && open http://localhost:5131)     >/dev/null 2>&1 &
wait
EOF
chmod +x "$INSTALL_DIR/run.sh"
ok "launcher in $BIN_DIR/dotcode"

# --- PATH -------------------------------------------------------------------
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *)
        for rc in "$HOME/.bashrc" "$HOME/.zshrc"; do
            [ -f "$rc" ] || continue
            grep -q "$BIN_DIR" "$rc" 2>/dev/null || echo "export PATH=\"$BIN_DIR:\$PATH\"" >> "$rc"
        done
        echo "  note: added $BIN_DIR to PATH in your shell rc — open a new terminal or run:"
        echo "        export PATH=\"$BIN_DIR:\$PATH\""
        ;;
esac

echo
bold "Installed. Run:  dotcode"
echo "  then open http://localhost:5131"
