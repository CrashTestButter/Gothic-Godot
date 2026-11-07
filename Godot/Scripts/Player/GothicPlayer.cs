using System.Linq;
using Gothic.Core.Logging;
using Godot;
using Logger = Gothic.Core.Logging.Logger;

namespace GothicGodot.Player
{
    /// <summary>
    /// The player: a capsule that walks on Gothic's world with Gothic-Unity's VR movement values (walk 1.5 m/s,
    /// 45° snap turn, 0.5 m steps, slope limit = the hero's SLIDE_ANGLE: G1 45°, G2 50°).
    /// - XR: XROrigin3D with head and both hands; the body follows the head on the floor (room scale), the left stick
    ///   moves (pressed: run), the right stick snap-turns around the head.
    /// - Desktop: camera at eye height, WASD to move, mouse to look, Shift to run, Q/E to snap-turn.
    /// - Walk test (--walk-test=seconds): walks straight ahead without input and logs where it got.
    /// </summary>
    public partial class GothicPlayer : CharacterBody3D
    {
        private const float _walkSpeed = 1.5f;
        private const float _runSpeed = 3.5f;
        private const float _snapTurnDegrees = 45f;
        private const float _stepHeight = 0.5f;
        private const float _radius = 0.2f;
        private const float _desktopEyeHeight = 1.7f;
        private const float _gravity = 9.81f;

        private XROrigin3D _origin;
        private XRCamera3D _xrCamera;
        private XRController3D _left;
        private XRController3D _right;
        private Camera3D _desktopCamera;
        private CollisionShape3D _shape;
        private bool _isXr;
        private bool _isSnapTurnReady = true;
        private float _pitch;
        private float _walkTestSeconds;
        private double _walkTestTime;
        private double _walkLogTime;
        private Vector3 _walkStart;
        private bool _isPlaced;
        private TeleportAimer _teleport;
        private bool _isAiming;
        private float _teleportTestDelay;
        private bool _isTeleportTestDone;

        public Camera3D Camera => _isXr ? _xrCamera : _desktopCamera;
        public Interaction.GrabHand LeftGrab { get; private set; }
        public Interaction.GrabHand RightGrab { get; private set; }
        public bool IsXr => _isXr;
        public XRController3D RightHand => _right;
        public XRController3D LeftHand => _left;
        public bool IsPlaced => _isPlaced;

        private readonly System.Collections.Generic.HashSet<string> _inputLocks = new();

        /// <summary>
        /// No walking, turning or teleporting (the head still looks) while anything holds a lock: a dialog (Gothic pins
        /// the hero), lock picking, being knocked out.
        /// </summary>
        public bool IsInputLocked => _inputLocks.Count > 0;

        public void SetInputLock(string reason, bool isLocked)
        {
            if (isLocked)
                _inputLocks.Add(reason);
            else
                _inputLocks.Remove(reason);
        }

        /// <summary>Desktop: turns the body and tilts the eyes so the camera looks at the point (tests).</summary>
        public void LookAtPoint(Vector3 target)
        {
            var direction = (target - Camera.GlobalPosition).Normalized();
            Rotation = new Vector3(0, Mathf.Atan2(-direction.X, -direction.Z), 0);
            _pitch = Mathf.Asin(Mathf.Clamp(direction.Y, -1f, 1f));
            if (_desktopCamera != null)
                _desktopCamera.Rotation = new Vector3(_pitch, 0, 0);
        }

        /// <summary>Puts the player's feet at the position, facing along the yaw (radians).</summary>
        public void PlaceAt(Vector3 position, float yaw)
        {
            GothicGodot.Spike.CollisionStreamer.LoadNear(position);
            GlobalPosition = position;
            Rotation = new Vector3(0, yaw, 0);
            Velocity = Vector3.Zero;
        }

        public void Setup(bool isXr, float slopeDegrees, float far, float walkTestSeconds, float teleportTestDelay = 0f)
        {
            _teleportTestDelay = teleportTestDelay;
            _isXr = isXr;
            _walkTestSeconds = walkTestSeconds;
            FloorMaxAngle = Mathf.DegToRad(slopeDegrees);
            FloorSnapLength = _stepHeight;
            FloorStopOnSlope = true;
            SafeMargin = 0.02f;

            CollisionLayer = Interaction.PhysicsLayers.Player;
            CollisionMask = Interaction.PhysicsLayers.World | Interaction.PhysicsLayers.Mobs;
            _shape = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = _radius, Height = _desktopEyeHeight } };
            _shape.Position = new Vector3(0, _desktopEyeHeight * 0.5f, 0);
            AddChild(_shape);

            if (isXr)
            {
                _origin = new XROrigin3D { Name = "XROrigin" };
                AddChild(_origin);
                _xrCamera = new XRCamera3D { Name = "Head", Far = far };
                _origin.AddChild(_xrCamera);
                _left = CreateHand("LeftHand", "left_hand");
                _right = CreateHand("RightHand", "right_hand");
                LeftGrab = CreateGrab(_left, "LeftGrab");
                RightGrab = CreateGrab(_right, "RightGrab");
            }
            else
            {
                _desktopCamera = new Camera3D
                {
                    Name = "Eyes", Position = new Vector3(0, _desktopEyeHeight, 0), Far = far, Fov = 75f, Current = true
                };
                AddChild(_desktopCamera);
                // The desktop hand: low right in front of the eyes; the right mouse button holds an item there.
                RightGrab = new Interaction.GrabHand
                {
                    Name = "RightGrab", Position = new Vector3(0.22f, -0.25f, -0.55f), AimCamera = _desktopCamera,
                    IsGripDown = () => Input.IsMouseButtonPressed(MouseButton.Right)
                };
                _desktopCamera.AddChild(RightGrab);
                if (walkTestSeconds <= 0 && DisplayServer.GetName() != "headless")
                    Input.MouseMode = Input.MouseModeEnum.Captured;
            }

            _teleport = new TeleportAimer { Name = "Teleport" };
            AddChild(_teleport);

            Logger.Log($"[Player] {(isXr ? "XR" : "desktop")} player, slope limit {slopeDegrees}°, step {_stepHeight} m", LogCat.VR);
        }

        /// <summary>The hero's own hands instead of the boxes (XR), or his right hand in front of the camera (desktop).</summary>
        public void SetHands(Spike.ModelFactory factory, ArrayMesh body, Color tint)
        {
            if (_isXr)
            {
                foreach (var (controller, isLeft) in new[] { (_left, true), (_right, false) })
                {
                    controller.GetNodeOrNull("Box")?.QueueFree();
                    var hand = new GothicHand { Name = isLeft ? "GothicLeftHand" : "GothicRightHand" };
                    hand.Grip = () => controller.GetFloat("grip");
                    controller.AddChild(hand);
                    hand.Setup(factory, body, isLeft, tint);
                }

                return;
            }

            var desktopHand = new GothicHand { Name = "GothicRightHand", Position = new Vector3(0.02f, -0.02f, 0.04f) };
            desktopHand.Grip = () => RightGrab.Held != null || RightGrab.ForcedGrip == true ||
                                     Input.IsMouseButtonPressed(MouseButton.Right) ? 1f : 0f;
            RightGrab.AddChild(desktopHand);
            desktopHand.Setup(factory, body, false, tint);
        }

        private static Interaction.GrabHand CreateGrab(XRController3D hand, string name)
        {
            var grab = new Interaction.GrabHand
            {
                Name = name, IsGripDown = () => hand.GetFloat("grip") > 0.6f || hand.IsButtonPressed("grip_click")
            };
            hand.AddChild(grab);
            return grab;
        }

        private XRController3D CreateHand(string name, string tracker)
        {
            var hand = new XRController3D { Name = name, Tracker = tracker };
            _origin.AddChild(hand);
            hand.AddChild(new MeshInstance3D
            {
                Name = "Box",
                Mesh = new BoxMesh { Size = new Vector3(0.08f, 0.04f, 0.16f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.58f, 0.48f) }
            });
            return hand;
        }

        public override void _UnhandledInput(InputEvent inputEvent)
        {
            if (_isXr || inputEvent is not InputEventMouseMotion motion || Input.MouseMode != Input.MouseModeEnum.Captured)
                return;

            RotateY(-motion.Relative.X * 0.003f);
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.003f, -1.45f, 1.45f);
            _desktopCamera.Rotation = new Vector3(_pitch, 0, 0);
        }

        public override void _PhysicsProcess(double delta)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            PhysicsStep(delta);
            GothicGodot.Spike.FrameEvents.PlayerPhysicsMs +=
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        private void PhysicsStep(double delta)
        {
            if (!_isPlaced)
            {
                PlaceOnGround();
                return;
            }

            var dt = (float)delta;
            if (_isXr)
                FollowHead();

            var isRunning = false;
            var input = IsInputLocked ? Vector2.Zero : GetMoveInput(dt, out isRunning);
            var forward = GetViewYaw();
            var direction = (forward * -input.Y + forward.Cross(Vector3.Up) * input.X).Normalized() *
                            Mathf.Min(1f, input.Length());
            var speed = isRunning ? _runSpeed : _walkSpeed;
            var velocity = Velocity;
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
            velocity.Y = IsOnFloor() ? Mathf.Min(velocity.Y, 0f) : velocity.Y - _gravity * dt;
            Velocity = velocity;

            var before = GlobalPosition;
            MoveAndSlide();
            if (IsOnWall() && direction.LengthSquared() > 0.01f)
                TryStepUp(before, direction * speed * dt);

            if (!IsInputLocked)
            {
                HandleSnapTurn();
                HandleTeleport(delta);
            }

            LogWalkTest(delta);
        }

        private int _placeAttempts;

        /// <summary>Waits until the world's collision is there (big trimeshes appear a few physics frames late).</summary>
        private void PlaceOnGround()
        {
            var space = GetWorld3D().DirectSpaceState;
            // From high up: a spawn point can lie inside a hill (the Old Camp's ring centre is below the castle yard).
            var from = GlobalPosition + Vector3.Up * 100f;
            var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 200f, Interaction.PhysicsLayers.World);
            query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = space.IntersectRay(query);
            if (hit.Count == 0 && ++_placeAttempts < 120)
                return;

            if (hit.Count == 0)
                DebugPhysics(space);

            if (hit.Count > 0)
                GlobalPosition = (Vector3)hit["position"] + Vector3.Up * 0.05f;
            _walkStart = GlobalPosition;
            _isPlaced = true;
            Logger.Log($"[Player] placed at {GlobalPosition} after {_placeAttempts} physics frames, ground " +
                       $"{(hit.Count > 0 ? hit["collider"].As<Node>()?.Name : "not found")}", LogCat.VR);
        }

        private void DebugPhysics(PhysicsDirectSpaceState3D space)
        {
            var body = GetTree().Root.FindChild("WorldCollision", true, false) as StaticBody3D;
            var shapes = body?.GetChildren().OfType<CollisionShape3D>().ToList();
            Logger.Log($"[Player] debug: collision body {(body != null)}, shapes {shapes?.Count}, body layer {body?.CollisionLayer}, " +
                       $"player mask {CollisionMask}, in tree {body?.IsInsideTree()}", LogCat.VR);
            if (shapes is { Count: > 0 } && shapes[0].Shape is ConcavePolygonShape3D concave)
            {
                var faces = concave.GetFaces();
                var aabb = new Aabb(faces[0], Vector3.Zero);
                foreach (var f in faces)
                    aabb = aabb.Expand(f);
                Logger.Log($"[Player] debug: first shape {faces.Length / 3} triangles, bounds {aabb}", LogCat.VR);
                var probe = aabb.GetCenter();
                var probeHit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(probe + Vector3.Up * 500, probe + Vector3.Down * 500));
                Logger.Log($"[Player] debug: ray through that shape's centre hits {probeHit.Count > 0}", LogCat.VR);
            }

            var high = space.IntersectRay(PhysicsRayQueryParameters3D.Create(GlobalPosition + Vector3.Up * 500, GlobalPosition + Vector3.Down * 500));
            Logger.Log($"[Player] debug: ray from 500 m above the player hits {high.Count > 0}", LogCat.VR);
        }

        /// <summary>
        /// Room scale: the head walks, the capsule follows it on the floor (with collision), the origin moves back so
        /// the head stays where it is in the world.
        /// </summary>
        private void FollowHead()
        {
            var head = _xrCamera.GlobalPosition;
            var offset = new Vector3(head.X - GlobalPosition.X, 0, head.Z - GlobalPosition.Z);
            if (offset.LengthSquared() < 0.0001f)
                return;

            var collision = MoveAndCollide(offset, testOnly: false);
            var moved = collision == null ? offset : offset - collision.GetRemainder();
            _origin.GlobalPosition -= moved;

            var eyeHeight = Mathf.Clamp(_xrCamera.Position.Y, 0.5f, 2.2f);
            var capsule = (CapsuleShape3D)_shape.Shape;
            capsule.Height = Mathf.Max(eyeHeight, _radius * 2.5f);
            _shape.Position = new Vector3(0, capsule.Height * 0.5f, 0);
        }

        /// <summary>Tests: move input instead of the stick / keys (x right, y back like the stick).</summary>
        public Vector2? ForcedMove { get; set; }

        private Vector2 GetMoveInput(float dt, out bool isRunning)
        {
            if (ForcedMove is { } forced)
            {
                isRunning = false;
                return forced;
            }

            isRunning = false;
            if (_walkTestSeconds > 0)
                return _walkTestTime < _walkTestSeconds ? new Vector2(0, -1) : Vector2.Zero;

            if (_isXr)
            {
                var stick = _left.GetVector2("primary");
                isRunning = _left.IsButtonPressed("primary_click");
                return stick.Length() < 0.15f ? Vector2.Zero : new Vector2(stick.X, -stick.Y);
            }

            isRunning = Input.IsKeyPressed(Key.Shift);
            var x = (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0);
            var y = (Input.IsKeyPressed(Key.S) ? 1 : 0) - (Input.IsKeyPressed(Key.W) ? 1 : 0);
            return new Vector2(x, y);
        }

        /// <summary>Horizontal view direction (head in XR, body on the desktop).</summary>
        private Vector3 GetViewYaw()
        {
            var forward = _isXr ? -_xrCamera.GlobalTransform.Basis.Z : -GlobalTransform.Basis.Z;
            forward.Y = 0;
            return forward.LengthSquared() > 0.0001f ? forward.Normalized() : Vector3.Forward;
        }

        /// <summary>CharacterBody3D has no step offset: blocked on a wall, try up by the step height, forward, down.</summary>
        private void TryStepUp(Vector3 before, Vector3 step)
        {
            var transform = GlobalTransform;
            transform.Origin = before;
            if (TestMove(transform, Vector3.Up * _stepHeight))
                return;
            transform.Origin += Vector3.Up * _stepHeight;
            if (TestMove(transform, step))
                return;
            transform.Origin += step;
            var down = new KinematicCollision3D();
            if (!TestMove(transform, Vector3.Down * _stepHeight * 1.5f, down))
                return;
            if (down.GetNormal().AngleTo(Vector3.Up) > FloorMaxAngle)
                return;
            GlobalPosition = transform.Origin + down.GetTravel();
        }

        /// <summary>
        /// XR: right stick forward aims, letting go teleports. Desktop: hold T to aim from the eyes. Test: one aimed
        /// teleport ahead after --teleport-test seconds.
        /// </summary>
        private void HandleTeleport(double delta)
        {
            bool isAimingNow;
            Vector3 origin;
            Vector3 direction;
            if (_isXr)
            {
                isAimingNow = _right.GetVector2("primary").Y > 0.7f;
                origin = _right.GlobalPosition;
                direction = -_right.GlobalTransform.Basis.Z;
            }
            else
            {
                isAimingNow = Input.IsKeyPressed(Key.T);
                origin = _desktopCamera.GlobalPosition;
                direction = -_desktopCamera.GlobalTransform.Basis.Z;
            }

            if (_teleportTestDelay > 0 && !_isTeleportTestDone)
            {
                _teleportTestDelay -= (float)delta;
                isAimingNow = _teleportTestDelay > 0 || !_isAiming;
                // Aim ahead and a little up, like a throw.
                direction = (GetViewYaw() + Vector3.Up * 0.35f).Normalized();
                origin = GlobalPosition + Vector3.Up * 1.4f;
                if (_teleportTestDelay <= 0 && _isAiming)
                    _isTeleportTestDone = true;
            }

            if (isAimingNow)
            {
                _isAiming = true;
                _teleport.Aim(GetWorld3D().DirectSpaceState, origin, direction, GlobalPosition, FloorMaxAngle, this);
                return;
            }

            if (!_isAiming)
                return;
            _isAiming = false;
            var target = _teleport.Target;
            var isValid = _teleport.IsTargetValid;
            _teleport.Stop();
            if (!isValid)
            {
                Logger.Log($"[Player] teleport refused at {target}", LogCat.VR);
                return;
            }

            var from = GlobalPosition;
            GlobalPosition = target;
            Velocity = Vector3.Zero;
            Logger.Log($"[Player] teleported {from.DistanceTo(target):F1} m to {target}", LogCat.VR);
        }

        private void HandleSnapTurn()
        {
            float turn;
            if (_isXr)
                turn = _right.GetVector2("primary").X;
            else
                turn = (Input.IsKeyPressed(Key.E) ? 1 : 0) - (Input.IsKeyPressed(Key.Q) ? 1 : 0);

            if (Mathf.Abs(turn) < 0.3f)
            {
                _isSnapTurnReady = true;
                return;
            }

            if (!_isSnapTurnReady || Mathf.Abs(turn) < 0.7f)
                return;
            _isSnapTurnReady = false;

            var angle = Mathf.DegToRad(-Mathf.Sign(turn) * _snapTurnDegrees);
            if (_isXr)
            {
                // Around the head, so the view turns in place.
                var pivot = _xrCamera.GlobalPosition;
                var originTransform = _origin.GlobalTransform;
                originTransform.Origin -= pivot;
                originTransform = new Transform3D(new Basis(Vector3.Up, angle), Vector3.Zero) * originTransform;
                originTransform.Origin += pivot;
                _origin.GlobalTransform = originTransform;
            }
            else
            {
                RotateY(angle);
            }
        }

        private void LogWalkTest(double delta)
        {
            if (_walkTestSeconds <= 0)
                return;

            _walkTestTime += delta;
            _walkLogTime += delta;
            if (_walkLogTime < 1.0)
                return;
            _walkLogTime = 0;
            Logger.Log($"[Player] walk test {_walkTestTime:F0} s: at {GlobalPosition}, {(_walkStart - GlobalPosition).Length():F1} m " +
                       $"from the start, on floor {IsOnFloor()}, on wall {IsOnWall()}", LogCat.VR);
        }
    }
}
