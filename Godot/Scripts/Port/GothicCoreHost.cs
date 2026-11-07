using Godot;

namespace GothicGodot.Port
{
    /// <summary>
    /// Connects Gothic-Unity's shared core (Gothic.Core assembly with the Unity shim) to Godot: its log output goes to
    /// Godot's output, its Time follows Godot's frames. Add once, early in the tree.
    /// </summary>
    public partial class GothicCoreHost : Node
    {
        public override void _EnterTree()
        {
            ProcessPriority = int.MinValue;
            UnityEngine.Debug.LogSink = message => GD.Print(message);
            UnityEngine.Debug.WarningSink = message => GD.PushWarning(message);
            UnityEngine.Debug.ErrorSink = message => GD.PushError(message);
        }

        public override void _Process(double delta)
        {
            UnityEngine.Time.Advance((float)delta);
        }
    }
}
