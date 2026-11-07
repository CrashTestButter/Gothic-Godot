# Porting strategy

Open question: how do 88k lines of Unity C# move to Godot while the Gothic-Unity team keeps developing?

## Options

1. **Clean port**: rewrite on Godot types, take upstream changes by hand. Fast start, falls behind upstream quickly.
2. **Engine-neutral core upstream**: move Gothic-Unity's Models, Domain and Services off `UnityEngine` types (System.Numerics, own interfaces for scene objects), so both engines compile the same files. Best long term, needs the team's agreement and many upstream PRs.
3. **Unity API shim**: a small assembly with the Unity types the core uses (`Vector3`, `Quaternion`, `Mathf`, `Color`, `Bounds`, ...) implemented on Godot, so unchanged upstream files compile. Scene objects (`GameObject`, `Transform`, `MonoBehaviour`) are the hard part.

## First measurement (2026-11-07, upstream main 5e2ca3f2b)

Gothic-Core's 359 C# files by what they take from Unity (text scan of the types they name, not a compile):

| Layer | No Unity | Unity values only (math, colours) | Unity scene objects | Burst / jobs |
|---|---|---|---|---|
| Models | 48 | 6 | 6 | 3 |
| Domain | 36 | 15 | 42 | 3 |
| Services | 22 | 7 | 48 | 1 |
| Adapters | 27 | 2 | 53 | 1 |
| Other (Bink, Extensions, Const, Logging, Debugging) | 19 | 1 | 19 | 0 |
| **Total** | **152** | **31** | **168** | **8** |

- About half (183 files) would compile on Godot with no change or a small value-type shim (`Vector3`, `Quaternion`, `Mathf`, `Color`, `Matrix4x4`, `Bounds`).
- Adapters are rewritten in any engine port; the problem is the 90 Domain and Services files that hold `GameObject`, `Transform`, meshes or materials. They are the coupling Gothic-Unity's own architecture rule ("logic belongs in services, adapters are thin") already wants gone.
- Gothic-Unity's own Bink decoder, the logging and most models are already engine free.

Leaning: option 2 for Domain and Services (move scene objects behind small interfaces upstream, one area at a time, as normal Gothic-Unity refactors that also help its architecture tests), option 3 (value-type shim) only as a bridge. To be confirmed with a real compile of the 183 files against a shim.
