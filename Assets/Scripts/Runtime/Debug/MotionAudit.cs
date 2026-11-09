using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Animations;

namespace CharacterControllerSystem.Diagnostics
{
    /// <summary>
    /// Development tool: drives the Player through scripted scenarios on the test course and measures what
    /// looks unrealistic, frame by frame (after animation, IK and the camera have run):
    /// - teleports (the capsule moving further than its speed allows) and snapping rotations;
    /// - body parts inside floors or walls (each bone tested as a sphere);
    /// - feet floating above or sunk into the ground while grounded;
    /// - planted feet sliding;
    /// - the camera lurching in or losing sight of the player;
    /// - animation states flickering.
    /// Add it to any GameObject in Play mode (it disables real input while it runs). The report goes to the
    /// Console and to Temp/MotionAudit.txt.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class MotionAudit : MonoBehaviour
    {
        public float SettleTime = 0.6f;
        [Tooltip("Only run scenarios whose name contains this (empty = all)")]
        public string Filter = "";

        public bool Done { get; private set; }
        public string Report { get; private set; } = "";

        private class Scenario
        {
            public string Name;
            public Vector3 Start;
            public float Yaw;
            public float Duration;
            public Action<float> Drive;
        }

        private class Stats
        {
            public int Frames, Teleports, RotationSnaps, Floats, Sinks, SlideFrames, PlantedFrames, Hidden, StateChanges;
            public float MaxTeleport, MaxSpin, MaxFloat, MaxSink, SlideSum, MinCamera = 99f, MaxCameraIn;
            public float MaxPenetration;
            public string CameraWhy = "";
            public string PenetrationWhere = "";
            public readonly List<string> Notes = new List<string>();
        }

        private static readonly (HumanBodyBones bone, float radius)[] Probes =
        {
            (HumanBodyBones.Hips, 0.12f), (HumanBodyBones.Spine, 0.11f), (HumanBodyBones.Chest, 0.12f), (HumanBodyBones.Head, 0.09f),
            (HumanBodyBones.LeftUpperLeg, 0.07f), (HumanBodyBones.RightUpperLeg, 0.07f), (HumanBodyBones.LeftLowerLeg, 0.05f),
            (HumanBodyBones.RightLowerLeg, 0.05f), (HumanBodyBones.LeftUpperArm, 0.05f), (HumanBodyBones.RightUpperArm, 0.05f),
            (HumanBodyBones.LeftLowerArm, 0.04f), (HumanBodyBones.RightLowerArm, 0.04f), (HumanBodyBones.LeftHand, 0.03f),
            (HumanBodyBones.RightHand, 0.03f),
        };

        private PlayerInputs _in;
        private ThirdPersonController _tpc;
        private ThirdPersonCamera _cam;
        private ParkourController _pk;
        private ClimbController _climb;
        private SwimController _swim;
        private MotionMatchingLocomotion _mm;
        private Animator _anim;
        private CharacterController _cc;
        private Transform _dir;
        private SphereCollider _probe;
        private readonly Collider[] _hits = new Collider[16];

        private Stats _s;
        private bool _recording;
        private Vector3 _lastPos;
        private float _lastYaw, _lastCam;
        private int _lastState;
        private Vector3[] _lastFoot = new Vector3[2];
        private bool _hasLast;
        private int _frames;
        private float _time;

        private IEnumerator Start()
        {
            _in = FindAnyObjectByType<PlayerInputs>();
            if (_in == null) { Debug.LogError("[MotionAudit] No player."); yield break; }
            _tpc = _in.GetComponent<ThirdPersonController>();
            _cam = FindAnyObjectByType<ThirdPersonCamera>();
            _pk = _in.GetComponent<ParkourController>();
            _climb = _in.GetComponent<ClimbController>();
            _swim = _in.GetComponent<SwimController>();
            _mm = _in.GetComponent<MotionMatchingLocomotion>();
            _anim = _in.GetComponent<Animator>();
            _cc = _in.GetComponent<CharacterController>();

            var pi = _in.GetComponent<UnityEngine.InputSystem.PlayerInput>();
            if (pi != null) pi.DeactivateInput();
            _in.cursorInputForLook = false;
            _in.look = Vector2.zero;
            _dir = new GameObject("AuditDirection").transform;
            _tpc.CameraTransform = _dir; // scripted moves are relative to this, not the (smart) camera
            var traj = _in.GetComponent<MxM.MxMTrajectoryGenerator>();
            if (traj != null) traj.RelativeCameraTransform = _dir;

            var pgo = new GameObject("AuditProbe") { layer = 2 };
            _probe = pgo.AddComponent<SphereCollider>();
            _probe.isTrigger = true;

            var report = new StringBuilder();
            report.AppendLine("MOTION AUDIT " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            foreach (var sc in Scenarios())
            {
                if (!string.IsNullOrEmpty(Filter) && sc.Name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Reset(sc);
                float t = 0f;
                while (t < SettleTime) { t += Time.deltaTime; yield return null; }
                _s = new Stats();
                _hasLast = false;
                _recording = true;
                t = 0f;
                while (t < sc.Duration)
                {
                    sc.Drive?.Invoke(t);
                    t += Time.deltaTime;
                    yield return null;
                }
                _recording = false;
                report.AppendLine(Summarise(sc.Name, _s));
            }
            report.AppendLine($"(average frame rate {_frames / Mathf.Max(0.01f, _time):F0} fps)");
            Report = report.ToString();
            Debug.Log(Report);
            try { System.IO.File.WriteAllText("Temp/MotionAudit.txt", Report); } catch { /* best effort */ }
            Done = true;
        }

        private void Reset(Scenario sc)
        {
            // A move still running from the last scenario would drag the character back after the teleport.
            if (_pk != null) _pk.Cancel();
            if (_climb != null) _climb.Cancel();
            if (_swim != null) _swim.Cancel();
            var actions = _in.GetComponent<CharacterActions>();
            if (actions != null && actions.IsDead) actions.Revive();
            _in.move = Vector2.zero; _in.sprint = _in.strafe = _in.crouch = _in.crouchHeld = _in.jumpHeld = _in.jump = false;
            _cc.enabled = false;
            _in.transform.SetPositionAndRotation(sc.Start, Quaternion.Euler(0f, sc.Yaw, 0f));
            _cc.enabled = true;
            _dir.rotation = Quaternion.Euler(0f, sc.Yaw, 0f);
            if (_cam != null) _cam.SetOrientation(sc.Yaw, 15f);
        }

        // ---------------------------------------------------------------- scenarios

        private IEnumerable<Scenario> Scenarios()
        {
            Vector2 F = new Vector2(0f, 1f), B = new Vector2(0f, -1f), R = new Vector2(1f, 0f), L = new Vector2(-1f, 0f);
            Scenario S(string n, float x, float y, float z, float yaw, float dur, Action<float> drive) =>
                new Scenario { Name = n, Start = new Vector3(x, y, z), Yaw = yaw, Duration = dur, Drive = drive };

            yield return S("open: walk, run, turn, stop", -30f, 0.05f, -46f, 90f, 7f, t =>
            {
                _in.move = t < 6f ? new Vector2(Mathf.Sin(t * 0.9f), Mathf.Cos(t * 0.9f)) : Vector2.zero;
                _in.sprint = t > 2f && t < 4f;
            });
            yield return S("open: sprint then hard 180", -40f, 0.05f, -46f, 90f, 4f, t => { _in.move = t < 2f ? F : (t < 3.3f ? B : Vector2.zero); _in.sprint = t < 3.3f; });
            yield return S("strafe: all directions", -30f, 0.05f, -46f, 90f, 6f, t =>
            {
                _in.strafe = true;
                _in.move = t < 1.5f ? R : t < 3f ? B : t < 4.5f ? L : F;
            });
            yield return S("curbs: walk over", -40f, 0.05f, 3f, 0f, 6f, t => _in.move = F);
            yield return S("stairs: walk up", -33f, 0.05f, 3f, 0f, 6f, t => _in.move = F);
            yield return S("ramps: 25 deg up", -24.03f, 0.05f, 7f, 0f, 5f, t => _in.move = F);
            yield return S("ramps: 60 deg slide", -18.625f, 2.6f, 12.6f, 180f, 3f, t => _in.move = F);
            yield return S("uneven ground: walk", -34f, 0.05f, -20f, 90f, 7f, t => _in.move = F);
            yield return S("parkour: walk at 1.1 m", 12f, 0.05f, -21f, 180f, 6f, t => _in.move = F);
            yield return S("parkour: sprint 2.2 m", 17f, 0.05f, -21f, 180f, 6f, t => { _in.move = F; _in.sprint = true; });
            yield return S("parkour: sprint hurdles", 9.5f, 0.05f, -21f, 180f, 5f, t => { _in.move = F; _in.sprint = true; });
            yield return S("edge: walk to 2.2 m top edge and stop", 17f, 2.35f, -28.2f, 180f, 3f, t => _in.move = t < 1.7f ? F : Vector2.zero);
            yield return S("drop tower: walk off 2 m", 32f, 2.1f, 15f, 180f, 4f, t => _in.move = F);
            yield return S("tunnels: slide into 1.1 m", 7.7f, 0.05f, -6f, 0f, 5f, t =>
            {
                _in.move = F; _in.sprint = true;
                if (!_in.crouch && _in.transform.position.z > 6.5f && !_pk.IsActive && t < 4f) _in.crouch = true;
            });
            yield return S("ledge: jump, hang, shimmy, climb", -44f, 0.05f, -18.2f, 180f, 6f, t =>
            {
                _in.move = t < 0.25f ? F * 0.5f : t > 1.5f && t < 2.8f ? R : t > 3.2f ? F : Vector2.zero;
                if (t < 0.05f) _in.jump = true;
            });
            yield return S("ladder: up and over", -44f, 0.05f, -35.2f, 180f, 7f, t => _in.move = F);
            yield return S("pool: deck, swim, dive, beach", 19f, 0.05f, -45.5f, 90f, 12f, t =>
            {
                _in.move = F;
                _in.crouchHeld = t > 5f && t < 6.5f;
            });
        }

        // ---------------------------------------------------------------- measuring

        private void LateUpdate()
        {
            if (!_recording || _s == null || _anim == null) return;
            // Rates over at least a 60 fps frame: the Editor sometimes ticks with tiny steps that inflate them.
            float dt = Mathf.Max(1f / 60f, Time.deltaTime);
            _s.Frames++;
            _frames++;
            _time += Time.deltaTime;
            Vector3 pos = _in.transform.position;
            float yaw = _in.transform.eulerAngles.y;
            string state = StateName();

            if (_hasLast)
            {
                float moved = (pos - _lastPos).magnitude;
                float allowed = 12f * dt + 0.12f;
                if (moved > allowed) { _s.Teleports++; _s.MaxTeleport = Mathf.Max(_s.MaxTeleport, moved); Note($"jump {moved:F2} m in one frame ({state})"); }
                float spin = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) / dt;
                if (spin > 760f) { _s.RotationSnaps++; _s.MaxSpin = Mathf.Max(_s.MaxSpin, spin); Note($"spin {spin:F0} deg/s ({state})"); }
                int stateHash = state.GetHashCode();
                if (stateHash != _lastState) _s.StateChanges++;
            }

            Penetration(state);
            Feet(dt, state);
            CameraCheck(dt);

            _lastPos = pos;
            _lastYaw = yaw;
            _lastState = state.GetHashCode();
            _hasLast = true;
        }

        private void Penetration(string state)
        {
            bool holdingEdge = (_pk != null && _pk.IsActive && _pk.HasLedge) || (_climb != null && _climb.IsClimbing);
            foreach (var (bone, radius) in Probes)
            {
                var t = _anim.GetBoneTransform(bone);
                if (t == null) continue;
                bool hand = bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand;
                float tolerance = hand && holdingEdge ? 0.06f : 0.02f;
                Vector3 p = t.position;
                float depth = 0f;
                string what = "floor";
                // Floors from above (a thin floor would push a sunk probe out through the bottom).
                // Only for surfaces with no volume to test against (terrain, planes): a box is measured by its
                // true shortest way out, so an elbow just past a front face is not counted as deep under the top.
                if (Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out RaycastHit floor, 0.6f + radius, _tpc.GroundLayers,
                        QueryTriggerInteraction.Ignore) && !floor.transform.IsChildOf(_in.transform) && floor.normal.y > 0.6f &&
                    floor.collider is TerrainCollider)
                {
                    if (floor.point.y + radius - p.y > depth) what = floor.collider.name;
                    depth = Mathf.Max(depth, floor.point.y + radius - p.y);
                }
                _probe.radius = radius;
                int n = Physics.OverlapSphereNonAlloc(p, radius, _hits, _tpc.GroundLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = _hits[i];
                    if (c == null || c.transform.IsChildOf(_in.transform) || c == _probe) continue;
                    if (BodyContactSolver.SpherePenetration(c, _probe, p, radius, out Vector3 dir, out float dist))
                    {
                        if (dist > depth) what = c.name;
                        depth = Mathf.Max(depth, dist); // walls (floors are measured from above)
                    }
                }
                depth -= tolerance;
                if (depth > _s.MaxPenetration)
                {
                    _s.MaxPenetration = depth;
                    string ledge = ", " + what;
                    if (_pk != null && _pk.IsActive && _pk.HasLedge)
                    {
                        Vector3 rel = p - _pk.LedgePoint;
                        ledge += $", p {_pk.Progress:F2}, {Vector3.Dot(rel, _pk.LedgeFace):F2} past face, {rel.y:F2} vs top";
                    }
                    _s.PenetrationWhere = $"{bone} {depth * 100f:F0} cm in ({state}{ledge})";
                }
            }
        }

        private void Feet(float dt, string state)
        {
            bool normal = _tpc.Grounded && !_tpc.ExternallyDriven && !_tpc.IsSlidingOnSlope &&
                          !(_swim != null && (_swim.IsSwimming || _swim.IsWading)) && !(_mm != null && _mm.InLandingState);
            float lowestGap = float.MaxValue;
            int lowest = -1;
            var gaps = new float[] { float.MaxValue, float.MaxValue };
            var feet = new Transform[2];
            for (int k = 0; k < 2; k++)
            {
                var foot = _anim.GetBoneTransform(k == 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                feet[k] = foot;
                if (foot == null) continue;
                float sole = foot.position.y - (k == 0 ? _anim.leftFeetBottomHeight : _anim.rightFeetBottomHeight);
                if (!Physics.Raycast(foot.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit g, 1.5f, _tpc.GroundLayers,
                        QueryTriggerInteraction.Ignore)) continue;
                gaps[k] = sole - g.point.y;
                if (gaps[k] < lowestGap) { lowestGap = gaps[k]; lowest = k; }
            }
            // The supporting foot (the lower one, on the ground) is the one that must not slide; the other may be
            // skimming the ground on its way forward.
            if (normal && _hasLast && lowest >= 0 && gaps[lowest] < 0.03f && feet[lowest] != null)
            {
                Vector3 v = (feet[lowest].position - _lastFoot[lowest]) / dt;
                v.y = 0f;
                _s.PlantedFrames++;
                _s.SlideSum += v.magnitude;
                if (v.magnitude > 0.35f) _s.SlideFrames++;
            }
            for (int k = 0; k < 2; k++) if (feet[k] != null) _lastFoot[k] = feet[k].position;
            if (!normal || lowest < 0) return;
            Vector3 vel = _cc.velocity;
            vel.y = 0f;
            if (lowestGap > 0.07f && vel.magnitude < 2.5f) // running and sprinting have a flight phase { _s.Floats++; _s.MaxFloat = Mathf.Max(_s.MaxFloat, lowestGap); }
            if (lowestGap < -0.05f) { _s.Sinks++; _s.MaxSink = Mathf.Max(_s.MaxSink, -lowestGap); }
        }

        private void CameraCheck(float dt)
        {
            if (_cam == null) return;
            float d = Vector3.Distance(_cam.transform.position, _cam.Target.position);
            _s.MinCamera = Mathf.Min(_s.MinCamera, d);
            if (_hasLast && (_lastCam - d) / dt > _s.MaxCameraIn)
            {
                _s.MaxCameraIn = (_lastCam - d) / dt;
                _s.CameraWhy = _cam.DistanceMode;
            }
            if (!_cam.PlayerFullyVisible) _s.Hidden++;
            _lastCam = d;
        }

        private string StateName()
        {
            if (_pk != null && _pk.IsActive) return "parkour " + _pk.CurrentKind;
            if (_climb != null && _climb.IsClimbing) return "climb " + _climb.Current;
            if (_swim != null && _swim.IsSwimming) return _swim.IsUnderwater ? "diving" : "swimming";
            if (_tpc.IsSlidingOnSlope) return "slope slide";
            if (!_tpc.Grounded) return "airborne";
            if (_mm != null && _mm.InLandingState) return "landing";
            if (_tpc.IsCrouching) return "crouch";
            if (_tpc.IsStrafing) return "strafe";
            return "locomotion";
        }

        private void Note(string text)
        {
            if (_s.Notes.Count < 4) _s.Notes.Add(text);
        }

        private static string Summarise(string name, Stats s)
        {
            var flags = new List<string>();
            if (s.Teleports > 0) flags.Add($"TELEPORT x{s.Teleports} (max {s.MaxTeleport:F2} m)");
            if (s.RotationSnaps > 0) flags.Add($"SNAP TURN x{s.RotationSnaps} (max {s.MaxSpin:F0} deg/s)");
            if (s.MaxPenetration > 0.03f) flags.Add($"INSIDE GEOMETRY {s.PenetrationWhere}");
            if (s.Floats > 3) flags.Add($"FEET FLOATING {s.Floats} frames (max {s.MaxFloat * 100f:F0} cm)");
            if (s.Sinks > 3) flags.Add($"FEET SUNK {s.Sinks} frames (max {s.MaxSink * 100f:F0} cm)");
            float slide = s.PlantedFrames > 0 ? (float)s.SlideFrames / s.PlantedFrames : 0f;
            if (slide > 0.35f) flags.Add($"FOOT SLIDE {slide * 100f:F0}% of planted frames (avg {s.SlideSum / Mathf.Max(1, s.PlantedFrames):F2} m/s)");
            if (s.MinCamera < 1.5f) flags.Add($"CAMERA CLOSE {s.MinCamera:F2} m");
            if (s.MaxCameraIn > 6f) flags.Add($"CAMERA LURCH {s.MaxCameraIn:F1} m/s ({s.CameraWhy})");
            float hidden = s.Frames > 0 ? (float)s.Hidden / s.Frames : 0f;
            if (hidden > 0.1f) flags.Add($"PLAYER HIDDEN {hidden * 100f:F0}% of frames");
            string verdict = flags.Count == 0 ? "OK" : string.Join(" | ", flags);
            string notes = s.Notes.Count > 0 ? "\n      " + string.Join("\n      ", s.Notes) : "";
            return $"- {name} [{s.Frames} frames]: {verdict}{notes}";
        }
    }
}
