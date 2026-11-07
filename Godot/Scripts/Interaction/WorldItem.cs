using Godot;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// An item lying in the world (oCItem): frozen where Gothic put it until a hand takes it. Held, it follows the
    /// hand kinematically; let go, it gets the hand's velocity and falls with physics.
    /// </summary>
    public partial class WorldItem : RigidBody3D
    {
        public string Instance;

        /// <summary>How many this body stands for (a chest's stack of 20 nuggets is one body).</summary>
        public int Amount = 1;
        public bool IsHeld { get; private set; }

        /// <param name="far">Hidden beyond this many metres (0: always drawn); items are small, each one is a draw.</param>
        public void Setup(Mesh mesh, Shape3D shape, Vector3 shapeOffset, Color tint, float far)
        {
            CollisionLayer = PhysicsLayers.Items;
            CollisionMask = PhysicsLayers.World | PhysicsLayers.Mobs | PhysicsLayers.Items;
            Mass = 0.5f;
            FreezeMode = FreezeModeEnum.Kinematic;
            Freeze = true;
            ContinuousCd = true;
            var instance = new MeshInstance3D
            {
                Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityRangeEnd = far
            };
            AddChild(instance);
            instance.SetInstanceShaderParameter("tint", tint);
            AddChild(new CollisionShape3D { Shape = shape, Position = shapeOffset });
        }

        public void Take()
        {
            IsHeld = true;
            Freeze = true;
        }

        public void Release(Vector3 velocity, Vector3 angularVelocity)
        {
            IsHeld = false;
            Freeze = false;
            LinearVelocity = velocity;
            AngularVelocity = angularVelocity;
        }
    }
}
