namespace GothicGodot.Spike
{
    /// <summary>
    /// What the spike's systems did in the current frame, so PerfLog can tell what long frames had in common.
    /// </summary>
    public static class FrameEvents
    {
        public static int StateLoops;
        public static double AiMs;
        public static double AnimationMs;
        public static bool IsPfxCulled;

        public static void Clear()
        {
            StateLoops = 0;
            AiMs = 0;
            AnimationMs = 0;
            IsPfxCulled = false;
        }
    }
}
