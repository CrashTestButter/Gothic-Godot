# Porting strategy

Open question: how do 88k lines of Unity C# move to Godot while the Gothic-Unity team keeps developing?

## Options

1. **Clean port**: rewrite on Godot types, take upstream changes by hand. Fast start, falls behind upstream quickly.
2. **Engine-neutral core upstream**: move Gothic-Unity's Models, Domain and Services off `UnityEngine` types (System.Numerics, own interfaces for scene objects), so both engines compile the same files. Best long term, needs the team's agreement and many upstream PRs.
3. **Unity API shim**: a small assembly with the Unity types the core uses (`Vector3`, `Quaternion`, `Mathf`, `Color`, `Bounds`, ...) implemented on Godot, so unchanged upstream files compile. Scene objects (`GameObject`, `Transform`, `MonoBehaviour`) are the hard part.

## Measure first

- Which upstream files compile unchanged with only a value-type shim?
- Which need scene objects, and how many of those are adapters (rewritten anyway)?

Result and decision: see [[Work-Log]].
