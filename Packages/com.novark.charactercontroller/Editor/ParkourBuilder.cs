using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Sets up context parkour (ParkourController):
    /// 1. analyses the profile's parkour clips: finds the window where the move happens (leaving and rejoining
    ///    the ground) and samples the root path, into the profile's parkour library;
    /// 2. adds one base-layer state per move to the Player Animator Controller (entered from Any State on
    ///    the "Parkour" int, starting at the window, leaving for Locomotion at its end), tagged "Parkour";
    /// 3. adds ParkourController to the Player prefab.
    /// Safe to re-run.
    /// </summary>
    public static class ParkourBuilder
    {
        private const string Tag = "[ParkourBuilder]";
        private const string StatePrefix = "PK ";
        private const float SampleRate = 60f;
        private const int PathSamples = 64;

        /// <summary>The bundled vault, climb, drop and slide clips (MxM demo mocap).</summary>
        public static List<CharacterSetupProfile.ParkourSource> DefaultSources()
        {
            var list = new List<CharacterSetupProfile.ParkourSource>();
            void Add(string path, ParkourKind kind)
            {
                var clip = KitPaths.EventClip(path);
                if (clip != null) list.Add(new CharacterSetupProfile.ParkourSource { Clip = clip, Kind = kind });
            }
            Add("VaultUp/Run_JumpUpMedium_2Hands_Run.anim", ParkourKind.ClimbMedium);
            Add("VaultUp/Run_JumpUpMedium_2Hands_Run_Mirror.anim", ParkourKind.ClimbMedium);
            Add("VaultUp/Run_JumpUpHigh_Run.anim", ParkourKind.ClimbHigh);
            Add("VaultUp/Run_JumpUpHigh_Run_Mirror.anim", ParkourKind.ClimbHigh);
            Add("VaultUp/RunJump_ToLeft_2.anim", ParkourKind.Hurdle);
            Add("VaultUp/RunJump_ToRight_2.anim", ParkourKind.Hurdle);
            Add("VaultOff/_209_Run_JumpDownLow_Run.anim", ParkourKind.DropLow);
            Add("VaultOff/_209_Run_JumpDownLow_Run_Mirror.anim", ParkourKind.DropLow);
            Add("VaultOff/Run_JumpDownLow_Roll_Run.anim", ParkourKind.DropLow);
            Add("VaultOff/Run_JumpDownLow_Roll_Run_Mirror.anim", ParkourKind.DropLow);
            Add("VaultOff/Run_JumpDownHigh_Roll_Run.anim", ParkourKind.DropHigh);
            Add("VaultOff/Run_JumpDownHigh_Roll_Run_Mirror.anim", ParkourKind.DropHigh);
            Add("VaultUp/Run_JumpUpHigh_Run.anim", ParkourKind.ClimbFromHang);
            Add("VaultUp/Run_JumpUpHigh_Run_Mirror.anim", ParkourKind.ClimbFromHang);
            Add("RunSlide_ToRight_1.anim", ParkourKind.Slide);
            Add("RunSlide_ToRight_2.anim", ParkourKind.Slide);
            return list;
        }

        // ---------------------------------------------------------------- library

        public static ParkourLibrary BuildLibrary(CharacterSetupProfile p)
        {
            var moves = new List<ParkourMove>();
            foreach (var source in p.ParkourClips)
            {
                if (source == null || source.Clip == null) continue;
                var clip = source.Clip;
                var kind = source.Kind;
                var move = Analyse(clip, kind);
                if (move == null)
                {
                    Debug.LogWarning($"{Tag} Could not find the move in {clip.name}, skipped.");
                    continue;
                }
                FindHandContacts(move);
                moves.Add(move);
            }
            if (moves.Count == 0)
            {
                Debug.LogWarning($"{Tag} Parkour skipped, no parkour clips in the profile could be analysed.");
                return null;
            }

            // A climb from a hang is the second half of a high climb: its hands hold the edge for the part of
            // the full climb's hold that falls inside its shorter window.
            foreach (var m in moves.Where(x => x.Kind == ParkourKind.ClimbFromHang))
            {
                var full = moves.FirstOrDefault(x => x.Kind == ParkourKind.ClimbHigh && x.Clip == m.Clip);
                if (full == null) continue;
                Vector2 Map(Vector2 w)
                {
                    if (w.y <= w.x) return Vector2.zero;
                    float t0 = Mathf.Lerp(full.WindowStart, full.WindowEnd, w.x);
                    float t1 = Mathf.Lerp(full.WindowStart, full.WindowEnd, w.y);
                    float a0 = Mathf.InverseLerp(m.WindowStart, m.WindowEnd, t0);
                    float a1 = Mathf.InverseLerp(m.WindowStart, m.WindowEnd, t1);
                    return a1 - a0 > 0.05f ? new Vector2(a0, a1) : Vector2.zero;
                }
                m.LeftHandContact = Map(full.LeftHandContact);
                m.RightHandContact = Map(full.RightHandContact);
            }

            AnimatorBuildUtil.EnsureFolderFor(p.ParkourLibraryPath);
            var library = AssetDatabase.LoadAssetAtPath<ParkourLibrary>(p.ParkourLibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<ParkourLibrary>();
                AssetDatabase.CreateAsset(library, p.ParkourLibraryPath);
            }
            library.Moves = moves;
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag} Library: " + string.Join(", ", moves.Select(m =>
                $"{m.Name} {m.Kind} h={m.Height:F2} contact={m.ContactDistance:F2} {m.Duration:F2}s hands L{m.LeftHandContact} R{m.RightHandContact} hold h={m.HoldHandHeight:F2} f={m.HoldHandForward:F2} rise={m.HoldRootRise:F2} ahead={m.HoldRootForward:F2}")));
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

        /// <summary>
        /// Plays the move on the X Bot and finds, for each hand, the longest stretch where it is still and up at
        /// the obstacle top in the recording: that is when the hand holds the edge.
        /// </summary>
        private static void FindHandContacts(ParkourMove move)
        {
            if (move.Kind == ParkourKind.DropLow || move.Kind == ParkourKind.DropHigh || move.Kind == ParkourKind.Slide) return;
            const string modelPath = KitPaths.XBot; // the move is measured on the reference body
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            if (model == null || avatar == null) return;
            var go = Object.Instantiate(model);
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var animator = go.GetComponent<Animator>();
                if (animator == null) animator = go.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.applyRootMotion = false;
                const int n = 90;
                var left = new Vector3[n];
                var right = new Vector3[n];
                float footStart = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = Mathf.Lerp(move.WindowStart, move.WindowEnd, i / (n - 1f));
                    move.Clip.SampleAnimation(go, t);
                    left[i] = animator.GetBoneTransform(HumanBodyBones.LeftHand).position;
                    right[i] = animator.GetBoneTransform(HumanBodyBones.RightHand).position;
                    if (i == 0)
                        footStart = Mathf.Min(animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y,
                            animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y) - 0.08f;
                }
                // Top of the obstacle in the recording's own coordinates.
                float top = footStart + (move.Kind == ParkourKind.Hurdle ? move.Height - 0.2f : move.Height);
                if (move.Kind == ParkourKind.ClimbFromHang) top = Mathf.Max(left.Max(h => h.y), right.Max(h => h.y)) - 0.05f;
                float dt = move.Duration / (n - 1);
                move.LeftHandContact = Window(left, top, dt);
                move.RightHandContact = Window(right, top, dt);
                RecordHold(move, left, right, footStart, n);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Where the hands and the root are while the hands hold the edge, in the recording, so the runtime can
        /// stretch the path to put the hands exactly on a real edge at that moment.
        /// </summary>
        private static void RecordHold(ParkourMove move, Vector3[] left, Vector3[] right, float footStart, int n)
        {
            Vector2 l = move.LeftHandContact, r = move.RightHandContact;
            bool hasL = l.y > l.x, hasR = r.y > r.x;
            move.HasHold = hasL || hasR;
            if (!move.HasHold) return;
            // Frames where both hands hold (or the one that does).
            float a = hasL && hasR ? Mathf.Max(l.x, r.x) : (hasL ? l.x : r.x);
            float b = hasL && hasR ? Mathf.Min(l.y, r.y) : (hasL ? l.y : r.y);
            if (b <= a) { a = Mathf.Min(hasL ? l.x : 1f, hasR ? r.x : 1f); b = Mathf.Max(l.y, r.y); }
            move.HoldEnd = Mathf.Max(l.y, r.y);

            var bindings = AnimationUtility.GetCurveBindings(move.Clip);
            AnimationCurve Curve(string name) =>
                AnimationUtility.GetEditorCurve(move.Clip, bindings.First(x => x.propertyName == name));
            var cx = Curve("RootT.x"); var cy = Curve("RootT.y"); var cz = Curve("RootT.z");
            Vector3 Root(float t) => new Vector3(cx.Evaluate(t), cy.Evaluate(t), cz.Evaluate(t));
            Vector3 start = Root(move.WindowStart);
            Vector3 travel = Root(move.WindowEnd) - start;
            travel.y = 0f;
            Vector3 fwd = travel.normalized;

            float handH = 0f, handF = 0f, rise = 0f, ahead = 0f;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                float p = i / (n - 1f);
                if (p < a || p > b) continue;
                Vector3 hand;
                bool lHold = hasL && p >= l.x && p <= l.y, rHold = hasR && p >= r.x && p <= r.y;
                if (lHold && rHold) hand = (left[i] + right[i]) * 0.5f;
                else if (lHold) hand = left[i];
                else if (rHold) hand = right[i];
                else continue;
                Vector3 root = Root(Mathf.Lerp(move.WindowStart, move.WindowEnd, p)) - start;
                handH += hand.y - footStart;
                handF += Vector3.Dot(new Vector3(hand.x - start.x, 0f, hand.z - start.z), fwd);
                rise += root.y;
                ahead += Vector3.Dot(new Vector3(root.x, 0f, root.z), fwd);
                count++;
            }
            if (count == 0) { move.HasHold = false; return; }
            move.HoldHandHeight = handH / count;
            move.HoldHandForward = handF / count;
            move.HoldRootRise = rise / count;
            move.HoldRootForward = ahead / count;
        }

        private static Vector2 Window(Vector3[] hand, float top, float dt)
        {
            int n = hand.Length, bestStart = -1, bestLen = 0, start = -1;
            for (int i = 0; i < n; i++)
            {
                float speed = i == 0 ? (hand[1] - hand[0]).magnitude / dt : (hand[i] - hand[i - 1]).magnitude / dt;
                bool holding = speed < 1.0f && hand[i].y > top - 0.45f && hand[i].y < top + 0.6f;
                if (holding && start < 0) start = i;
                if ((!holding || i == n - 1) && start >= 0)
                {
                    int end = holding ? i : i - 1;
                    if (end - start + 1 > bestLen) { bestLen = end - start + 1; bestStart = start; }
                    start = -1;
                }
            }
            if (bestLen < 4) return Vector2.zero; // a brush, not a hold
            return new Vector2(bestStart / (n - 1f), (bestStart + bestLen - 1) / (n - 1f));
        }

        private static int FirstIndex(Vector3[] pos, int from, System.Func<Vector3, bool> test)
        {
            for (int i = Mathf.Max(0, from); i < pos.Length; i++) if (test(pos[i])) return i;
            return -1;
        }

        // ---------------------------------------------------------------- animator

        public static bool ApplyAnimator(AnimatorController controller, ParkourLibrary library)
        {
            if (controller == null || library == null) return false;
            if (controller.parameters.All(p => p.name != "Parkour"))
                controller.AddParameter("Parkour", AnimatorControllerParameterType.Int);

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            if (loco == null)
            {
                Debug.LogError($"{Tag} No Locomotion state.");
                return false;
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
            return true;
        }

        // ---------------------------------------------------------------- player

        public static void SetUpPlayer(GameObject root, ParkourLibrary library)
        {
            var pk = Kit.Ensure<ParkourController>(root);
            pk.Library = library;
            pk.Controller = root.GetComponent<ThirdPersonController>();
        }
    }
}
