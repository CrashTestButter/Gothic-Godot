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

        /// <summary>The clip whose full pose is on the skeleton (then only its moving bones are written).</summary>
        public ModelFactory.Clip AppliedClip;
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

        private Vector3 _position;

        /// <summary>
        /// Where it stands, kept here: the AI moves NPCs itself, and reading the scene's GlobalPosition (an engine call)
        /// was a quarter of the AI's managed time in Khorinis. The hero's is synced from the player once per frame.
        /// </summary>
        public Vector3 Position => _position;

        private bool _isVisible = true;

        /// <summary>Shown: it has a body and is in animation range; kept here for the same reason.</summary>
        public bool IsShown => Root != null && _isVisible;

        /// <summary>Its body (skeleton, mesh, hitbox) exists: only near the camera, like the engine's spawn range.</summary>
        public bool HasBody => Root != null;

        /// <summary>
        /// In the world with its AI running, near the hero. The others only follow their routine: at each switch
        /// they are put at the new entry's waypoint, like the engine's routine manager does for NPCs out of range.
        /// </summary>
        public bool IsActive;

        /// <summary>Never in the world: no model, no body or no waypoint to stand at.</summary>
        public bool IsLeftOut;

        private bool _isPositionDirty;

        /// <summary>
        /// Hidden NPCs only remember where they are; the scene node moves when they are shown again (moving nodes was
        /// the next biggest cost). The AI node sits at the origin, so the local position is the world position.
        /// </summary>
        public void SetPosition(Vector3 position)
        {
            _position = position;
            if (!IsShown)
            {
                _isPositionDirty = true;
                return;
            }

            Root.Position = position;
        }

        public void SyncPosition()
        {
            if (Root != null)
                _position = Root.GlobalPosition;
        }

        private float _yaw;
        private bool _isYawDirty;

        /// <summary>Its facing (radians around up, 0 = -Z), kept like the position.</summary>
        public float Yaw => _yaw;
        public Vector3 Forward => new(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));

        public void SetYaw(float yaw)
        {
            if (Mathf.Abs(yaw - _yaw) < 0.0001f && !_isYawDirty)
                return;
            _yaw = yaw;
            if (!IsShown)
            {
                _isYawDirty = true;
                return;
            }

            Root.Rotation = new Vector3(0, yaw, 0);
            _isYawDirty = false;
        }

        /// <summary>Turns it to look along the direction (ignoring up and down).</summary>
        public void FaceTowards(Vector3 direction)
        {
            if (direction.X * direction.X + direction.Z * direction.Z > 1e-6f)
                SetYaw(Mathf.Atan2(-direction.X, -direction.Z));
        }

        public void SetShown(bool isShown)
        {
            if (Root == null)
            {
                _isVisible = isShown;
                return;
            }

            if (_isVisible == isShown)
                return;
            _isVisible = isShown;
            Root.Visible = isShown;
            if (isShown && _isPositionDirty)
            {
                Root.Position = _position;
                _isPositionDirty = false;
            }

            if (isShown && _isYawDirty)
            {
                Root.Rotation = new Vector3(0, _yaw, 0);
                _isYawDirty = false;
            }
        }

        /// <summary>Its body was made: it stands where the NPC is and plays its clip from the full pose.</summary>
        public void AttachBody(Node3D root)
        {
            Root = root;
            Root.Position = _position;
            Root.Rotation = new Vector3(0, _yaw, 0);
            Root.Visible = _isVisible;
            _isPositionDirty = false;
            _isYawDirty = false;
            AppliedClip = null;
        }

        /// <summary>Its body was taken away (out of range); the NPC itself goes on.</summary>
        public void DetachBody()
        {
            Root = null;
            Skeleton = null;
            Hitbox = null;
            WeaponNode = null;
            AppliedClip = null;
        }
    }
}
