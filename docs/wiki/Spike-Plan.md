# Spike plan

The spike answers the high risks with numbers before anybody ports game code. Godot 4.7.2 .NET, Mobile renderer, OpenXR vendors plugin, Quest 3 (Quest 2 goal: same scene at 72 Hz).

## Steps

1. **Native libraries**: load ZenKit (`libzenkitcapi`) through P/Invoke on Linux and on the Quest; open the Gothic 1 archives and the world.
2. **World**: build the world mesh in chunks with one texture array material and Gothic's vertex light colours; fly a camera along a fixed path through the Old Camp; count draw calls.
3. **Objects**: build all world objects (VOBs) as single meshes, then merged per chunk and material, then as MultiMesh for repeated meshes; compare draw calls and CPU time.
4. **NPCs**: 50 human skeletons with skinned bodies, an animation sampled in C# every frame and written as bone poses; measure the C# to engine call cost; then the alternatives (bulk setter in a fork, GPU skinning from a bone texture).
5. **Runtime**: same scene with MonoVM JIT, Mono AOT and NativeAOT (linux-bionic) where they build; count garbage collections and pauses.
6. **Quest**: export the APK, run the fixed camera path, read frame timing from `adb logcat` (VrApi lines) and our own log.

## Go / no-go

Go if the Old Camp scene on the Quest 3
- holds 72 Hz with CPU time at or below Gothic-Unity's,
- has garbage collection pauses below 2 ms,
- stays inside Meta's draw call budget (400-600) with all objects and 50 NPCs.

No-go items get a fix path in [[Godot-Risks]] or end the study.

## Baseline (Gothic-Unity, Quest 3, 2026-11-03)

- G1 72 Hz: average 70.4 FPS, app GPU 3.7-8.2 ms of 13.9 ms, drops are CPU spikes.
- G2 120 Hz: average 98.8 FPS; Khorinis at night with a large object fade distance GPU-bound at 9.2 ms.
