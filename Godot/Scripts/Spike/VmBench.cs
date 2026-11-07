using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using ZenKit;
using ZenKit.Daedalus;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Game logic cost on this runtime: GOTHIC.DAT in ZenKit's Daedalus VM, every dialog condition function
    /// (DIA_*_CONDITION, what Gothic checks for each NPC near the hero) called in a loop. Externals they use land
    /// in C# - a few typed ones like Gothic-Unity registers them, the rest in the default handler - so the numbers
    /// include the native to C# callbacks (reverse P/Invoke), the part that differs most between runtimes.
    /// </summary>
    public static class VmBench
    {
        public static void Run(Vfs vfs, double milliseconds)
        {
            var load = Stopwatch.StartNew();
            var vm = new DaedalusVm(vfs, "GOTHIC.DAT",
                (byte)(DaedalusExecutionFlags.AllowNullInstanceAccess | DaedalusExecutionFlags.IgnoreConstSpecifier));
            var loadMs = load.Elapsed.TotalMilliseconds;

            long defaultExternals = 0;
            long typedExternals = 0;
            vm.RegisterExternalDefault((_, _) => defaultExternals++);
            vm.RegisterExternal<int, NpcInstance, int>("Npc_KnowsInfo", (_, _) =>
            {
                typedExternals++;
                return 0;
            });
            vm.RegisterExternal<int, NpcInstance, int>("Npc_GetTalentSkill", (_, _) =>
            {
                typedExternals++;
                return 0;
            });
            vm.RegisterExternal<int, NpcInstance>("Npc_IsDead", _ =>
            {
                typedExternals++;
                return 0;
            });

            var conditions = vm.Symbols
                .Where(s => s.Type == DaedalusDataType.Function && !s.IsExternal &&
                            s.Name.EndsWith("_CONDITION", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Index)
                .ToArray();

            // Warm up (JIT, caches), then measure.
            foreach (var index in conditions)
                vm.Call<int>(index);

            defaultExternals = 0;
            typedExternals = 0;
            long calls = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < milliseconds)
            {
                foreach (var index in conditions)
                    vm.Call<int>(index);
                calls += conditions.Length;
            }

            var elapsed = watch.Elapsed.TotalMilliseconds;
            var externals = defaultExternals + typedExternals;
            GD.Print($"[Vm] GOTHIC.DAT loaded in {loadMs:F0} ms, {vm.SymbolCount} symbols, {conditions.Length} conditions; " +
                     $"{calls / elapsed:F0} condition calls per ms ({elapsed * 1000.0 / calls:F2} µs each), " +
                     $"{externals / elapsed:F0} externals per ms ({typedExternals} typed, {defaultExternals} default), " +
                     $"{(externals > 0 ? elapsed * 1000.0 / externals : 0):F2} µs per external incl. the script around it");
        }
    }
}
