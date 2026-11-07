namespace GothicGodot.Interaction
{
    /// <summary>The physics layers (bits) of the port.</summary>
    public static class PhysicsLayers
    {
        /// <summary>The world and the objects Gothic marks cdStatic.</summary>
        public const uint World = 1 << 0;

        /// <summary>The player's body.</summary>
        public const uint Player = 1 << 1;

        /// <summary>Items the hands can take; they land on the world, the player walks through them.</summary>
        public const uint Items = 1 << 3;
    }
}
