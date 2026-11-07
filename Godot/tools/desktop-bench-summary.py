"""Averages the spike's [Perf] and [Ai] lines of a desktop run (skips the first seconds after loading)."""
import re
import statistics as st
import sys

lines = open(sys.argv[1], errors="ignore").read().splitlines()
skip = int(sys.argv[2]) if len(sys.argv) > 2 else 8
perf = [l for l in lines if "[Perf]" in l][skip:]
ai = [l for l in lines if re.search(r"\[Ai\] \d+ NPCs", l)][skip:]


def avg(pattern, source):
    found = [float(m.group(1).replace(",", ".")) for l in source for m in [re.search(pattern, l)] if m]
    return (st.mean(found), min(found), max(found)) if found else (0, 0, 0)


gpu = avg(r"render cpu [\d.,]+ gpu ([\d.,]+)", perf)
cpu = avg(r"render cpu ([\d.,]+)", perf)
draws = avg(r"draws (\d+)", perf)
prims = avg(r"prims (\d+)", perf)
fps = avg(r"fps ([\d.,]+)", perf)
print(f"samples {len(perf)}: fps {fps[0]:.0f}, render gpu {gpu[0]:.2f} ms ({gpu[1]:.2f}-{gpu[2]:.2f}), "
      f"render cpu {cpu[0]:.2f} ms, draws {draws[0]:.0f} ({draws[1]:.0f}-{draws[2]:.0f}), "
      f"triangles {prims[0] / 1000:.0f}k ({prims[1] / 1000:.0f}k-{prims[2] / 1000:.0f}k)")
if ai:
    ai_ms = avg(r"AI ([\d.,]+) ms per frame", ai)
    vm_s = avg(r"; [\d.,]+ / ([\d.,]+) ms per second", ai)
    ai_s = avg(r"; ([\d.,]+) / [\d.,]+ ms per second", ai)
    anim = avg(r"animation ([\d.,]+) ms", ai)
    animated = avg(r"\((\d+) animated\)", ai)
    npcs = re.search(r"\[Ai\] (\d+) NPCs", ai[-1]).group(1)
    print(f"{npcs} NPCs: AI {ai_ms[0]:.2f} ms per frame ({ai_s[0]:.1f} ms per second, VM {vm_s[0]:.1f}), "
          f"animation {anim[0]:.2f} ms per frame ({animated[0]:.0f} animated)")
