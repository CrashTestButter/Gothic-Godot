using System.Collections.Generic;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Runs particle effects only near the camera (Gothic culls effects by distance too): emitters further than
    /// the far distance stop emitting and hide, so the GPU neither simulates nor draws them. Nearer, fewer particles
    /// further away.
    /// </summary>
    public partial class PfxCuller : Node
    {
        private const double _interval = 0.25;

        private readonly List<GpuParticles3D> _emitters;
        private readonly float _farSquared;
        private double _time;

        public PfxCuller(List<GpuParticles3D> emitters, float far)
        {
            _emitters = emitters;
            _farSquared = far * far;
        }

        public PfxCuller()
        {
        }

        public override void _Process(double delta)
        {
            _time += delta;
            if (_emitters == null || _time < _interval)
                return;
            _time = 0;

            var camera = GetViewport().GetCamera3D();
            if (camera == null)
                return;

            FrameEvents.IsPfxCulled = true;
            var eye = camera.GlobalPosition;
            foreach (var emitter in _emitters)
            {
                var distanceSquared = emitter.GlobalPosition.DistanceSquaredTo(eye);
                var isNear = distanceSquared < _farSquared;
                if (emitter.Emitting != isNear)
                {
                    emitter.Emitting = isNear;
                    emitter.Visible = isNear;
                }

                // Fewer particles further away: fires and smoke are overdraw, the Quest's tile GPU pays for each layer.
                var ratio = distanceSquared < 15f * 15f ? 1f : distanceSquared < 25f * 25f ? 0.6f : 0.35f;
                if (isNear && !Mathf.IsEqualApprox(emitter.AmountRatio, ratio))
                    emitter.AmountRatio = ratio;
            }
        }
    }
}
