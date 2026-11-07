using System.Collections.Generic;
using Godot;
using ZenKit.Daedalus;

namespace GothicGodot.Spike
{
    /// <summary>
    /// One scripted Gothic NPC of the AI load test: its Daedalus instance, look, daily routine, AI state, action
    /// queue and animation.
    /// </summary>
    public class GothicNpc
    {
        public enum LoopState
        {
            None,
            Start,
            Loop,
            End,
            AfterEnd
        }

        public struct RoutineEntry
        {
            public int StartMinute;
            public int EndMinute;
            public int State;
            public string Waypoint;
        }

        public abstract class Action
        {
        }

        public class PlayAniAction : Action
        {
            public string Name;
        }

        public class WaitAction : Action
        {
            public float Seconds;
        }

        public class GotoAction : Action
        {
            public string Waypoint;
        }

        public NpcInstance Instance;
        public string SymbolName;
        public string Name;
        public string Visual;
        public string Body = "HUM_BODY_NAKED0";
        public string Head;
        public int BodyTexture;
        public int SkinColor;
        public int HeadTexture;
        public int ArmorInstance = -1;
        public string SpawnWaypoint;
        public readonly List<RoutineEntry> Routine = new();
        public readonly Interaction.Inventory Inventory = new();

        public ModelFactory Factory;
        public string IdleClip = "S_RUN";
        public string WalkClip = "S_WALKL";
        public Node3D Root;
        public Skeleton3D Skeleton;

        // Animation
        public ModelFactory.Clip Clip;
        public double ClipTime;
        public bool IsClipLooping = true;

        // AI state like Gothic-Unity's AiHandler
        public LoopState State = LoopState.None;
        public int StateMain;
        public int StateLoop;
        public int StateEnd;
        public double NextLoopTime;
        public double StateTime;
        public float PerceptionTime = 5f;
        public double NextPerceptionTime;
        public readonly Dictionary<int, int> Perceptions = new();

        public readonly Queue<Action> Actions = new();
        public Action Current;
        public double ActionTime;
        public readonly List<Vector3> Path = new();

        /// <summary>In a dialog with the hero: AI and routine wait, the dialog plays the animations.</summary>
        public bool IsInDialog;

        public Vector3 Position => Root.GlobalPosition;
    }
}
