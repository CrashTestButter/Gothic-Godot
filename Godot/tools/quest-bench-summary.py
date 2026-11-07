"""Summarises a Quest logcat capture: VrApi frame stats of the app's process and the spike's [Perf]/[Npc] lines."""
import re
import statistics as st
import sys

log, pid = sys.argv[1], sys.argv[2].strip()
lines = open(log, errors="ignore").read().splitlines()
vr = [l for l in lines if "VrApi" in l and re.search(r"\(\s*%s\)" % re.escape(pid), l)]


def values(pattern, source, cast=float):
    found = []
    for line in source:
        match = re.search(pattern, line)
        if match:
            found.append(cast(match.group(1).replace(",", ".")))
    return found


fps = values(r"FPS=(\d+)/", vr, int)
hz = values(r"FPS=\d+/(\d+)", vr, int)
app = values(r"App=([\d.]+)ms", vr)
gpu = values(r"GPU%=([\d.]+)", vr)
cpu = values(r"CPU%=([\d.]+)", vr)
core = values(r"CPU%=[\d.]+\(W([\d.]+)\)", vr)
stale = values(r"Stale=(\d+)", vr, int)
perf = [l for l in lines if "[Perf]" in l]
npc = [l for l in lines if "[Npc]" in l]
if not fps:
    print("no VrApi lines for pid", pid)
    sys.exit(1)
print(f"{hz[0]} Hz: FPS avg {st.mean(fps):.1f} (min {min(fps)}), stale {st.mean(stale):.1f}/s, "
      f"app GPU {st.mean(app):.2f} ms (max {max(app):.2f}), GPU {st.mean(gpu) * 100:.0f}%, "
      f"CPU {st.mean(cpu) * 100:.0f}% (busiest core {st.mean(core) * 100:.0f}%)")
if perf:
    draws = values(r"draws (\d+)", perf, int)
    prims = values(r"prims (\d+)", perf, int)
    worst = values(r"maxFrame ([\d,]+)", perf)
    gc = sorted({l.split(" gc ")[1].split(" ")[0] for l in perf})
    print(f"draws {min(draws)}-{max(draws)}, triangles {min(prims) // 1000}k-{max(prims) // 1000}k, "
          f"worst frame {max(worst):.1f} ms, gc per second {gc}")
if npc:
    bones = values(r": ([\d,]+) ms per frame", npc)
    print(f"npc bones {st.mean(bones):.2f} ms per frame ({npc[-1].split('[Npc] ')[1].split(':')[0]})")
