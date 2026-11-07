# Gothic-Godot

Gothic-Godot is a port study of [Gothic-Unity](https://github.com/Gothic-Unity-Project/Gothic-Unity) to the Godot 4 engine (C#). Gothic-Unity is the community rebuild of Gothic 1 and 2 with a native VR focus. It loads all game assets at runtime from the player's own Gothic installation through ZenKit.

Why: Gothic-Unity depends on one vendor's decisions (licence terms, runtime fees, platform support). Godot is MIT licensed, runs C#, and has OpenXR for Quest, Pico and PC VR built in. This repository finds out what a move would cost, which problems block it, and how to fix them, partly in forks of upstream projects whose fixes we try to bring back upstream.

The goal stays the same as in Gothic-Unity: Gothic 1 and 2 smooth and native on a Quest 2 / Quest 3, plus PC VR.

## Pages

- [[Godot-Risks]] - what could block a Godot port, with sources
- [[Spike-Plan]] - the measurements that decide go / no-go
- [[Porting-Strategy]] - how the 88k lines of C# move, and how we stay in sync with Gothic-Unity
- [[Upstream-Forks]] - Godot, XR Tools and other projects we patch, and what we send back
- [[Toolchain]] - how to build, run and deploy to the Quest
- [[Work-Log]] - what was done, measured and decided, newest first

## Repository layout

- `main` - Gothic-Unity's history plus the Godot work on top
- `upstream-main` - Gothic-Unity's main as it was forked, for merging new upstream work
- `Godot/` - the Godot project (spikes first, then the port)
- `docs/wiki/` - these pages (GitHub wikis need a public repository on the free plan)

## Status

Research done, spike in progress. See [[Work-Log]].
