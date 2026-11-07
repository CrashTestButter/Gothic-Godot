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

        /// <summary>A freepoint (zCVobSpot, e.g. FP_SMALLTALK_OC_01): a place to stand, sit or talk, one NPC at a time.</summary>
        public class FreePoint
        {
            public string Name;
            public Vector3 Position;
            public Vector3 Direction;
            public GothicNpc Occupant;
        }

        /// <summary>AI_GotoFP / AI_GotoNextFP: walk to the nearest free point of that name.</summary>
        public class GotoFpAction : Action
        {
            public string Name;
            public bool IsNext;
        }

        /// <summary>AI_AlignToFP, AI_TurnToNpc: face the freepoint's direction or another NPC.</summary>
        public class TurnAction : Action
        {
            public GothicNpc Target;
        }

        /// <summary>AI_OutputSVM: one of the voice's standard lines; an overlay doesn't hold the queue.</summary>
        public class SpeakAction : Action
        {
            public string Name;
            public bool IsOverlay;
            public double Seconds;
            public bool IsLoaded;
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
        public readonly Dictionary<int, int> TalentValues = new();
        public readonly Dictionary<int, int> TalentSkills = new();

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

        /// <summary>AI_StartState asked for this state (0: none); with IsPendingEndRun the old state's _END runs first.</summary>
        public int PendingState;
        public bool IsPendingEndRun;
        public readonly Dictionary<int, int> Perceptions = new();

        public readonly Queue<Action> Actions = new();
        public Action Current;
        public double ActionTime;
        public readonly List<Vector3> Path = new();
        public FreePoint CurrentFreePoint;

        /// <summary>The routine entry running now (-1: none); a new one ends the state and starts the next.</summary>
        public int RoutineIndex = -1;

        /// <summary>In a dialog with the hero: AI and routine wait, the dialog plays the animations.</summary>
        public bool IsInDialog;

        /// <summary>Unconscious or dead after the hero's hits.</summary>
        public bool IsDown;
        public bool IsDead;
        public double LastHitTime = -10;
        public Area3D Hitbox;

        // Fight: whom it fights, its equipped melee weapon (item instance), 0 none / 1 fists / 2 melee drawn.
        public GothicNpc Target;
        public string MeleeWeapon;
        public int FightMode;
        public Node3D WeaponNode;

        /// <summary>Its own attitude towards the hero (Npc_SetAttitude / Npc_SetTempAttitude), -1: not set.</summary>
        public int Attitude = -1;
        public int TempAttitude = -1;

        public Vector3 Position => Root.GlobalPosition;
    }
}
