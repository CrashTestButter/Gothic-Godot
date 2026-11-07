using System;
using System.Diagnostics;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Prints one line per second: frame times, draw calls, objects, garbage collections. Read on the Quest with
    /// `adb logcat -s godot`.
    /// </summary>
    public partial class PerfLog : Node
    {
        private double _elapsed;
        private int _frames;
        private double _maxFrameMs;
        private int _gen0;
        private int _gen1;
        private int _gen2;
        private TimeSpan _gcPause;
        private readonly Stopwatch _frameWatch = Stopwatch.StartNew();

        private double _renderCpuMs;
        private double _renderGpuMs;
        private double _setupCpuMs;
        private double _processMs;
        private int _longFrames;
        private int _longWithGc;
        private int _longWithLoops;
        private int _longWithPfx;
        private int _longWithAi;
        private double _longAiMs;
        private double _longAnimationMs;
        private int _lastGen0;

        public override void _Ready()
        {
            ProcessPriority = int.MaxValue;
            // Godot's own render timing: CPU time to record the viewport's frame, GPU time, frame setup.
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            _gen0 = GC.CollectionCount(0);
            _gen1 = GC.CollectionCount(1);
            _gen2 = GC.CollectionCount(2);
            _gcPause = GC.GetTotalPauseDuration();
        }

        public override void _Process(double delta)
        {
            var frameMs = _frameWatch.Elapsed.TotalMilliseconds;
            _frameWatch.Restart();
            _maxFrameMs = Math.Max(_maxFrameMs, frameMs);

            // A frame longer than 1.5 refresh intervals missed its slot: what did it have?
            var target = 1000.0 / Math.Max(60.0, Engine.GetFramesPerSecond() > 100 ? 120.0 : Engine.GetFramesPerSecond() > 80 ? 90.0 : 72.0);
            var gen0Now = GC.CollectionCount(0);
            if (frameMs > target * 1.5)
            {
                _longFrames++;
                if (gen0Now != _lastGen0)
                    _longWithGc++;
                if (FrameEvents.StateLoops > 0)
                    _longWithLoops++;
                if (FrameEvents.IsPfxCulled)
                    _longWithPfx++;
                if (FrameEvents.AiMs + FrameEvents.AnimationMs > 2.0)
                    _longWithAi++;
                _longAiMs += FrameEvents.AiMs;
                _longAnimationMs += FrameEvents.AnimationMs;
            }

            _lastGen0 = gen0Now;
            FrameEvents.Clear();
            var viewport = GetViewport().GetViewportRid();
            _renderCpuMs += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            _renderGpuMs += RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
            _setupCpuMs += RenderingServer.GetFrameSetupTimeCpu();
            _processMs += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
            _elapsed += delta;
            _frames++;
            if (_elapsed < 1.0)
                return;

            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var gcPause = GC.GetTotalPauseDuration();

            GD.Print($"[Perf] fps {_frames / _elapsed:F1} maxFrame {_maxFrameMs:F1} ms" +
                     $" process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F2} ms" +
                     $" draws {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}" +
                     $" objects {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)}" +
                     $" prims {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)}" +
                     $" gc {gen0 - _gen0}/{gen1 - _gen1}/{gen2 - _gen2} pause {(gcPause - _gcPause).TotalMilliseconds:F2} ms" +
                     $" managed {GC.GetTotalMemory(false) / 1048576} MB static {OS.GetStaticMemoryUsage() / 1048576} MB" +
                     $" | render cpu {_renderCpuMs / _frames:F2} gpu {_renderGpuMs / _frames:F2} setup {_setupCpuMs / _frames:F2}" +
                     $" process avg {_processMs / _frames:F2} ms" +
                     $" | long {_longFrames} (gc {_longWithGc}, state loops {_longWithLoops}, pfx culler {_longWithPfx}," +
                     $" ai+anim>2ms {_longWithAi}, avg ai {(_longFrames > 0 ? _longAiMs / _longFrames : 0):F2}" +
                     $" anim {(_longFrames > 0 ? _longAnimationMs / _longFrames : 0):F2} ms)");
            _renderCpuMs = _renderGpuMs = _setupCpuMs = _processMs = 0;
            _longFrames = _longWithGc = _longWithLoops = _longWithPfx = _longWithAi = 0;
            _longAiMs = _longAnimationMs = 0;

            _gen0 = gen0;
            _gen1 = gen1;
            _gen2 = gen2;
            _gcPause = gcPause;
            _elapsed = 0;
            _frames = 0;
            _maxFrameMs = 0;
        }
    }
}
