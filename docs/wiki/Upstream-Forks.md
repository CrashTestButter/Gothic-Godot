# Upstream forks

Fixes we need in other projects. Each one is made in a fork, kept small, and offered upstream.

| Project | Why | Status |
|---|---|---|
| [ZenKitCS](https://github.com/GothicKit/ZenKitCS) | External calls produced ~430 bytes of garbage each (new VM and symbol wrappers, a finalizable instance wrapper per instance parameter, boxing): ~1.5 MB/s with the Old Camp's 134 scripted NPCs, a garbage collection every 1-4 s, 10-16 ms frame spikes on the Quest. | Patch `forks/ZenKitCS/0001-perf-DaedalusVm-reuse-wrappers-in-external-callbacks.patch` on e370d5ea (1.3.0-cs.1), built by `Godot/setup.sh`. Allocations in script calls ~1500 -> ~10 KB/s; Quest 3 at 120 Hz 116 -> 119 FPS, stale frames 6.4 -> 3.8 per second. Not offered upstream yet. Tried in Gothic-Unity (same ZenKit.dll): no measurable change, see below. |
| Godot | possible: bulk bone poses from C#, interop speed, CoreCLR on Android | not needed so far |
| Godot XR Tools | probably not needed: our own interaction backend | not started |

## ZenKitCS patch details

- External callback contexts keep the managed `DaedalusVm` (and a symbol cache for default externals and the access trap), so a callback allocates nothing.
- Instances with `UserData` come back as the wrapper the `UserData` was set on; the native reference the pop created is released at once. Behaviour change: such an instance is now always the same wrapper object (before: a new one per call).
- `Push`/`Pop` use typed delegates created once per type instead of boxing `int`/`float`/`bool`.
- No change to the public API or to the native library.

## Trial in Gothic-Unity (2026-11-07)

Gothic-Unity on split/14-backpack (3a3920c06), the binary package embedded locally with the patched ZenKit.dll, Quest 3 build, G1 new game at OCC_CENTER_2 (Old Camp courtyard, all NPCs from the startup script), hand tracking feature on so it launches without controllers, two 40 s windows per build. The builds differ only in ZenKit.dll (checked in the IL2CPP metadata).

| Gothic-Unity, Quest 3 | FPS avg (min) | Stale frames | App GPU | CPU (busiest core) |
|---|---|---|---|---|
| upstream ZenKit, 120 Hz | 84.7 / 84.2 (77) | 43 per second | 8.53 ms | 63% (71%) |
| patched ZenKit, 120 Hz | 84.7 / 84.6 (78) | 42 per second | 8.48-8.51 ms | 64% (69%) |
| upstream ZenKit, 72 Hz | 70.7 / 71.0 (65) | 3.6-3.8 per second | 9.55-9.60 ms | 56% (61-62%) |
| patched ZenKit, 72 Hz | 70.6 / 70.5 (65) | 3.5-4.3 per second | 9.27-9.35 ms | 57% (61-62%) |

- No measurable difference. Gothic-Unity is GPU bound at 120 Hz (8.5 ms of 8.3), and at 72 Hz its remaining missed frames have another cause; IL2CPP's incremental collector spreads the cleanup instead of stopping a frame like Godot's .NET runtimes do.
- So the patch matters for the Godot port (MonoVM / CoreCLR stop the world for each collection), not for Gothic-Unity's Quest frames. Upstreaming it still makes sense as a general clean-up, without a performance promise for Unity.
