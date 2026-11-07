#!/usr/bin/env bash
# Renders spike shots without a window: tools/render.sh <width>x<height> <output.png> -- <spike args...>
# Needs gamescope, Godot .NET at $GODOT (default ~/opt/godot/godot) and dotnet at $DOTNET_ROOT (default ~/opt/dotnet).
set -euo pipefail
cd "$(dirname "$0")/.."
SIZE="$1"; OUT="$2"; shift 2; [ "${1:-}" = "--" ] && shift
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/opt/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
GODOT="${GODOT:-$HOME/opt/godot/godot}"
W="${SIZE%x*}"; H="${SIZE#*x}"
exec systemd-run --user --scope -q -p MemoryMax=5G nice timeout 300 \
    gamescope --backend headless -W "$W" -H "$H" -- "$GODOT" --path . --resolution "$SIZE" -- --screenshot="$OUT" "$@"
