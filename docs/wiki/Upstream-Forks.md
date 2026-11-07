# Upstream forks

Fixes we need in other projects. Each one is made in a fork, kept small, and offered upstream.

| Project | Why | Status |
|---|---|---|
| [ZenKitCS](https://github.com/GothicKit/ZenKitCS) | External calls produced ~430 bytes of garbage each (new VM and symbol wrappers, a finalizable instance wrapper per instance parameter, boxing): ~1.5 MB/s with the Old Camp's 134 scripted NPCs, a garbage collection every 1-4 s, 10-16 ms frame spikes on the Quest. | Patch `forks/ZenKitCS/0001-perf-DaedalusVm-reuse-wrappers-in-external-callbacks.patch` on e370d5ea (1.3.0-cs.1), built by `Godot/setup.sh`. Allocations in script calls ~1500 -> ~10 KB/s; Quest 3 at 120 Hz 116 -> 119 FPS, stale frames 6.4 -> 3.8 per second. Not offered upstream yet. Gothic-Unity uses the same ZenKit.dll and would gain the same (IL2CPP's collector is not generational). |
| Godot | possible: bulk bone poses from C#, interop speed, CoreCLR on Android | not needed so far |
| Godot XR Tools | probably not needed: our own interaction backend | not started |

## ZenKitCS patch details

- External callback contexts keep the managed `DaedalusVm` (and a symbol cache for default externals and the access trap), so a callback allocates nothing.
- Instances with `UserData` come back as the wrapper the `UserData` was set on; the native reference the pop created is released at once. Behaviour change: such an instance is now always the same wrapper object (before: a new one per call).
- `Push`/`Pop` use typed delegates created once per type instead of boxing `int`/`float`/`bool`.
- No change to the public API or to the native library.
