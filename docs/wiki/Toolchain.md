# Toolchain

## Setup

1. Godot 4.7.2 .NET (mono) for Linux x86_64, plus its export templates (`~/.local/share/godot/export_templates/4.7.2.stable.mono/`).
2. .NET SDK 8 (editor, desktop) and 9 (Godot's prebuilt Android template only runs net9.0).
3. `Godot/setup.sh` fetches ZenKit and dmusic (from [binary-dependencies](https://github.com/Gothic-Unity-Project/binary-dependencies), linux-x64 and android-arm64) and the [OpenXR vendors plugin](https://github.com/GodotVR/godot_openxr_vendors) 5.1.0. They are not committed.
4. `Godot/nuget.config` points to the Godot SDK packages next to the editor (`~/opt/godot/...`); adjust the path to your install.

## Running the spike

- Desktop: `godot --path Godot -- --data=<Gothic 1 folder> [--slice=256] [--vobs=false] [--fog=0.0018]`
- Screenshots: `--shots="x,y,z>tx,ty,tz;..." --screenshot=/path/shot.png` (Godot coordinates, metres), one file per shot, quits after the last.
- Without a window: `gamescope --backend headless -W 1600 -H 900 -- godot --path Godot --resolution 1600x900 -- ...`
- Output: `[Spike]` load times, `[Perf]` one line per second (FPS, draw calls, objects, garbage collections).

## Android / Quest

- Android SDK with build tools 36.1.0 and platform 36, JDK 17, set in Godot's editor settings.
- Gradle build (`--install-android-build-template`) and the vendors plugin with the Meta plugin enabled are needed for a Quest build; the prebuilt template has no OpenXR loader.
- Export: `godot --headless --path Godot --export-debug "Quest" ../Builds/Quest/GothicGodot.apk`
- Data: `/storage/emulated/0/Android/data/com.gothic.godot/files/Gothic1` (create folders with `adb shell mkdir -p`, then `chmod -R a+rwX`).
- Frame timing: `adb logcat -s godot VrApi`.
