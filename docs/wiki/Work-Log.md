# Work log

Newest first.

## 2026-11-07 - Frame spikes: garbage from ZenKit's external calls

- With the GPU at 71-74% at 120 Hz, 6-8 frames per second still missed. Logging what long frames had in common: 2-16 ms of AI time in a single script call.
- Those calls were garbage collections (gen0 and gen1) every 1-4 s: 2.5-5.5 ms on the desktop, 10-16 ms on the Quest's MonoVM. Two causes:
  - The heap still held ~1.2 GB of loading garbage (the cached ZenKit world mesh and builder data), which makes every collection slower. Fix: one full, compacting collection right after loading (1224 MB -> 14 MB).
  - ZenKitCS allocates ~430 bytes per external call (new VM and symbol wrappers, a finalizable instance wrapper per instance parameter, boxing): ~1.5 MB/s with 134 NPCs. Fix: a small ZenKitCS patch, see [[Upstream-Forks]]. Allocations inside script calls ~1500 -> ~10 KB/s, no collections in a 25 s run.
- Quest 3, 120 Hz, 134 scripted NPCs, objects 80 m, particles 40 m, animation culling, occlusion culling, MSAA 2x: **119.1 FPS average (minimum 111), stale frames 3.8 per second, no garbage collection**.
- What is left: 1-3 long frames per second from the spike's own path search when an NPC starts walking (breadth-first search over the whole waynet, nearest waypoint by scanning all of them) - spike code, not ZenKit or Godot.

## 2026-11-07 - Where the Quest 3 stands at 120 Hz

Old Camp, 134 scripted NPCs (routines, AI states), objects to 80 m, particles to 40 m:

| Variant | FPS avg (min) | App GPU | GPU busy | CPU (busiest core) | Stale frames | NPC animation |
|---|---|---|---|---|---|---|
| MSAA 4x | 110.1 (89) | 6.95 ms | 95% | 30% (50%) | 14.6/s | 1.8-2.1 ms, all 134 |
| MSAA 4x, animation culled (80 m, outside the view) | 110.0 | 6.53 ms | 91% | 25% (30%) | 12.1/s | 0.74-0.97 ms, 6-19 animated |
| MSAA 2x, culled | 115.4 (58) | 6.19 ms | 87% | 25% (29%) | 7.0/s | |
| MSAA 2x, culled, objects 60 m, particles 30 m | 116.9 (82) | 5.61 ms | 80% | 24% (28%) | 7.7/s | |
| Godot's separate render thread | 46.4 (40) | 7.75 ms | 54% | 54% (65%) | 78/s | 12-15 GC per second |

- About 110 of 120 FPS with full quality, 115-117 with MSAA 2x.
- Godot's own render timers (viewport render time measurement) on the Quest at 120 Hz, MSAA 4x: the main thread records a frame in about 1.0 ms (frame setup 0.2 ms), the GPU needs 7.4-8.6 ms per frame - right at the 8.3 ms budget. The missed frames are GPU frames running over, not the main thread (a first reading of VrApi's CPU-to-GPU latency suggested ~6 ms main thread; that number includes waiting).
- Godot's separate render thread is no help here (46 FPS, many garbage collections).
- Animation culling (hide and stop NPCs beyond the object distance, keep the pose outside the view, update far ones less often) cuts the bone work from ~2 ms to under 1 ms; the rest is the per-NPC distance and view check.
- Occlusion culling with occluders built at runtime (per 64 m cell, world triangles of at least 2 m²; Godot's ArrayOccluder3D) at 120 Hz, MSAA 4x: app GPU 6.85 -> 4.97 ms, draws 256-383 -> 20-131, FPS 112.8 -> 116.0, stale frames 10.8 -> 8.3 per second; the CPU pays ~0.35 ms per frame for it. With MSAA 2x on top: 4.67 ms GPU, 116.0 FPS, 6.4 stale per second.
- With the GPU at 71-74% the remaining 6-8 missed frames per second are spikes, not load: next is finding them (frame time histogram against garbage collections, AI state loops, the particle culler).

## 2026-11-07 - Daily routines and AI states on the Quest

The question: animations and daily routines cost time too. So the spike now runs the Old Camp's real NPCs the way Gothic-Unity's AiHandler does:
- G1's STARTUP_OLDCAMP inserts them (137, 134 within 150 m of the camp), each initialised from its Daedalus instance; the look comes from `Mdl_SetVisualBody` (body, head, armour item's visual).
- Each NPC's daily routine (`Rtn_*` -> `TA_*` -> `TA_Min`, 655 entries) places it at its current routine waypoint (12:00) and starts its AI state.
- State machine like AiHandler: state start, `_LOOP` when the action queue is empty and at most once per perception time, `_END`, next routine; active perception (assess player) on the perception timer when the hero is within 20 m.
- Actions from the scripts: `AI_PlayAni` (with the animation's follow-up), `AI_Wait`, `AI_GotoWP` (walking the waynet), `AI_StartState`; about 20 externals implemented, the rest default.

Quest 3, 72 Hz, objects to 80 m, particles to 40 m, 134 scripted NPCs:

| | Value |
|---|---|
| FPS | 71.8 average, 70 minimum |
| App GPU | 9.13 ms (77% busy) |
| CPU | 26% (busiest core 33%) |
| AI (scripts and actions) | 15-50 ms per second, 0.2-0.7 ms per frame |
| of that in the Daedalus VM | 10-29 ms per second |
| Script activity | 86-123 state loops, 6-9 perceptions, 3300-4900 externals per second |
| Animation (bone writes) | 2.3-2.6 ms per frame |
| Garbage collection | at most one small collection in a second |

The routines and states cost far less than feared (a few percent of one core). Per frame, the bone writes are the bigger CPU cost - the bulk bone path from [[Godot-Risks]] R4 is worth doing. Not in the load yet: NPCs perceiving each other (fights, gossip), dialogs, physics and collision of NPCs, sound, animation blending and overlays; most externals are still the cheap default.

## 2026-11-07 - Game logic on the Quest: Daedalus VM

GOTHIC.DAT in ZenKit's Daedalus VM, all 1524 dialog condition functions (DIA_*_CONDITION) called in a loop; externals land in C# (three typed ones as Gothic-Unity registers them, the rest in the default handler), so every external is a native to C# callback plus the P/Invokes ZenKit uses to read its arguments.

| Runtime | GOTHIC.DAT load | Condition calls | Per condition | Per external (with its script) |
|---|---|---|---|---|
| Desktop, CoreCLR | 21 ms | 874 per ms | 1.14 µs | 0.84 µs |
| Quest 3, MonoVM | 42 ms | 165 per ms | 6.04 µs | 4.43 µs |

| Quest 3, NativeAOT (linux-bionic) | 34 ms | 177 per ms | 5.64 µs | 4.14 µs |

5.3 times slower on the Quest than on the desktop, and NativeAOT wins only 7% over MonoVM: the native/managed transitions are not the cost, the Quest's CPU and ZenKit's own work per call are (the same that Gothic-Unity's IL2CPP build pays). Rough budget: 60 active NPCs running state loops with ~10 externals each at 72 Hz are ~43000 externals per second, about 190 ms per second, a fifth of one core. Workable. The place to win time back is fewer calls (ZenKit's C# binding reads every argument with its own P/Invoke), not the runtime.

NativeAOT on the Quest works (export preset "QuestAOT", NDK clang on PATH, `PublishAot` for linux-bionic, ZenKit kept from trimming; Godot logs a harmless "can't open libmonosgen" once). Old Camp at 72 Hz: 71.7 FPS, NPC bones 0.90 ms for 60 humans and 1.98 ms for 150 - about the same as MonoVM (1.0-1.2 ms and 1.94 ms). MonoVM is good enough; NativeAOT stays an option.

## 2026-11-07 - Quest 3: the Old Camp runs native

Godot 4.7.2 .NET (MonoVM), Mobile renderer, OpenXR vendors plugin 5.1.0, MSAA 4x, foveation 3, Old Camp with the whole G1 world, 8610 objects, 450 particle emitters, 60 animated humans, camera turning 18° per second (no game logic: no AI, scripts, audio).

First run (everything drawn as far as the world, 600 m):

| Refresh | FPS avg (min) | App GPU | GPU busy | CPU (busiest core) | Draws | Triangles |
|---|---|---|---|---|---|---|
| 72 Hz | 67.2 (36) | 12.0 ms | 89% | 21% (25%) | 293-590 | 302k-586k |
| 90 Hz | 78.3 (34) | 10.9 ms | 93% | 23% (28%) | 293-591 | 302k-587k |
| 120 Hz | 87.6 (30) | 10.1 ms | 96% | 22% (27%) | 291-590 | 301k-586k |

GPU bound everywhere. Taking parts away at 120 Hz (9.96 ms with everything): alpha tested objects (trees, bushes) cost 3.3 ms and most of the triangles, all objects 2.7 ms, particles 2.7 ms (every emitter is simulated every frame), humans 0.9 ms; the bare world alone takes 3.9 ms and holds 120 FPS. 32 m cells instead of 64 m made it worse (more draws, 11.6 ms).

With Gothic's own limits back (objects drawn up to 80 m from their 64 m cell, particle effects only within 40 m):

| Refresh | FPS avg (min) | App GPU | GPU busy | CPU (busiest core) | Draws | Triangles |
|---|---|---|---|---|---|---|
| 72 Hz | 71.5 (53) | 8.35 ms | 72% | 19% (24%) | 239-360 | 115k-238k |
| 90 Hz | 90.7 (89) | 7.35 ms | 77% | 22% (26%) | 239-360 | 115k-238k |
| 120 Hz | 115.8 (61) | 6.59 ms | 91% | 25% (31%) | 240-359 | 115k-238k |
| 120 Hz, MSAA 2x | 116.8 (67) | 5.88 ms | 84% | 27% (31%) | 240-360 | 115k-238k |
| 120 Hz, 150 humans | 111.0 (48) | 7.15 ms | 94% | 30% (48%) | 280-402 | 132k-279k |

- No garbage collection at all while running (0 collections per second), managed heap 1.2 GB after loading.
- Human bones from C# on MonoVM: 0.27-0.3 µs per call, 1.0-1.2 ms per frame for 60 humans, 1.9 ms for 150. Close to the desktop (CoreCLR) numbers.
- Loading on the Quest: world built in 6.2 s (from the raw Gothic files, no cache), app start to first frame 20.7 s.
- For comparison, Gothic-Unity on the same Quest 3 (2026-11-03, full game, other scene): G1 72 Hz average 70.4 FPS with app GPU 3.7-8.2 ms; G1 120 Hz about 91 FPS in the Old Camp at night.
- Quest launch traps found on the way: see [[Toolchain]] (prebuilt template has no OpenXR loader, controller check blocks launches, launch arguments are dropped, refresh rate property).

## 2026-11-07 - NPCs, mobs and fire

![Gate guard](images/old-camp-gate-guard.png)
![Guard on the palisade](images/old-camp-palisade-guard.png)
![Hut fire at dusk](images/old-camp-hut-fire-dusk.png)
![Shadow at a hut door](images/old-camp-hut-door.png)
![Castle torches at dusk](images/old-camp-castle-torches-dusk.png)
![Campfire with a scavenger on the spit](images/old-camp-campfire-dusk.png)

- **Humans**: HUMANS.MDH skeleton (34 bones) as a Skeleton3D, armours and bodies (.MDM soft skin) as skinned meshes with one Skin binding every node, heads (.MMB) on a BoneAttachment3D at "BIP01 HEAD", Gothic animations (.MAN: S_RUN, S_LGUARD, S_HGUARD, S_SIT) baked per frame and written bone by bone from C# every frame. Spawned at Old Camp waypoints, feet put on the ground by a ray.
- Armours are modelled in another pose than the skeleton: their soft skin positions are 13-19 cm (guard armour) or 3-4 cm (shadow armour) off the rest pose, only the naked body matches. The first renders used them and the guards' arms looked off. Vertices now come from the weights' own bone offsets (sum of weight x bone rest x offset), like the engine and Gothic-Unity's armour position cache.
- Conversions that held: bind poses from the hierarchy rest with the root at RootTranslation, animation rotations as `(x, y, -z, w)` of Gothic's stored quaternion (Gothic-Unity negates w; the z mirror flips x and y on top), root bone keeps only its height.
- **Mobs** (.ASC and .MDS visuals: beds, chairs, benches, chests, doors, torch holders, cauldrons): mesh attachments at their hierarchy nodes in rest pose, merged into the world cells like the static objects. 8610 objects of 351 meshes, 11 not found.
- **Particles**: torches, fires, smoke, coal glow from their C_ParticleFX instances in PARTICLEFX.DAT (450 emitters, all found), on GPUParticles3D in Gothic's units (cm to m, ms to s, velocity cm/ms x10, gravity cm/ms² x10000), animated textures as flipbook atlases, ADD/BLEND/MUL blending. Gothic-Unity scales emission and velocity by guessed factors; this maps the units directly.
- **Collision**: the merged world as Jolt trimesh shapes (979k triangles) built in about a second, used for ground snapping and to frame screenshots.

R4 measured (desktop, CoreCLR, one frame):

| Humans | Bone calls | Time | Per call |
|---|---|---|---|
| 60 | 3450 | 0.79 ms | 0.23 µs |
| 150 | 8598 | 1.2-1.5 ms | 0.14-0.17 µs |

Two calls per bone (position, rotation). On the Quest (MonoVM, slower cores) expect several times more; the bulk paths in [[Godot-Risks]] R4 get measured there.

## 2026-11-07 - Old Camp renders in Godot

First Gothic 1 world in Godot 4.7.2 (C#, Mobile renderer), desktop, rendered headless with gamescope.

![Old Camp from the north](images/old-camp-overview-north.png)
![Over the palisade](images/old-camp-palisade.png)
![Arena](images/old-camp-arena.png)
![Huts at the castle](images/old-camp-castle-huts.png)
![From the south](images/old-camp-overview-south.png)
![The valley from the mountains](images/old-camp-valley.png)

What is in it:
- World mesh from ZenKit, only the polygons the BSP leaves reference (like Gothic and Gothic-Unity), cut into 64 m cells per kind (opaque, alpha tested, water): 636 meshes, 107k triangles.
- 7214 static objects (.3DS / MRM), 271 shared meshes, merged into the world cells (or one node each with `--vob-mode=nodes`).
- One texture array (409 layers of 256 px, every texture scaled to the layer size) shared by everything.
- Gothic's baked vertex light on the world; objects take the light of the world below them (like Gothic's ground polygon light), plus a little sun shading.
- Procedural sky and thin fog instead of Gothic's sky layers, 1.5 km camera range.

Not in it: NPCs and animated models, mobs (.ASC), decals, particles, Gothic's sky, light maps of rooms, day time.

Findings:
- The world mesh holds 32565 polygons (of 138215) that no BSP leaf references: low detail copies overlapping the real geometry. The first renders drew them and got slanted slabs around the castle ("the height map gets funky around the center"). Drawing only BSP leaf polygons fixed it.
- ZenKit works unchanged in Godot C# (.NET 8 desktop): VFS, world, meshes, textures. Native library resolution through `NativeLibrary.SetDllImportResolver`.
- Loading on the desktop: world 1.1 s, world mesh 1 s, objects 0.3 s, texture array 0.1 s.
- Managed memory 910 MB after loading, mostly the cached ZenKit world mesh and the builder lists. Too much for a Quest; the port needs Gothic-Unity's static cache approach (or streaming).
- Draw calls in Old Camp views: 3600-4300 with one node per object, far above the Quest budget (400-600), as expected from [[Godot-Risks]] R2. Merging the objects into the world cells (their light baked into the vertex colour, one texture array) brings it to 218-580 with a 1.5 km view. Cost: 370k-710k triangles per view, as whole cells are drawn; Gothic's object fade distance (and lower detail far away) has to come back for the Quest. Merged load adds 0.8 s and 180 MB managed memory (1093 MB total).
- Godot's per-instance shader parameters share one global buffer: 7214 instances overflowed the default, raised `rendering/limits/global_shader_variables/buffer_size` to 262144.
- Coordinates: Gothic is left-handed, Godot right-handed. Mirroring z keeps the world readable; triangle corners are then reversed (world fans 0, p+1, p; mesh triangles w0, w2, w1) and rotations become `S * R * S` with `S = diag(1, 1, -1)`. Every position, rotation and animation in the port goes through this one conversion.

Quest pipeline (paused for the renders):
- The prebuilt Android export (no gradle) needs the C# project on net9.0 and builds (MonoVM, ZenKit and dmusic land in `lib/arm64-v8a`), but contains no OpenXR loader and no VR launcher category, so the Quest would start it as a flat panel.
- A Quest build needs the gradle build plus the OpenXR vendors plugin (5.1.0, Meta plugin enabled). SDK packages for that are installed; the gradle build is the next step.
- G1 data is on the Quest in the app's folder (copied on the device from Gothic-Unity's folder).

## 2026-11-07 - Start

- Repository created from Gothic-Unity main (5e2ca3f2b, after #476).
- Risk research written down: [[Godot-Risks]]. Spike defined: [[Spike-Plan]].
