#!/usr/bin/env bash
# Runs the spike's scripted tests headless one after the other and prints PASS / FAIL for each:
#   tools/run-tests.sh [game] [test...]     game 1 (default) or 2; tests: dialog grab bag use chest door lock
# Logs and screenshots go to $OUT (default /tmp/gothic-godot-tests). Needs what tools/render.sh needs.
set -uo pipefail
cd "$(dirname "$0")/.."
GAME="${1:-1}"; shift || true
TESTS=("$@"); [ ${#TESTS[@]} -eq 0 ] && TESTS=(dialog grab bag use chest door lock)
OUT="${OUT:-/tmp/gothic-godot-tests}"; mkdir -p "$OUT"
COMMON=(--game="$GAME" --player=true --ai=true --hour=12 --anim-far=80 --object-far=80 --music=false)
declare -A ARG=([dialog]=--dialog-test=nearest [grab]=--grab-test=true [bag]=--grab-test=bag [use]=--grab-test=use
    [chest]=--chest-test=true [door]=--door-test=true [lock]=--lock-test=true)
FAILED=0
for test in "${TESTS[@]}"; do
    log="$OUT/g$GAME-$test.log"
    tools/render.sh 1280x720 "$OUT/g$GAME-$test.png" -- "${COMMON[@]}" "${ARG[$test]}" > "$log" 2>&1
    result=$(grep -E '^\[[A-Za-z]+Test\] ' "$log" | tail -1)
    if echo "$result" | grep -qE 'Test\] done'; then echo "PASS $test: $result"; else echo "FAIL $test: ${result:-no result (see $log)}"; FAILED=1; fi
done
exit $FAILED
