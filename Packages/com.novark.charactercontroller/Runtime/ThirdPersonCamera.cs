using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Orbit / follow camera driven by the same look input as the player (no Cinemachine).
    ///
    /// It keeps the whole player in view: every frame it tests lines of sight from the camera to the head,
    /// chest, hips and both feet. When something hides part of the body it searches nearby orbit angles
    /// (sideways and higher) for the closest view that sees all of it, and eases there; if no angle works it
    /// pulls in until the body is visible. Looking around yourself takes over from the automatic offset. The
    /// camera never passes through geometry (sphere-cast from the pivot).
    /// </summary>
    [DefaultExecutionOrder(50)] // after the player has moved
    public class ThirdPersonCamera : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The point the camera looks at and orbits (usually a head-height socket on the player)")]
        public Transform Target;
        public PlayerInputs Input;

        [Header("Orbit")]
        public float Distance = 4.0f;
        public float MouseSensitivity = 1.0f;
        [Tooltip("Gamepad stick look multiplier, applied per second (stick input is a rate, not a delta)")]
        public float GamepadLookRate = 60.0f;
        [Tooltip("Extra look height above the target pivot")]
        public float HeightOffset = 0f;
        public float TopClamp = 70.0f;
        public float BottomClamp = -30.0f;

        [Header("Collision")]
        public LayerMask CollisionLayers = ~0;
        public float CollisionRadius = 0.2f;
        [Tooltip("Closest the camera may get to the pivot when pulled in by collision")]
        public float MinDistance = 0.5f;
        [Tooltip("How fast the camera eases back out after an obstruction clears")]
        public float RecoverSpeed = 6.0f;

        [Header("Keep The Player Visible")]
        public bool KeepPlayerVisible = true;
        [Tooltip("Largest automatic sideways swing to find a clear view (degrees)")]
        public float MaxAutoYaw = 80f;
        [Tooltip("Largest automatic rise to look over something (degrees)")]
        public float MaxAutoPitch = 35f;
        [Tooltip("Seconds to ease into a new view")]
        public float AutoSmoothTime = 0.25f;
        [Tooltip("Seconds a clear view must last before easing back to where the player aimed")]
        public float ReturnDelay = 0.6f;
        [Tooltip("Thickness of the sight lines (thin poles don't count as hiding the body)")]
        public float SightRadius = 0.06f;
        [Tooltip("Seconds the camera's height lags the player during jumps, drops and climbs (keeps the view over edges)")]
        public float VerticalLag = 0.3f;
        [Tooltip("How fast the camera closes in when that is the only way to see the player (geometry still snaps)")]
        public float VisibilityPullSpeed = 2.5f;

        public bool PlayerFullyVisible { get; private set; } = true;
        /// <summary>Vertical shift of the orbit point set by gameplay (e.g. following a diver's head down).</summary>
        [System.NonSerialized] public float PivotOffset;

        private static readonly HumanBodyBones[] SightBones =
            { HumanBodyBones.Head, HumanBodyBones.Chest, HumanBodyBones.Hips, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
        private static readonly float[] YawSteps = { 0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 80f, -80f };
        private static readonly float[] PitchSteps = { 0f, 12f, 24f, 35f, -10f };

        private float _yaw;
        private float _pitch = 15f;
        private float _currentDistance;
        private float _autoYaw, _autoPitch, _autoYawVel, _autoPitchVel;
        private float _targetAutoYaw, _targetAutoPitch;
        private float _clearTime;
        private float _lastSearch = -1f;
        private Transform _player;
        private Animator _playerAnimator;
        private ThirdPersonController _controller;
        private float _pivotY, _pivotYVelocity;
        private bool _pivotYReady;
        private float _relaxedHold;
        private readonly Vector3[] _points = new Vector3[5];
        private readonly RaycastHit[] _hits = new RaycastHit[12];

        private void Start()
        {
            Vector3 angles = transform.eulerAngles;
            _yaw = angles.y;
            // eulerAngles.x is 0..360, convert to -180..180 so the clamp works for upward pitch.
            _pitch = Mathf.DeltaAngle(0f, angles.x);
            _currentDistance = Distance;
        }

        /// <summary>Points the camera (e.g. after a teleport), clearing any automatic offset.</summary>
        public void SetOrientation(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = pitch;
            _autoYaw = _autoPitch = _targetAutoYaw = _targetAutoPitch = _autoYawVel = _autoPitchVel = _groundPitch = _groundPitchVel = 0f;
            _currentDistance = Distance;
            _pivotYReady = false;
        }

        private void LateUpdate()
        {
            if (Target == null) return;
            ResolvePlayer();

            bool looking = Input != null && Input.look.sqrMagnitude > 0.0001f;
            if (looking)
            {
                // The player takes over: keep the current view as the new aim, and stop correcting it.
                _yaw += _autoYaw;
                _pitch += _autoPitch;
                _autoYaw = _autoPitch = _targetAutoYaw = _targetAutoPitch = 0f;
                _autoYawVel = _autoPitchVel = 0f;

                // Mouse delta is already per-frame; stick input is a rate and must scale with time.
                float scale = Input.LookIsRate ? GamepadLookRate * Time.deltaTime : MouseSensitivity;
                _yaw += Input.look.x * scale;
                _pitch -= Input.look.y * scale;
            }
            _pitch = Mathf.Clamp(_pitch, BottomClamp, TopClamp);

            // Climbing, vaulting, dropping, falling: the ledge hides the feet on purpose. Only ask to see the head
            // and chest, and never pull in just for visibility, so the view stays steady through the move.
            bool relaxed = _controller != null && (!_controller.Grounded || _controller.ExternallyDriven);
            // The orbit point's height trails fast vertical moves (a drop off a ledge, a climb), so the camera
            // stays over the edge instead of being dragged into it.
            if (relaxed) _relaxedHold = 0.35f;
            else if (_relaxedHold > 0f) _relaxedHold -= Time.deltaTime;
            float targetY = Target.position.y + HeightOffset + PivotOffset;
            if (!_pivotYReady) { _pivotY = targetY; _pivotYReady = true; }
            _pivotY = Mathf.SmoothDamp(_pivotY, targetY, ref _pivotYVelocity, relaxed || _relaxedHold > 0f ? VerticalLag : 0.05f);
            Vector3 pivot = new Vector3(Target.position.x, _pivotY, Target.position.z);
            // Under a low roof (a tunnel, a crawlspace) the orbit point stays below it, or every cast starts
            // inside the ceiling and the camera ends up in the player's head.
            Vector3 low = Target.position + Vector3.up * 0.3f;
            float above = pivot.y - low.y + CollisionRadius;
            if (above > 0f && Physics.SphereCast(low, CollisionRadius * 0.5f, Vector3.up, out RaycastHit roof, above,
                    CollisionLayers, QueryTriggerInteraction.Ignore) && !IsPlayer(roof.transform))
                pivot.y = Mathf.Max(low.y, roof.point.y - CollisionRadius - 0.05f);
            int pointCount = GatherSightPoints();
            if (relaxed) pointCount = Mathf.Min(pointCount, 2);

            if (KeepPlayerVisible && !looking && pointCount > 0) ChooseView(pivot, pointCount);

            _autoYaw = Mathf.SmoothDampAngle(_autoYaw, _targetAutoYaw, ref _autoYawVel, AutoSmoothTime);
            _autoPitch = Mathf.SmoothDamp(_autoPitch, _targetAutoPitch, ref _autoPitchVel, AutoSmoothTime);

            float pitch = Mathf.Clamp(_pitch + _autoPitch, BottomClamp, TopClamp);
            // Low ground behind the player (steps, a curb, the deck above a swimmer): rise over it instead of
            // being pushed in by it.
            float liftTarget = GroundLift(pivot, pitch, _yaw + _autoYaw);
            _groundPitch = Mathf.SmoothDamp(_groundPitch, liftTarget, ref _groundPitchVel, liftTarget > _groundPitch ? 0.12f : 0.5f);
            pitch = Mathf.Clamp(pitch + _groundPitch, BottomClamp, TopClamp);
            Quaternion rotation = Quaternion.Euler(pitch, _yaw + _autoYaw, 0f);
            Vector3 back = -(rotation * Vector3.forward);

            float geometry = CollisionDistance(pivot, back, Distance);
            float targetDistance = geometry;
            // Still hidden at this angle (nothing better found): come closer until the body shows.
            if (KeepPlayerVisible && !relaxed && pointCount > 0 && Visible(pivot + back * geometry, pointCount) < pointCount)
                targetDistance = ClosestVisibleDistance(pivot, back, geometry, pointCount);

            if (geometry < _currentDistance && CameraInside(pivot + back * _currentDistance))
            {
                _currentDistance = geometry; // the camera would be inside a wall: snap in, never clip through
                DistanceMode = "inside geometry";
            }
            else if (geometry < _currentDistance)
            {
                // Only something in between (a post, a step, a ledge): close in quickly but smoothly.
                _currentDistance = Mathf.MoveTowards(_currentDistance, geometry,
                    Mathf.Clamp((_currentDistance - geometry) * 5f, 1.5f, OcclusionPullSpeed) * Time.deltaTime);
                DistanceMode = "occluded";
            }
            else if (targetDistance < _currentDistance)
            {
                _currentDistance = Mathf.Lerp(_currentDistance, targetDistance, 1f - Mathf.Exp(-VisibilityPullSpeed * Time.deltaTime));
                DistanceMode = "visibility";
            }
            else
            {
                _currentDistance = Mathf.Lerp(_currentDistance, targetDistance, 1f - Mathf.Exp(-RecoverSpeed * Time.deltaTime));
                DistanceMode = "free";
            }
            if (pivot.y < _pivotY - 0.01f) DistanceMode += ", under roof";

            transform.position = pivot + back * _currentDistance;
            transform.rotation = rotation;
            PlayerFullyVisible = pointCount == 0 || Visible(transform.position, pointCount) == pointCount;
        }

        /// <summary>Keeps the current automatic offset while it sees the whole player, otherwise searches for one that does.</summary>
        private void ChooseView(Vector3 pivot, int pointCount)
        {
            float current = Score(pivot, _targetAutoYaw, _targetAutoPitch, pointCount, out int seen, out float ratio);
            // Fully visible and not squeezed against something: keep it.
            if (seen == pointCount && ratio > 0.6f)
            {
                // Clear. After a moment, try to drift back towards where the player aimed.
                _clearTime += Time.deltaTime;
                if (_clearTime > ReturnDelay && (_targetAutoYaw != 0f || _targetAutoPitch != 0f))
                {
                    Score(pivot, 0f, 0f, pointCount, out int seenHome, out float homeRatio);
                    if (seenHome == pointCount && homeRatio > 0.6f) _targetAutoYaw = _targetAutoPitch = 0f;
                }
                return;
            }
            _clearTime = 0f;
            if (Time.unscaledTime - _lastSearch < 0.08f) return; // search about 12 times a second while hidden
            _lastSearch = Time.unscaledTime;

            float bestScore = current, bestYaw = _targetAutoYaw, bestPitch = _targetAutoPitch;
            foreach (float pitchStep in PitchSteps)
            {
                if (pitchStep > MaxAutoPitch || _pitch + pitchStep < BottomClamp) continue;
                foreach (float yawStep in YawSteps)
                {
                    if (Mathf.Abs(yawStep) > MaxAutoYaw) continue;
                    float s = Score(pivot, yawStep, pitchStep, pointCount, out _, out _);
                    if (s > bestScore + 0.5f)
                    {
                        bestScore = s;
                        bestYaw = yawStep;
                        bestPitch = pitchStep;
                    }
                }
            }
            _targetAutoYaw = bestYaw;
            _targetAutoPitch = bestPitch;
        }

        /// <summary>How good a view is: seeing the whole body first, then staying far, then staying near the aim.</summary>
        private float Score(Vector3 pivot, float yawOffset, float pitchOffset, int pointCount, out int seen, out float ratio)
        {
            float pitch = Mathf.Clamp(_pitch + pitchOffset, BottomClamp, TopClamp);
            Vector3 back = -(Quaternion.Euler(pitch, _yaw + yawOffset, 0f) * Vector3.forward);
            float d = CollisionDistance(pivot, back, Distance);
            seen = Visible(pivot + back * d, pointCount);
            ratio = d / Distance;
            return seen * 10f + (d / Distance) * 3f - Mathf.Abs(yawOffset) / 60f - pitchOffset / 50f;
        }

        [Tooltip("Speed (m/s) the camera closes in when geometry passes between it and the player")]
        public float OcclusionPullSpeed = 6f;
        /// <summary>Why the camera distance did what it did this frame (diagnostics).</summary>
        public string DistanceMode { get; private set; } = "";
        private readonly Collider[] _inside = new Collider[8];

        /// <summary>Whether a camera at this position would be inside (or touching) level geometry.</summary>
        private bool CameraInside(Vector3 position)
        {
            int n = Physics.OverlapSphereNonAlloc(position, CollisionRadius, _inside, CollisionLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (_inside[i] != null && !IsPlayer(_inside[i].transform)) return true;
            return false;
        }

        [Tooltip("Height the camera keeps above the ground under it (it tilts up to stay above steps and ledges)")]
        public float GroundClearance = 0.35f;
        [Tooltip("Most the camera tilts up to clear the ground behind the player (degrees)")]
        public float MaxGroundLift = 30f;
        private float _groundPitch, _groundPitchVel;

        /// <summary>Extra downward pitch (degrees) that keeps the camera above the ground under where it would sit.</summary>
        private float GroundLift(Vector3 pivot, float pitch, float yaw)
        {
            Vector3 back = -(Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);
            Vector3 cam = pivot + back * Distance;
            // Find the ground under the camera, starting from the orbit point's height (never a roof above it).
            Vector3 from = new Vector3(cam.x, pivot.y, cam.z);
            if (!Physics.SphereCast(from, CollisionRadius * 0.5f, Vector3.down, out RaycastHit ground, pivot.y - cam.y + GroundClearance + 1f,
                    CollisionLayers, QueryTriggerInteraction.Ignore) || IsPlayer(ground.transform)) return 0f;
            float minY = ground.point.y + GroundClearance;
            if (cam.y >= minY) return 0f;
            float needed = Mathf.Asin(Mathf.Clamp((minY - pivot.y) / Distance, -1f, 1f)) * Mathf.Rad2Deg;
            return Mathf.Clamp(needed - pitch, 0f, MaxGroundLift);
        }

        private float CollisionDistance(Vector3 pivot, Vector3 back, float distance)
        {
            int n = Physics.SphereCastNonAlloc(pivot, CollisionRadius, back, _hits, distance, CollisionLayers,
                QueryTriggerInteraction.Ignore);
            float nearest = distance;
            for (int i = 0; i < n; i++)
            {
                if (IsPlayer(_hits[i].transform) || _hits[i].distance <= 0f) continue;
                nearest = Mathf.Min(nearest, _hits[i].distance - CollisionRadius);
            }
            return Mathf.Clamp(nearest, MinDistance, distance);
        }

        /// <summary>Largest distance (up to max) along back from which every sight point is visible.</summary>
        private float ClosestVisibleDistance(Vector3 pivot, Vector3 back, float max, int pointCount)
        {
            float lo = MinDistance, hi = max;
            if (Visible(pivot + back * lo, pointCount) < pointCount) return lo;
            for (int i = 0; i < 8; i++) // bisection
            {
                float mid = (lo + hi) * 0.5f;
                if (Visible(pivot + back * mid, pointCount) == pointCount) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        private int Visible(Vector3 cameraPosition, int pointCount)
        {
            int seen = 0;
            for (int i = 0; i < pointCount; i++)
            {
                Vector3 to = _points[i] - cameraPosition;
                float length = to.magnitude;
                if (length < 1e-3f) { seen++; continue; }
                int n = Physics.SphereCastNonAlloc(cameraPosition, SightRadius, to / length, _hits, Mathf.Max(0f, length - SightRadius),
                    CollisionLayers, QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int k = 0; k < n && !blocked; k++)
                    blocked = !IsPlayer(_hits[k].transform);
                if (!blocked) seen++;
            }
            return seen;
        }

        private int GatherSightPoints()
        {
            if (_player == null) return 0;
            if (_playerAnimator != null && _playerAnimator.isHuman)
            {
                int n = 0;
                foreach (var bone in SightBones)
                {
                    var t = _playerAnimator.GetBoneTransform(bone);
                    if (t != null) _points[n++] = t.position;
                }
                if (n > 0) return n;
            }
            Vector3 p = _player.position;
            _points[0] = p + Vector3.up * 1.65f;
            _points[1] = p + Vector3.up * 1.2f;
            _points[2] = p + Vector3.up * 0.9f;
            _points[3] = p + Vector3.up * 0.15f;
            return 4;
        }

        private void ResolvePlayer()
        {
            if (_player == null)
            {
                var cc = Target.GetComponentInParent<CharacterController>();
                _player = cc != null ? cc.transform : Target.root;
                _controller = _player != null ? _player.GetComponent<ThirdPersonController>() : null;
            }
            // The switcher swaps models: look the animator up again when it changes.
            if (_playerAnimator == null || !_playerAnimator.isActiveAndEnabled)
                _playerAnimator = _player != null ? _player.GetComponentInChildren<Animator>() : null;
        }

        private bool IsPlayer(Transform t) => _player != null && t.IsChildOf(_player);
    }
}
