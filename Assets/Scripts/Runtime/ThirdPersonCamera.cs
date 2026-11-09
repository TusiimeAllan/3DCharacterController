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

        public bool PlayerFullyVisible { get; private set; } = true;
        /// <summary>Vertical shift of the orbit point set by gameplay (e.g. following a diver's head down).</summary>
        [System.NonSerialized] public float PivotOffset;

        private static readonly HumanBodyBones[] SightBones =
            { HumanBodyBones.Head, HumanBodyBones.Chest, HumanBodyBones.Hips, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
        private static readonly float[] YawSteps = { 0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 80f, -80f };
        private static readonly float[] PitchSteps = { 0f, 12f, 24f, 35f };

        private float _yaw;
        private float _pitch = 15f;
        private float _currentDistance;
        private float _autoYaw, _autoPitch, _autoYawVel, _autoPitchVel;
        private float _targetAutoYaw, _targetAutoPitch;
        private float _clearTime;
        private float _lastSearch = -1f;
        private Transform _player;
        private Animator _playerAnimator;
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

            Vector3 pivot = Target.position + Vector3.up * (HeightOffset + PivotOffset);
            int pointCount = GatherSightPoints();

            if (KeepPlayerVisible && !looking && pointCount > 0) ChooseView(pivot, pointCount);

            _autoYaw = Mathf.SmoothDampAngle(_autoYaw, _targetAutoYaw, ref _autoYawVel, AutoSmoothTime);
            _autoPitch = Mathf.SmoothDamp(_autoPitch, _targetAutoPitch, ref _autoPitchVel, AutoSmoothTime);

            float pitch = Mathf.Clamp(_pitch + _autoPitch, BottomClamp, TopClamp);
            Quaternion rotation = Quaternion.Euler(pitch, _yaw + _autoYaw, 0f);
            Vector3 back = -(rotation * Vector3.forward);

            float targetDistance = CollisionDistance(pivot, back, Distance);
            // Still hidden at this angle (nothing better found): come closer until the body shows.
            if (KeepPlayerVisible && pointCount > 0 && Visible(pivot + back * targetDistance, pointCount) < pointCount)
                targetDistance = ClosestVisibleDistance(pivot, back, targetDistance, pointCount);

            // Snap in instantly so we never clip through walls, ease back out smoothly.
            if (targetDistance < _currentDistance)
                _currentDistance = targetDistance;
            else
                _currentDistance = Mathf.Lerp(_currentDistance, targetDistance, 1f - Mathf.Exp(-RecoverSpeed * Time.deltaTime));

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
                if (pitchStep > MaxAutoPitch) continue;
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
            }
            // The switcher swaps models: look the animator up again when it changes.
            if (_playerAnimator == null || !_playerAnimator.isActiveAndEnabled)
                _playerAnimator = _player != null ? _player.GetComponentInChildren<Animator>() : null;
        }

        private bool IsPlayer(Transform t) => _player != null && t.IsChildOf(_player);
    }
}
