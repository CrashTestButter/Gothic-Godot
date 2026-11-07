#!/usr/bin/env bash
# The benchmark (--bench=true) for Gothic 1 and 2 with the test settings; prints one [Bench] line per game.
#   tools/bench.sh [extra spike args...]      OUT=<dir> for the logs (default /tmp/gothic-godot-bench)
set -uo pipefail
cd "$(dirname "$0")/.."
OUT="${OUT:-/tmp/gothic-godot-bench}"; mkdir -p "$OUT"
for game in 1 2; do
    tools/render.sh 1280x720 "$OUT/g$game.png" -- --game="$game" --player=true --ai=true --hour=12 --anim-far=80 \
        --object-far=80 --pfx-far=40 --far=600 --music=false --bench=true "$@" > "$OUT/g$game.log" 2>&1
    echo "G$game $(grep -E '^\[Bench\] ' "$OUT/g$game.log" | tail -1 | cut -d' ' -f2-)"
done
