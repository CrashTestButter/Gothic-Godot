using System;
using Godot;
using GothicGodot.Spike;

namespace GothicGodot.Player
{
    /// <summary>
    /// One of the hero's hands from his own body mesh (skin, armour gloves): a skeleton of its own where only the hand
    /// and its finger bone (Gothic's hands are mittens) are posed; every other bone is shrunk to the wrist, so the
    /// arm's vertices vanish there. The hand lies in the controller's grip pose (fingers along -Z, palm towards the
    /// other hand) and the finger bone curls with the grip.
    /// </summary>
    public partial class GothicHand : Node3D
    {
        private const float _maxCurlDegrees = 75f;
        private const float _palmFromWrist = 0.07f;

        private Skeleton3D _skeleton;
        private int _hand;
        private int _finger;
        private Transform3D _fingerRest;
        private Vector3 _curlAxis;
        private float _curl = -1f;

        /// <summary>0 open .. 1 closed, from the grip.</summary>
        public Func<float> Grip;

        public void Setup(ModelFactory factory, ArrayMesh body, bool isLeft, Color tint)
        {
            var side = isLeft ? "L" : "R";
            _hand = Array.IndexOf(factory.BoneNames, $"BIP01 {side} HAND");
            _finger = Array.IndexOf(factory.BoneNames, $"BIP01 {side} FINGER0");
            var forearm = Array.IndexOf(factory.BoneNames, $"BIP01 {side} FOREARM");
            if (_hand < 0 || _finger < 0 || forearm < 0)
                return;

            var globals = new Transform3D[factory.Rests.Length];
            for (var i = 0; i < globals.Length; i++)
                globals[i] = factory.Parents[i] < 0 ? factory.Rests[i] : globals[factory.Parents[i]] * factory.Rests[i];
            var handRest = globals[_hand];

            // Rest pose: arms out, palms down. Turn the hand so the fingers point along -Z and the palm faces the
            // other hand (grip pose: right palm -X, left palm +X), the palm's middle at the origin.
            var fingers = (handRest.Origin - globals[forearm].Origin).Normalized();
            var palm = Vector3.Down;
            var turn = AlignBasis(fingers, palm, Vector3.Forward, isLeft ? Vector3.Right : Vector3.Left);
            var palmCentre = handRest.Origin + fingers * _palmFromWrist;
            var handGlobal = new Transform3D(turn * handRest.Basis, -(turn * palmCentre) + turn * handRest.Origin);

            _skeleton = new Skeleton3D { Name = "Skeleton" };
            for (var i = 0; i < factory.BoneNames.Length; i++)
            {
                _skeleton.AddBone(factory.BoneNames[i]);
                _skeleton.SetBoneRest(i, factory.Rests[i]);
            }

            // Only the finger keeps its parent; everything else is a root bone posed directly in hand space.
            _skeleton.SetBoneParent(_finger, _hand);
            var wrist = new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.0001f), handGlobal.Origin);
            for (var i = 0; i < factory.BoneNames.Length; i++)
            {
                if (i == _hand || i == _finger)
                    continue;
                _skeleton.SetBonePosePosition(i, wrist.Origin);
                _skeleton.SetBonePoseRotation(i, Quaternion.Identity);
                _skeleton.SetBonePoseScale(i, Vector3.One * 0.0001f);
            }

            _skeleton.SetBonePosePosition(_hand, handGlobal.Origin);
            _skeleton.SetBonePoseRotation(_hand, handGlobal.Basis.GetRotationQuaternion());
            _fingerRest = factory.Rests[_finger];
            _skeleton.SetBonePosePosition(_finger, _fingerRest.Origin);
            _skeleton.SetBonePoseRotation(_finger, _fingerRest.Basis.GetRotationQuaternion());

            // Curling bends the finger towards the palm: around the axis across the hand, in the finger's own space.
            var across = fingers.Cross(palm).Normalized();
            _curlAxis = (globals[_finger].Basis.Inverse() * across).Normalized();

            // The finger tip has to move towards the palm when the hand closes; else the axis is the other way round.
            var fingerGlobal = handGlobal * _fingerRest;
            var tipLocal = globals[_finger].AffineInverse() * (globals[_finger].Origin + fingers * 0.05f);
            var open = fingerGlobal * tipLocal;
            var closed = handGlobal * new Transform3D(new Basis(_fingerRest.Basis.GetRotationQuaternion() *
                new Quaternion(_curlAxis, Mathf.DegToRad(_maxCurlDegrees))), _fingerRest.Origin) * tipLocal;
            var palmSide = isLeft ? Vector3.Right : Vector3.Left;
            if ((closed - open).Dot(palmSide) < 0)
                _curlAxis = -_curlAxis;
            GD.Print($"[Hand] {side}: tip moves {(closed - open).Dot(palmSide):F3} m towards the palm when closing" +
                     $"{((closed - open).Dot(palmSide) < 0 ? " (axis flipped)" : "")}");

            AddChild(_skeleton);
            var mesh = new MeshInstance3D
            {
                Mesh = body, Skin = factory.Skin, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _skeleton.AddChild(mesh);
            mesh.Skeleton = mesh.GetPathTo(_skeleton);
            mesh.SetInstanceShaderParameter("tint", tint);
        }

        /// <summary>The rotation that turns the rest directions onto the wanted ones (both pairs orthogonalised).</summary>
        private static Basis AlignBasis(Vector3 fromForward, Vector3 fromSide, Vector3 toForward, Vector3 toSide)
        {
            Basis Frame(Vector3 forward, Vector3 side)
            {
                var f = forward.Normalized();
                var s = (side - f * side.Dot(f)).Normalized();
                return new Basis(f, s, f.Cross(s));
            }

            return Frame(toForward, toSide) * Frame(fromForward, fromSide).Inverse();
        }

        public override void _Process(double delta)
        {
            if (_skeleton == null)
                return;
            var curl = Mathf.Clamp(Grip?.Invoke() ?? 0f, 0f, 1f);
            if (Mathf.Abs(curl - _curl) < 0.01f)
                return;
            _curl = curl;
            var bend = new Quaternion(_curlAxis, Mathf.DegToRad(_maxCurlDegrees) * curl);
            _skeleton.SetBonePoseRotation(_finger, _fingerRest.Basis.GetRotationQuaternion() * bend);
        }
    }
}
