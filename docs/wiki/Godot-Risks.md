# Godot risks

Research from 2026-11-07, Godot 4.7.2 (stable since 2026-08-18). Each risk gets a status: **open** (only researched), **measured** (spike numbers exist), **path** (a fix path is chosen), **solved**.

How tied Gothic-Unity is to Unity today (split/14, 2026-11-06): 573 C# files (~88k lines), 363 use `UnityEngine`, 148 MonoBehaviours, Reflex DI in 229 files, physics calls in 83, coroutines in 44, Burst/Jobs in 9 (world mesh writing, animation poses), 119 prefabs, 21 shaders, 10 scenes. Helping us: no bundled assets (all Gothic data loads at runtime), VR interaction sits behind our own ports (`Gothic.VR.Interaction`), only one file outside the XRI backend uses XRI types.

## High

### R1 - C# on Android / Quest is experimental
- Godot docs 4.7: "Android support is currently experimental" ([docs](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/index.html)), since 4.2 (2023).
- Runtime on Android is Microsoft's mobile MonoVM; desktop uses CoreCLR. NativeAOT has no upstream Android support, only the linux-bionic route added in 4.5 with many limits ([godot#107992](https://github.com/godotengine/godot/issues/107992)).
- Gothic-Unity's Quest build is IL2CPP (ahead-of-time C++) plus Burst. Godot has neither, so hot paths run on Mono's JIT on ARM64.
- .NET 11 makes CoreCLR the default runtime for MAUI on Android (GA November 2026), with first regression reports from big apps ([.NET blog](https://devblogs.microsoft.com/dotnet/dotnet-maui-moves-to-coreclr-in-dotnet-11/)). Godot would have to adopt it.
- An unconfirmed forum report shows 7 ms spikes in an empty 4.7.1 C# project on Android phones ([forum](https://forum.godotengine.org/t/godot-4-mobile-performance/142637)).
- No Quest store game written in Godot C# found.
- Fixed: the C# startup crash on Quest (DLL extraction) in 4.5 ([godot#103915](https://github.com/godotengine/godot/issues/103915)).
- Status: **open**. Fix paths to test: Mono AOT, NativeAOT via linux-bionic, CoreCLR on Android from .NET 11 in a Godot fork, zero allocations per frame.

### R2 - Quest rendering performance is unmeasured
- Mobile renderer is the recommended XR path (4.7). Foveation on Vulkan Android and Application SpaceWarp on Quest and Pico since 4.5 ([vendors plugin](https://godotengine.org/article/godot-openxr-vendors-plugin-400/)), Vulkan subsampled images since 4.7 ([vr.org](https://vr.org/articles/godot-4-7-xr-steam-frame-android-xr-open-source-2026)).
- No automatic 3D batching: one draw call per mesh surface, only MultiMesh draws many in one call ([guide](https://www.strayspark.studio/blog/godot-3d-optimization-guide-2026)). Unity's SRP batcher at least lowers the CPU cost per draw.
- Meta's budget for Quest 3: 400-600 draw calls for a medium scene ([Meta](https://developers.meta.com/vr/documentation/unreal/unreal-debug-android/)). Gothic worlds with thousands of objects need merging and instancing, which is our job.
- No public Godot Quest benchmarks ([forum](https://forum.godotengine.org/t/quest-3-standalone-forward-mobile-or-compatibility-for-a-3d-vr-project-in-godot-4-6/136328)).
- Status: **open**. Baseline to beat: Gothic-Unity on Quest 3, G1 72 Hz avg 70.4 FPS, G2 120 Hz avg 98.8 FPS (2026-11-03).

### R3 - Occlusion culling
- Gothic-Unity uses Unity's baked occlusion data per world (Umbra, shipped in a binary package). Godot has occluders (baked in the editor, or set at runtime as arrays) and culls on the CPU.
- Our worlds load at runtime, so occluders must be built at runtime from the world mesh or pre-baked per world.
- Status: **open**.

## Medium

### R4 - Engine calls from C# cost marshalling
- Each Godot API call crosses from C# into C++. A zero-allocation API is only a proposal ([proposals#7842](https://github.com/godotengine/godot-proposals/issues/7842)); the PR "Optimize C# Interop" (godot#116305) is open.
- Our own animation system writes bone poses: about 50 NPCs x ~60 bones = ~3000 calls per frame, as `Skeleton3D` has no bulk setter.
- Measured on the desktop (CoreCLR): 150 humans, 8598 bone calls per frame, 1.2-1.5 ms (0.14-0.17 µs per call). See [[Work-Log]].
- Status: **measured** (desktop), Quest still open. Fix paths: bulk bone setter in a Godot fork (upstream it), GPU skinning from one bone texture, caching `StringName`s.

### R5 - No Burst
- `AnimationPoseJob` and `WorldMeshWriteJob` use Burst today. In Godot they become plain C# (System.Numerics / hardware intrinsics) or threads.
- Status: **open**.

### R6 - No XR Interaction Toolkit
- Godot XR Tools is 100% GDScript, actively maintained (pickables, snap zones, teleport, climbing).
- Godot core gives OpenXR, action maps, controllers and hand tracking.
- Path: our own backend behind the `Gothic.VR.Interaction` ports (same move as HVR to XRI).
- Status: **path**.

### R7 - Code sharing with Gothic-Unity
- The team keeps developing in Unity. A port that can't take their work is dead after a few weeks.
- Status: **open**, see [[Porting-Strategy]].

## Low

- **Physics**: Jolt is the default 3D physics since 4.6. Layer matrix maps to 32 collision layers and masks. The merged Old Camp world (979k triangles) builds as Jolt trimeshes in about a second. Status: **path**.
- **Video**: Godot core plays only Theora, but Gothic-Unity has its own Bink decoder in C#. Only the texture upload changes. Status: **path**.
- **Native libraries**: ZenKit (`libzenkitcapi`) and dmusic exist for linux-x64 and android-arm64; P/Invoke packaging on Android needs a test. Status: **open**.
- **DXT textures**: Quest GPUs don't sample DXT in either engine; Gothic-Unity decompresses or transcodes already. Check Godot's fallback. Status: **open**.
- **Audio**: dmusic music as a stream generator, Gothic sounds as WAV streams. Status: **open**.
- **Vendor risk**: MIT licence, Meta pays Godot XR contributors since 2024 ([vr.org](https://vr.org/articles/godot-46-xr-engine-meta-funding)), steady releases, Pico in the default action map.
