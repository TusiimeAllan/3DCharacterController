using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Sets up context parkour (ParkourController):
    /// 1. analyses the MxM demo vault clips: finds the window where the move happens (leaving and rejoining
    ///    the ground) and samples the root path, into Assets/Art/Animations/Parkour/ParkourLibrary.asset;
    /// 2. adds one base-layer state per move to the Player Animator Controller (entered from Any State on
    ///    the "Parkour" int, starting at the window, leaving for Locomotion at its end), tagged "Parkour";
    /// 3. adds ParkourController to the Player prefab.
    /// Safe to re-run.
    /// </summary>
    public static class ParkourBuilder
    {
        private const string Tag = "[ParkourBuilder]";
        private const string LibraryPath = "Assets/Art/Animations/Parkour/ParkourLibrary.asset";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string PlayerPrefab = "Assets/Art/Characters/Player.prefab";
        private const string Events = "Assets/Plugins/MotionMatching/Demo/Animations/Events/";
        private const string StatePrefix = "PK ";
        private const float SampleRate = 60f;
        private const int PathSamples = 64;

        private static readonly (string path, ParkourKind kind)[] Sources =
        {
            (Events + "VaultUp/Run_JumpUpMedium_2Hands_Run.anim", ParkourKind.ClimbMedium),
            (Events + "VaultUp/Run_JumpUpMedium_2Hands_Run_Mirror.anim", ParkourKind.ClimbMedium),
            (Events + "VaultUp/Run_JumpUpHigh_Run.anim", ParkourKind.ClimbHigh),
            (Events + "VaultUp/Run_JumpUpHigh_Run_Mirror.anim", ParkourKind.ClimbHigh),
            (Events + "VaultUp/RunJump_ToLeft_2.anim", ParkourKind.Hurdle),
            (Events + "VaultUp/RunJump_ToRight_2.anim", ParkourKind.Hurdle),
            (Events + "VaultOff/_209_Run_JumpDownLow_Run.anim", ParkourKind.DropLow),
            (Events + "VaultOff/_209_Run_JumpDownLow_Run_Mirror.anim", ParkourKind.DropLow),
            (Events + "VaultOff/Run_JumpDownLow_Roll_Run.anim", ParkourKind.DropLow),
            (Events + "VaultOff/Run_JumpDownLow_Roll_Run_Mirror.anim", ParkourKind.DropLow),
            (Events + "VaultOff/Run_JumpDownHigh_Roll_Run.anim", ParkourKind.DropHigh),
            (Events + "VaultOff/Run_JumpDownHigh_Roll_Run_Mirror.anim", ParkourKind.DropHigh),
            (Events + "VaultUp/Run_JumpUpHigh_Run.anim", ParkourKind.ClimbFromHang),
            (Events + "VaultUp/Run_JumpUpHigh_Run_Mirror.anim", ParkourKind.ClimbFromHang),
            (Events + "RunSlide_ToRight_1.anim", ParkourKind.Slide),
            (Events + "RunSlide_ToRight_2.anim", ParkourKind.Slide),
        };

        [MenuItem("Tools/Character Controller/Add Parkour (Vault, Climb, Drop)")]
        public static void BuildAll()
        {
            var library = BuildLibrary();
            if (library == null) return;
            ApplyAnimator(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath), library);
            SetUpPlayer(library);
        }

        // ---------------------------------------------------------------- library

        public static ParkourLibrary BuildLibrary()
        {
            var moves = new List<ParkourMove>();
            foreach (var (path, kind) in Sources)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                {
                    Debug.LogWarning($"{Tag} Missing clip {path}, skipped.");
                    continue;
                }
                var move = Analyse(clip, kind);
                if (move == null)
                {
                    Debug.LogWarning($"{Tag} Could not find the move in {clip.name}, skipped.");
                    continue;
                }
                moves.Add(move);
            }
            if (moves.Count == 0)
            {
                Debug.LogError($"{Tag} No parkour clips could be analysed.");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath));
            var library = AssetDatabase.LoadAssetAtPath<ParkourLibrary>(LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<ParkourLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }
            library.Moves = moves;
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Library: " + string.Join(", ", moves.Select(m =>
                $"{m.Name} {m.Kind} h={m.Height:F2} contact={m.ContactDistance:F2} {m.Duration:F2}s")));
            return library;
        }

        /// <summary>Finds where the move happens in the clip and samples the root path over that window.</summary>
        private static ParkourMove Analyse(AnimationClip clip, ParkourKind kind)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            AnimationCurve Curve(string n)
            {
                var b = bindings.FirstOrDefault(x => x.propertyName == n);
                return b.propertyName == null ? null : AnimationUtility.GetEditorCurve(clip, b);
            }
            var cx = Curve("RootT.x");
            var cy = Curve("RootT.y");
            var cz = Curve("RootT.z");
            if (cx == null || cy == null || cz == null) return null;

            int n = Mathf.Max(2, Mathf.CeilToInt(clip.length * SampleRate) + 1);
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float t = clip.length * i / (n - 1);
                pos[i] = new Vector3(cx.Evaluate(t), cy.Evaluate(t), cz.Evaluate(t));
            }
            float dt = clip.length / (n - 1);
            int Frames(float seconds) => Mathf.RoundToInt(seconds / dt);
            float y0 = pos[0].y;

            int start, end, contact;
            float height;
            switch (kind)
            {
                case ParkourKind.ClimbMedium:
                case ParkourKind.ClimbHigh:
                case ParkourKind.ClimbFromHang:
                {
                    int rise = FirstIndex(pos, 0, p => p.y > y0 + 0.08f);
                    if (rise < 0) return null;
                    int peakSearchEnd = Mathf.Min(n - 1, rise + Frames(1.6f));
                    float top = 0f;
                    for (int i = rise; i <= peakSearchEnd; i++) top = Mathf.Max(top, pos[i].y);
                    int reached = FirstIndex(pos, rise, p => p.y >= top - 0.06f);
                    end = Mathf.Min(n - 1, reached + Frames(0.45f));
                    // Stop before the recording steps back down again.
                    int down = FirstIndex(pos, reached, p => p.y < top - 0.2f);
                    if (down > 0) end = Mathf.Min(end, down - 1);
                    start = Mathf.Max(0, rise - Frames(0.45f));
                    contact = rise;
                    height = pos[end].y - y0;
                    if (kind == ParkourKind.ClimbFromHang)
                    {
                        // Start where the hands have reached the ledge and the body hangs below it.
                        start = FirstIndex(pos, rise, p => p.y >= y0 + 0.6f * height);
                        if (start < 0 || start >= end) return null;
                        contact = start;
                        height = pos[end].y - pos[start].y;
                    }
                    break;
                }
                case ParkourKind.DropLow:
                case ParkourKind.DropHigh:
                {
                    int fall = FirstIndex(pos, 0, p => p.y < y0 - 0.25f); // below any crouch before the jump
                    if (fall < 0) return null;
                    float final = pos[n - 1].y;
                    int settled = n - 1;
                    for (int i = n - 1; i > fall; i--)
                        if (Mathf.Abs(pos[i].y - final) > 0.06f) { settled = i; break; }
                    start = Mathf.Max(0, fall - Frames(0.6f));
                    end = Mathf.Min(n - 1, settled + Frames(0.25f));
                    contact = Mathf.Max(start, fall - Frames(0.1f));
                    height = y0 - final;
                    break;
                }
                case ParkourKind.Slide:
                {
                    int dip = FirstIndex(pos, 0, p => p.y < y0 - 0.1f);
                    if (dip < 0) return null;
                    int low = dip;
                    for (int i = dip; i < n; i++) if (pos[i].y < pos[low].y) low = i;
                    int up = FirstIndex(pos, low, p => p.y >= y0 - 0.05f);
                    if (up < 0) up = n - 1;
                    start = Mathf.Max(0, dip - Frames(0.2f));
                    end = Mathf.Min(n - 1, up + Frames(0.15f));
                    contact = start;
                    height = y0 - pos[low].y;
                    break;
                }
                default: // Hurdle
                {
                    int up = FirstIndex(pos, 0, p => p.y > y0 + 0.15f); // above running bob
                    if (up < 0) return null;
                    int peak = up;
                    for (int i = up; i < Mathf.Min(n, up + Frames(1f)); i++) if (pos[i].y > pos[peak].y) peak = i;
                    int down = FirstIndex(pos, peak, p => p.y <= y0 + 0.03f);
                    if (down < 0) down = n - 1;
                    start = Mathf.Max(0, up - Frames(0.35f));
                    end = Mathf.Min(n - 1, down + Frames(0.3f));
                    contact = peak;
                    height = pos[peak].y - y0;
                    break;
                }
            }
            if (end <= start || height <= 0.05f) return null;

            // Forward = overall travel direction of the window.
            Vector3 travel = pos[end] - pos[start];
            travel.y = 0f;
            if (travel.sqrMagnitude < 1e-4f) return null;
            Vector3 fwd = travel.normalized;

            var path = new Vector2[PathSamples];
            for (int k = 0; k < PathSamples; k++)
            {
                float f = Mathf.Lerp(start, end, k / (PathSamples - 1f));
                int i = Mathf.Min((int)f, n - 2);
                Vector3 p = Vector3.Lerp(pos[i], pos[i + 1], f - i) - pos[start];
                path[k] = new Vector2(Vector3.Dot(new Vector3(p.x, 0f, p.z), fwd), p.y);
            }
            Vector3 toContact = pos[contact] - pos[start];

            return new ParkourMove
            {
                Name = kind == ParkourKind.ClimbFromHang ? clip.name + "_FromHang" : clip.name,
                Kind = kind,
                Clip = clip,
                StateName = StatePrefix + (kind == ParkourKind.ClimbFromHang ? clip.name + "_FromHang" : clip.name),
                WindowStart = start * dt,
                WindowEnd = end * dt,
                Height = height,
                ContactDistance = Mathf.Max(0.1f, Vector3.Dot(new Vector3(toContact.x, 0f, toContact.z), fwd)),
                Path = path,
            };
        }

        private static int FirstIndex(Vector3[] pos, int from, System.Func<Vector3, bool> test)
        {
            for (int i = Mathf.Max(0, from); i < pos.Length; i++) if (test(pos[i])) return i;
            return -1;
        }

        // ---------------------------------------------------------------- animator

        public static void ApplyAnimator(AnimatorController controller, ParkourLibrary library = null)
        {
            library = library != null ? library : AssetDatabase.LoadAssetAtPath<ParkourLibrary>(LibraryPath);
            if (controller == null || library == null)
            {
                Debug.LogWarning($"{Tag} Controller or parkour library missing, parkour states not added.");
                return;
            }
            if (controller.parameters.All(p => p.name != "Parkour"))
                controller.AddParameter("Parkour", AnimatorControllerParameterType.Int);

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            if (loco == null)
            {
                Debug.LogError($"{Tag} No Locomotion state.");
                return;
            }

            // Remove what an earlier run added.
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState != null &&
                                                                 t.destinationState.name.StartsWith(StatePrefix)).ToArray())
                sm.RemoveAnyStateTransition(t);
            foreach (var s in sm.states.Select(x => x.state).Where(s => s.name.StartsWith(StatePrefix)).ToArray())
                sm.RemoveState(s);

            for (int i = 0; i < library.Moves.Count; i++)
            {
                var m = library.Moves[i];
                var state = sm.AddState(m.StateName, new Vector3(-800f, -200f + i * 50f));
                state.motion = m.Clip;
                state.tag = "Parkour";
                state.writeDefaultValues = true;

                var enter = sm.AddAnyStateTransition(state);
                enter.canTransitionToSelf = false;
                enter.hasExitTime = false;
                enter.hasFixedDuration = true;
                enter.duration = 0.12f;
                enter.offset = m.WindowStart / m.Clip.length;
                enter.AddCondition(AnimatorConditionMode.Equals, i + 1, "Parkour");

                var exit = state.AddTransition(loco);
                exit.hasExitTime = true;
                exit.exitTime = m.WindowEnd / m.Clip.length;
                exit.hasFixedDuration = true;
                exit.duration = 0.2f;
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} {library.Moves.Count} parkour states added to {AssetDatabase.GetAssetPath(controller)}.");
        }

        // ---------------------------------------------------------------- player

        public static void SetUpPlayer(ParkourLibrary library)
        {
            var root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
            try
            {
                var pk = root.GetComponent<ParkourController>() ?? root.AddComponent<ParkourController>();
                pk.Library = library;
                pk.Controller = root.GetComponent<ThirdPersonController>();
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
                Debug.Log($"{Tag} ParkourController on {PlayerPrefab}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
