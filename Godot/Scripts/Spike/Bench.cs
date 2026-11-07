using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Benchmark (--bench=true): the player stands at fixed waypoints around the start, looking along the four
    /// directions at each, and the frames there are measured - draw calls, triangles, render and frame setup CPU,
    /// AI, animation, physics. Prints one summary line, then quits. The same numbers on every run for comparing
    /// optimizations (draw calls and triangles stand in for the Quest's GPU and render thread).
    /// </summary>
    public partial class Bench : Node
    {
        private const int _settleFrames = 20;
        private const int _measureFrames = 10;
        private const double _startSeconds = 3;

        private GothicPlayer _player;
        private List<Vector3> _views;
        private int _sample = -1;
        private int _frame;
        private double _time;
        private readonly List<double> _draws = new();
        private readonly List<double> _prims = new();
        private double _renderCpu;
        private double _setupCpu;
        private double _ai;
        private double _animation;
        private double _physics;
        private double _playerPhysics;
        private int _frames;

        /// <summary>When the load ended (ms since start), for the summary.</summary>
        public long LoadMs { get; set; }

        private string _shots;

        /// <param name="shots">A folder for a screenshot of each view's first direction (empty: none).</param>
        public void Init(GothicPlayer player, IEnumerable<Vector3> views, string shots = "")
        {
            _player = player;
            _views = views.ToList();
            _shots = shots;
        }

        public override void _Ready()
        {
            // After the AI (it fills FrameEvents) and before PerfLog (it clears them).
            ProcessPriority = int.MaxValue - 1;
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        }

        public override void _Process(double delta)
        {
            if (_player == null || !_player.IsPlaced)
                return;
            if (_sample < 0)
            {
                _time += delta;
                if (_time < _startSeconds)
                    return;
                _sample = 0;
                _frame = 0;
            }

            var view = _sample / 4;
            if (view >= _views.Count)
            {
                Finish();
                return;
            }

            if (_frame == 0)
            {
                var yaw = _sample % 4 * Mathf.Pi / 2;
                _player.PlaceAt(_views[view], yaw);
                var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
                _player.LookAtPoint(_views[view] + Vector3.Up * 1.6f + forward * 10f);
            }
            else if (_frame > _settleFrames)
            {
                var viewport = GetViewport().GetViewportRid();
                _draws.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
                _prims.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
                _renderCpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
                _setupCpu += RenderingServer.GetFrameSetupTimeCpu();
                _ai += FrameEvents.AiMs;
                _animation += FrameEvents.AnimationMs;
                _physics += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
                _playerPhysics += FrameEvents.PlayerPhysicsMs;
                _frames++;
            }

            FrameEvents.PlayerPhysicsMs = 0;
            _frame++;
            if (_frame > _settleFrames + _measureFrames)
            {
                if (!string.IsNullOrEmpty(_shots) && _sample % 4 == 0)
                {
                    System.IO.Directory.CreateDirectory(_shots);
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_shots, $"view{view:00}.png"));
                }

                _sample++;
                _frame = 0;
            }
        }

        private void Finish()
        {
            var frames = Math.Max(1, _frames);
            GD.Print($"[Bench] {_views.Count} views x 4: draws avg {_draws.Average():F0} max {_draws.Max():F0}, " +
                     $"triangles avg {_prims.Average() / 1000:F0}k max {_prims.Max() / 1000:F0}k, " +
                     $"render cpu {_renderCpu / frames:F2} ms, setup {_setupCpu / frames:F2} ms, " +
                     $"AI {_ai / frames:F3} ms, animation {_animation / frames:F3} ms, physics {_physics / frames:F2} ms (player {_playerPhysics / frames:F2}), " +
                     $"load {LoadMs} ms, static {OS.GetStaticMemoryUsage() / 1048576} MB, " +
                     $"video {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} MB");
            GetTree().Quit();
        }
    }
}
