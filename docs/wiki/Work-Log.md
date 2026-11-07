# Work log

Newest first.

## 2026-11-07 - Old Camp renders in Godot

First Gothic 1 world in Godot 4.7.2 (C#, Mobile renderer), desktop, rendered headless with gamescope.

![Old Camp from the north](images/old-camp-overview-north.png)
![Over the palisade](images/old-camp-palisade.png)
![Arena](images/old-camp-arena.png)
![Huts at the castle](images/old-camp-castle-huts.png)
![From the south](images/old-camp-overview-south.png)

What is in it:
- World mesh from ZenKit, only the polygons the BSP leaves reference (like Gothic and Gothic-Unity), cut into 64 m cells per kind (opaque, alpha tested, water): 636 meshes, 107k triangles.
- 7214 static objects (.3DS / MRM), 271 shared meshes, one node each.
- One texture array (409 layers of 256 px, every texture scaled to the layer size) shared by everything.
- Gothic's baked vertex light on the world; objects take the light of the world below them (like Gothic's ground polygon light), plus a little sun shading.
- Procedural sky and fog instead of Gothic's sky layers.

Not in it: NPCs and animated models, mobs (.ASC), decals, particles, Gothic's sky, light maps of rooms, day time.

Findings:
- The world mesh holds 32565 polygons (of 138215) that no BSP leaf references: low detail copies overlapping the real geometry. The first renders drew them and got slanted slabs around the castle ("the height map gets funky around the center"). Drawing only BSP leaf polygons fixed it.
- ZenKit works unchanged in Godot C# (.NET 8 desktop): VFS, world, meshes, textures. Native library resolution through `NativeLibrary.SetDllImportResolver`.
- Loading on the desktop: world 1.1 s, world mesh 1 s, objects 0.3 s, texture array 0.1 s.
- Managed memory 910 MB after loading, mostly the cached ZenKit world mesh and the builder lists. Too much for a Quest; the port needs Gothic-Unity's static cache approach (or streaming).
- Draw calls in Old Camp views: 3600-4300 with one node per object. Far above the Quest budget (400-600), as expected from [[Godot-Risks]] R2. Next: merge static objects per cell and material, MultiMesh for repeated ones.
- Godot's per-instance shader parameters share one global buffer: 7214 instances overflowed the default, raised `rendering/limits/global_shader_variables/buffer_size` to 262144.
- Coordinates: Gothic is left-handed, Godot right-handed. Mirroring z keeps the world readable; triangle corners are then reversed (world fans 0, p+1, p; mesh triangles w0, w2, w1) and rotations become `S * R * S` with `S = diag(1, 1, -1)`. Every position, rotation and animation in the port goes through this one conversion.

Quest pipeline (paused for the renders):
- The prebuilt Android export (no gradle) needs the C# project on net9.0 and builds (MonoVM, ZenKit and dmusic land in `lib/arm64-v8a`), but contains no OpenXR loader and no VR launcher category, so the Quest would start it as a flat panel.
- A Quest build needs the gradle build plus the OpenXR vendors plugin (5.1.0, Meta plugin enabled). SDK packages for that are installed; the gradle build is the next step.
- G1 data is on the Quest in the app's folder (copied on the device from Gothic-Unity's folder).

## 2026-11-07 - Start

- Repository created from Gothic-Unity main (5e2ca3f2b, after #476).
- Risk research written down: [[Godot-Risks]]. Spike defined: [[Spike-Plan]].
