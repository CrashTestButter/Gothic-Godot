# Headset test

Everything since the 120 Hz run (2026-11-07) was built and tested without a headset. This is the list to go through on the Quest 3 once it is connected. Controls follow Godot's default OpenXR action map; hand tracking maps pinch to the trigger.

## Install and start

1. `adb install -r Builds/Quest/GothicGodot.apk` (keeps the Gothic data in the app folder).
2. Arguments go to `files/spike-args.txt` (Godot drops launch arguments): `adb shell "echo '--player=true --ai=true --hour=12' > /sdcard/Android/data/com.gothic.godot/files/spike-args.txt"`.
3. Start from the library (Unknown sources) or `tools/quest-bench.sh` for a measured run. Add `--game=2` for Gothic 2.

## Check

| What | How | Expected |
|---|---|---|
| Walk, run, turn | Left stick, press it to run; right stick left/right | 1.5 m/s, 3.5 m/s, 45° snaps around the head |
| Teleport | Right stick forward, aim, release | Green arc on floor, red on walls or higher than 1.5 m |
| Hands | Look at both hands, press the grips | Gothic's hands in the grip pose, fingers close with the grip |
| Talk | Stand in front of an NPC (name tag), trigger | Dialog panel below the eyes, voices at the NPC, subtitles; choose by pointing / stick, trigger |
| Take and throw | Grip near an item, swing, let go | Item stays where touched, flies with the hand's speed |
| Bag | Let go of an item over the shoulder; Y (or left menu) opens the list | Item gone into the bag, list in front, trigger takes one out into the right hand |
| Eat, drink | Hold food or a potion at the mouth for a moment | Item used up, HP on the inside of the left wrist goes up |
| Chests, doors | Grip on a chest's lid or a door | Opens; contents lie inside; the open door lets you through |
| Lock picks | Grip a locked chest with lock picks in the bag, right stick left/right | Tag counts right turns, wrong turns may break a pick, opens at the end |
| Camp life | Stand in the Old Camp for a minute | Smalltalk lines near you, routines changing on time (`--time-speed=40` to see evenings fast) |
| Night | `--hour=23` | Outdoors dark, inside buildings lit, night music |

## Measure

- `tools/quest-bench.sh 30 --hz=120 --ai=true --object-far=80 --pfx-far=40 --anim-far=80 --occluder-area=2 --msaa=2` (G1) and with `--game=2` (Khorinis, still never measured on the headset).
- Watch in the `[Perf]` lines: garbage collections (voice lines load on a worker thread now), AI ms per second (desktop 14, Quest estimate ~70), long frames.
- Compare with the 2026-11-07 run before the features: 119.1 FPS at 120 Hz, app GPU 4.67 ms.
- Load time: the first start builds and saves the world cache, the second loads it (`[Spike] world cache` line). Desktop G2: 7.7 s first, 2.8 s after.
- Textures: the Quest build compresses them to ETC2 (once, then cached); `--texture-compression=none` for the uncompressed look and GPU time to compare.
- Trees: `--alpha-far=40` hides alpha tested objects (trees, bushes) at 40 m instead of the object distance; app GPU ms with and without.
