using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using ZenKit;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Spawns Gothic humans at Old Camp waypoints and plays a looping animation on each, sampled in C# and written
    /// bone by bone into the Skeleton3D every frame - the C# to engine call cost of Gothic-Unity's own animation
    /// system on Godot (risk R4). Logs the time of the bone writes once per second.
    /// </summary>
    public partial class HumanCrowd : Node3D
    {
        private static readonly string[] _guardArmors = { "HUM_GRDM_ARMOR.MDM", "HUM_GRDL_ARMOR.MDM", "HUM_GRDS_ARMOR.MDM" };
        private static readonly string[] _campArmors =
            { "HUM_VLKL_ARMOR.MDM", "HUM_VLKM_ARMOR.MDM", "HUM_STTL_ARMOR.MDM", "HUM_STTM_ARMOR.MDM", "HUM_STTS_ARMOR.MDM", "HUM_BODY_NAKED0.MDM" };
        private static readonly string[] _heads =
            { "HUM_HEAD_BALD.MMB", "HUM_HEAD_FIGHTER.MMB", "HUM_HEAD_PONY.MMB", "HUM_HEAD_PSIONIC.MMB", "HUM_HEAD_THIEF.MMB", "HUM_HEAD_FATBALD.MMB" };
        private static readonly string[] _g2GuardArmors = { "ARMOR_MIL_L.MDM", "ARMOR_MIL_M.MDM", "ARMOR_PAL_M.MDM" };
        private static readonly string[] _g2TownArmors =
            { "ARMOR_BAU_L.MDM", "ARMOR_BAU_M.MDM", "ARMOR_LEATHER_L.MDM", "ARMOR_NOV_L.MDM", "ARMOR_BARKEEPER.MDM", "HUM_BODY_NAKED0.MDM" };
        private static readonly string[] _idleClips = { "S_RUN", "S_LGUARD", "S_HGUARD", "S_RUN", "S_SIT" };

        private class Npc
        {
            public Node3D Root;
            public Skeleton3D Skeleton;
            public ModelFactory.Clip Clip;
            public float Phase;
        }

        private readonly List<Npc> _npcs = new();
        private readonly Stopwatch _watch = new();
        private double _time;
        private double _logTime;
        private double _boneMs;
        private int _frames;
        private int _boneWrites;
        private bool _isSnapped;
        private string _boneMode;

        public int Count => _npcs.Count;

        public Transform3D? GetNpcTransform(string waypoint)
        {
            var npc = _npcs.FirstOrDefault(n => n.Root.Name.ToString().Equals(waypoint, StringComparison.OrdinalIgnoreCase));
            return npc?.Root.GlobalTransform;
        }

        public void Spawn(ModelFactory factory, WorldData world, int count, GroundLightSampler light, string boneMode,
            bool isGothic2 = false)
        {
            var guardArmors = isGothic2 ? _g2GuardArmors : _guardArmors;
            var campArmors = isGothic2 ? _g2TownArmors : _campArmors;
            _boneMode = boneMode;
            var random = new Random(7);
            var points = world.Waypoints
                .Where(p => isGothic2
                    ? p.Name.StartsWith("NW_CITY_")
                    : p.Name.StartsWith("OCR_") || p.Name.StartsWith("OCC_") || p.Name.StartsWith("OC_"))
                .OrderBy(_ => random.Next())
                .Take(count)
                .ToList();

            foreach (var point in points)
            {
                var isGuard = point.Name.StartsWith("OCC_") || random.Next(4) == 0;
                var armor = isGuard ? guardArmors[random.Next(guardArmors.Length)] : campArmors[random.Next(campArmors.Length)];
                var body = factory.GetSkinnedMesh(armor) ?? factory.GetSkinnedMesh("HUM_BODY_NAKED0.MDM");
                var clip = factory.GetClip(_idleClips[random.Next(_idleClips.Length)]) ?? factory.GetClip("S_RUN");
                if (body == null || clip == null)
                    continue;

                var root = new Node3D { Name = point.Name };
                AddChild(root);
                var direction = point.Direction * 100f;
                root.GlobalPosition = point.Position;
                if (new Vector2(direction.X, direction.Z).Length() > 0.01f)
                    root.Rotation = new Vector3(0, Mathf.Atan2(-direction.X, -direction.Z), 0);

                var skeleton = factory.CreateSkeleton();
                root.AddChild(skeleton);
                var tint = light.Sample(root.GlobalPosition, new Color(0.6f, 0.6f, 0.6f));

                var bodyInstance = new MeshInstance3D { Mesh = body, Skin = factory.Skin };
                skeleton.AddChild(bodyInstance);
                bodyInstance.Skeleton = bodyInstance.GetPathTo(skeleton);
                bodyInstance.SetInstanceShaderParameter("tint", tint);

                var head = factory.GetHeadMesh(_heads[random.Next(_heads.Length)]);
                if (head != null)
                {
                    var attachment = new BoneAttachment3D { BoneName = "BIP01 HEAD" };
                    skeleton.AddChild(attachment);
                    var headInstance = new MeshInstance3D { Mesh = head };
                    attachment.AddChild(headInstance);
                    headInstance.SetInstanceShaderParameter("tint", tint);
                }

                _npcs.Add(new Npc { Root = root, Skeleton = skeleton, Clip = clip, Phase = (float)random.NextDouble() * 10f });
                if (_npcs.Count <= 12)
                    GD.Print($"[Npc] {point.Name} at {root.GlobalPosition}: {armor}, {clip.Name}");
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_isSnapped || _npcs.Count == 0)
                return;
            _isSnapped = true;

            // Waypoints sit roughly on the ground; put the feet exactly on it.
            var space = GetWorld3D().DirectSpaceState;
            foreach (var npc in _npcs)
            {
                var from = npc.Root.GlobalPosition + Vector3.Up * 1.0f;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 4f));
                if (hit.Count > 0)
                    npc.Root.GlobalPosition = (Vector3)hit["position"];
            }
        }

        public override void _Process(double delta)
        {
            _time += delta;
            _watch.Restart();

            foreach (var npc in _npcs)
            {
                var clip = npc.Clip;
                var t = (_time + npc.Phase) * clip.Fps;
                var frame0 = (int)(t % clip.FrameCount);
                var frame1 = (frame0 + 1) % clip.FrameCount;
                var weight = (float)(t - Math.Floor(t));
                var channels = clip.Nodes.Length;

                for (var channel = 0; channel < channels; channel++)
                {
                    var a = frame0 * channels + channel;
                    var b = frame1 * channels + channel;
                    var position = clip.Positions[a].Lerp(clip.Positions[b], weight);
                    var rotation = clip.Rotations[a].Slerp(clip.Rotations[b], weight);
                    var bone = clip.Nodes[channel];

                    if (_boneMode == "pose")
                    {
                        npc.Skeleton.SetBonePose(bone, new Transform3D(new Basis(rotation), position));
                        _boneWrites++;
                    }
                    else
                    {
                        npc.Skeleton.SetBonePosePosition(bone, position);
                        npc.Skeleton.SetBonePoseRotation(bone, rotation);
                        _boneWrites += 2;
                    }
                }
            }

            _boneMs += _watch.Elapsed.TotalMilliseconds;
            _frames++;
            _logTime += delta;
            if (_logTime < 1.0)
                return;

            GD.Print($"[Npc] {_npcs.Count} humans, mode {_boneMode}: {_boneMs / _frames:F3} ms per frame for " +
                     $"{_boneWrites / _frames} bone calls ({_boneMs * 1000.0 / Math.Max(1, _boneWrites):F3} µs per call)");
            _logTime = 0;
            _boneMs = 0;
            _frames = 0;
            _boneWrites = 0;
        }
    }
}
