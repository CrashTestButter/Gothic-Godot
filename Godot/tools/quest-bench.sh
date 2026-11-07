#!/usr/bin/env bash
# Runs the spike on a USB-connected Quest and prints frame stats: tools/quest-bench.sh <seconds> [spike args...]
# Waits for the first [Perf] line (world loaded), then samples VrApi and the spike's own lines for <seconds>.
set -uo pipefail
SECONDS_TO_SAMPLE="$1"; shift
PKG=com.gothic.godot
LOG="${QUEST_BENCH_LOG:-/tmp/quest-bench.log}"
FILES=/storage/emulated/0/Android/data/$PKG/files
HZ=""; for a in "$@"; do case "$a" in --hz=*) HZ="${a#--hz=}";; esac; done

adb shell am force-stop $PKG
adb shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
adb shell input keyevent KEYCODE_WAKEUP
# A stuck "controller required" launch check blocks every launch; remove its task.
for t in $(adb shell dumpsys activity activities | grep -o 'LaunchCheckControllerRequiredDialogActivity t[0-9]*' | grep -o '[0-9]*$'); do
    adb shell am stack remove "$t" >/dev/null 2>&1
done
# Godot drops launch arguments of exported activities: the spike reads them from a file.
adb shell "echo '$*' > $FILES/spike-args.txt && chmod a+r $FILES/spike-args.txt"
# The refresh rate the runtime uses (the debug property overrides the app's request).
[ -n "$HZ" ] && adb shell setprop debug.oculus.refreshRate "$HZ"
adb logcat -c
adb shell am start -n $PKG/com.godot.game.GodotAppLauncher >/dev/null

for _ in $(seq 1 90); do
    adb logcat -d -s godot:I | grep -q '\[Perf\]' && break
    timeout 1 tail -f /dev/null 2>/dev/null
done
adb logcat -d -s godot:I | grep -E '\[Spike\]' | sed 's/^.*\[Spike\]/[Spike]/' | grep -E 'OpenXR|MSAA|built|loaded|nodes'
adb logcat -c
timeout "$SECONDS_TO_SAMPLE" adb logcat -v time > "$LOG" 2>&1
PID=$(adb shell pidof $PKG)
python3 "$(dirname "$0")/quest-bench-summary.py" "$LOG" "$PID"
