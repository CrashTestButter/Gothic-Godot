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
- Headless shots: `Godot/tools/render.sh <width>x<height> <shot.png> -- <spike args>` (gamescope, memory cap, Godot's Dummy audio driver; `AUDIO_DRIVER=PulseAudio` to hear it).
- Playable: `--player=true --ai=true` (desktop: WASD, mouse, Shift, Q/E, T teleport, F talk / open, right mouse take, B put away, Tab bag, U eat / drink, arrow keys lock picks). `--game=2` for Gothic 2.
- Scripted tests: `Godot/tools/run-tests.sh [1|2] [dialog grab bag use chest door lock]` runs each one headless and prints PASS / FAIL (`--dialog-test`, `--grab-test=true|bag|use`, `--chest-test`, `--door-test`, `--lock-test`; also `--walk-test=<s>`, `--teleport-test=<s>`).
- World cache: the built world (chunk meshes, collision, occluders, ground light, texture layers) is saved to `user://world-cache` on the first start and loaded on the next ones (Linux: `~/.local/share/godot/app_userdata/Gothic-Godot/world-cache`, 0.3-0.5 GB per world). Rebuilt by itself when the game data, the world, the build options or the code change. `--world-cache=false` builds without it, `--world-cache=rebuild` builds and saves again.
- Load and GPU options: `--object-far=<m>` (objects in their own cells, hidden beyond), `--alpha-far=<m>` (trees and bushes hidden earlier), `--texture-compression=none|etc2|astc|s3tc` (default ETC2 on the Quest, none on the desktop), `--compress-meshes=true` (less video memory, texture streaks on big objects), `--occluder-area=<m²>` (occlusion culling), `--cell=<m>` (world cell size, default 96), `--collision-near=<m>` (collision this near the start at load, the rest after; default 60, 0 = all at load), `--npc-draws=split` (NPC sub-meshes and heads drawn on their own).
- Helpers: `--record-audio=<file.wav>` saves the mix, `--count-externals` lists the scripts' externals the spike lacks, `--shots=%MODEL,<m>` frames the first NPC of a model, `--open-chests=true`, `--hand-preview=true`, `--spare-check=<TEXTURE.TGA>` (a texture asked for after loading, on a quad in front of the camera: the texture array's spare layers), `--preload-items=false` (carried and chest items get their textures when they show up).

## The wonderbranch

Branch `wonderbranch` is Gothic Godot on top of Todi's Gothic-Unity wonderbranch (all his open PRs together): the Unity side is exactly that branch, the Godot side comes from `main`, and the shared core compiles against the newer scripts (87 files instead of 78). `Godot/tools/sync-wonderbranch.sh [--test] [--push]` refreshes it from the local Gothic-Unity checkout (`GOTHIC_UNITY`, default `../Gothic-Unity`): it snapshots the wonderbranch's tree as one commit on Gothic-Unity main (its own history holds an editor crash dump and isn't published), lays `Godot/`, `forks/`, `docs/wiki/` and the README from `main` over it, regenerates the core list, builds, and with `--test` runs both suites. Godot work stays on `main`; the wonderbranch only takes it.

## Android / Quest

Traps found while getting the spike onto the Quest 3:
- The prebuilt Android template (no gradle) needs the C# project on net9.0 and contains no OpenXR loader and no VR launch category: the Quest starts it as a flat panel. Use the gradle build with the OpenXR vendors plugin (Meta plugin on).
- The Meta plugin adds hand tracking only when the project setting `xr/openxr/extensions/hand_tracking` exists; Godot 4.7 no longer defines it, so the project sets it by hand.
- Without optional hand tracking, a launch with sleeping controllers hangs on "controller required" (`LaunchCheckControllerRequiredDialogActivity`); its task blocks every later launch until removed (`adb shell am stack remove <task>`, the bench script does it).
- Godot drops launch arguments of exported Android activities, so the spike reads its arguments from `files/spike-args.txt` in its app folder.
- The refresh rate follows `debug.oculus.refreshRate` (left at 120 by earlier tests, lost on reboot); requesting a rate from the app before the session runs fell back to 120 Hz.
- `--install-android-build-template` did nothing headless; the template is unzipped by hand into `android/build` (plus `android/.build_version`), gradle's heap lowered to 2 GB in `android/build/gradle.properties`.
- `tools/quest-bench.sh <seconds> [args]` launches, waits for the world, samples VrApi and the spike's lines and prints a summary.

- Android SDK with build tools 36.1.0 and platform 36, JDK 17, set in Godot's editor settings.
- Gradle build (`--install-android-build-template`) and the vendors plugin with the Meta plugin enabled are needed for a Quest build; the prebuilt template has no OpenXR loader.
- Export: `godot --headless --path Godot --export-debug "Quest" ../Builds/Quest/GothicGodot.apk`
- Data: `/storage/emulated/0/Android/data/com.gothic.godot/files/Gothic1` (create folders with `adb shell mkdir -p`, then `chmod -R a+rwX`).
- Frame timing: `adb logcat -s godot VrApi`.
