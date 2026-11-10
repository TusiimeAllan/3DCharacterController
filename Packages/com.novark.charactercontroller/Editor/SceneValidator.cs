using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Checks the open scene for everything the character needs: a Player with a working Humanoid, input,
    /// a camera that follows it, ground under it on the ground layers, ladders and water marked up, and no
    /// broken components. Each issue can be selected, and most have a one-click fix.
    /// </summary>
    public class SceneValidator : EditorWindow
    {
        public enum Severity { Error, Warning, Info }

        public class Issue
        {
            public Severity Severity;
            public string Message;
            public Object Context;
            public string FixLabel;
            public Action Fix;
        }

        private List<Issue> _issues = new List<Issue>();
        private Vector2 _scroll;

        [MenuItem("Tools/Character Controller/Validate Scene", priority = 40)]
        public static void ValidateAndReportMenu() => ValidateAndReport();

        public static void ValidateAndReport(bool quietWhenClean = false)
        {
            var issues = Validate();
            bool clean = issues.All(i => i.Severity == Severity.Info);
            foreach (var i in issues.Where(i => i.Severity != Severity.Info))
                (i.Severity == Severity.Error ? (Action<object, Object>)Debug.LogError : Debug.LogWarning)("[CharacterKit] " + i.Message, i.Context);
            if (clean && quietWhenClean) return;
            var w = GetWindow<SceneValidator>(false, "Character Scene Check");
            w._issues = issues;
            w.Show();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"{_issues.Count(i => i.Severity == Severity.Error)} errors, " +
                                $"{_issues.Count(i => i.Severity == Severity.Warning)} warnings", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Fix All", EditorStyles.toolbarButton))
                {
                    foreach (var i in _issues.Where(i => i.Fix != null).ToList()) i.Fix();
                    _issues = Validate();
                }
                if (GUILayout.Button("Re-check", EditorStyles.toolbarButton)) _issues = Validate();
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_issues.All(i => i.Severity == Severity.Info))
                EditorGUILayout.HelpBox("The scene is ready for the character.", MessageType.Info);
            foreach (var issue in _issues)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var type = issue.Severity == Severity.Error ? MessageType.Error
                        : issue.Severity == Severity.Warning ? MessageType.Warning : MessageType.Info;
                    EditorGUILayout.HelpBox(issue.Message, type);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(70)))
                    {
                        if (issue.Context != null && GUILayout.Button("Select")) { Selection.activeObject = issue.Context; EditorGUIUtility.PingObject(issue.Context); }
                        if (issue.Fix != null && GUILayout.Button(issue.FixLabel ?? "Fix"))
                        {
                            issue.Fix();
                            _issues = Validate();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        public static List<Issue> Validate()
        {
            var issues = new List<Issue>();
            void Add(Severity s, string msg, Object ctx = null, string fixLabel = null, Action fix = null) =>
                issues.Add(new Issue { Severity = s, Message = msg, Context = ctx, FixLabel = fixLabel, Fix = fix });

            var players = Object.FindObjectsByType<ThirdPersonController>(FindObjectsInactive.Exclude);
            if (players.Length == 0)
            {
                Add(Severity.Error, "No Player in the scene. Use Tools > Character Controller > Character Wizard, or right-click a character > Character Controller > Make Playable.",
                    null, "Wizard", () => CharacterWizard.Open(null));
                return issues;
            }
            if (players.Length > 1)
                Add(Severity.Warning, $"{players.Length} Players in the scene; input and the camera follow only one.", players[1].gameObject);

            foreach (var tpc in players) CheckPlayer(tpc, Add);
            CheckScene(players[0], Add);
            return issues;
        }

        private static void CheckPlayer(ThirdPersonController tpc, Action<Severity, string, Object, string, Action> add)
        {
            var go = tpc.gameObject;
            int missing = go.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
            if (missing > 0) add(Severity.Error, $"{go.name} has {missing} missing script(s).", go, null, null);

            var lossy = go.transform.lossyScale;
            if ((lossy - Vector3.one).sqrMagnitude > 1e-4f)
                add(Severity.Warning, $"{go.name} is scaled {lossy}. Keep the Player at scale 1; body size is measured from the character itself.",
                    go, "Reset", () => { Undo.RecordObject(go.transform, "Reset Scale"); go.transform.localScale = Vector3.one; });

            var animator = go.GetComponent<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
                add(Severity.Error, $"{go.name} has no Animator Controller. Rebuild it with the Character Wizard.", go, null, null);
            else if (animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid)
                add(Severity.Error, $"{go.name}'s avatar is not a valid Humanoid. Run the character through the Character Wizard.", go, null, null);

            if (go.transform.Find("CameraRoot") == null)
                add(Severity.Error, $"{go.name} has no CameraRoot child for the camera to follow.", go, "Add", () =>
                {
                    var root = new GameObject("CameraRoot");
                    Undo.RegisterCreatedObjectUndo(root, "Add CameraRoot");
                    root.transform.SetParent(go.transform, false);
                    root.transform.localPosition = new Vector3(0f, 1.6f, 0f);
                });

#if ENABLE_INPUT_SYSTEM
            var pi = go.GetComponent<UnityEngine.InputSystem.PlayerInput>();
            if (pi == null || pi.actions == null)
                add(Severity.Error, $"{go.name} has no input actions, it will not respond to keys or a gamepad.", go, "Assign", () =>
                {
                    var p = CharacterSetupProfileEditor.FindOrCreateProfile();
                    if (pi == null) pi = Undo.AddComponent<UnityEngine.InputSystem.PlayerInput>(go);
                    Undo.RecordObject(pi, "Assign Input");
                    pi.actions = p.InputActions;
                    pi.defaultActionMap = p.DefaultActionMap;
                    pi.notificationBehavior = UnityEngine.InputSystem.PlayerNotifications.SendMessages;
                });
#else
            add(Severity.Error, "The Input System package is not active (Project Settings > Player > Active Input Handling).", null, null, null);
#endif
            if (tpc.GroundLayers.value == 0)
                add(Severity.Error, $"{go.name}'s Ground Layers are empty: it will fall through everything.", go, "Default", () =>
                {
                    Undo.RecordObject(tpc, "Ground Layers");
                    tpc.GroundLayers = LayerMask.GetMask("Default");
                });

            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            void Needs<T>(string state, string feature) where T : Component
            {
                if (controller == null || AnimatorBuildUtil.State(controller, state) == null || go.GetComponent<T>() != null) return;
                add(Severity.Warning, $"The Animator has {feature} but {go.name} has no {typeof(T).Name}; rebuild the Player to enable it.", go, null, null);
            }
            Needs<ClimbController>("Hang Idle", "ledges and ladders");
            Needs<SwimController>("Swim", "swimming");
            Needs<CharacterActions>("Interact", "actions");
            Needs<CharacterMountController>("Mounted", "mounts");
            if (go.GetComponent<CharacterDimensions>() == null)
                add(Severity.Warning, $"{go.name} has no CharacterDimensions: distances will not adapt to the character's size.", go, "Add",
                    () => Undo.AddComponent<CharacterDimensions>(go));
            if (go.GetComponent<CharacterControllerRig>() == null)
                add(Severity.Info, $"{go.name} has no Character Controller hub component.", go, "Add", () =>
                {
                    var rig = Undo.AddComponent<CharacterControllerRig>(go);
                    CharacterControllerRig.HideParts(rig, true);
                });
        }

        private static void CheckScene(ThirdPersonController player, Action<Severity, string, Object, string, Action> add)
        {
            // Camera.
            var cam = Camera.main;
            if (cam == null)
                add(Severity.Error, "No camera tagged MainCamera.", null, "Create", () => CharacterSetupBuilder.WireCamera(player.gameObject, CharacterSetupProfileEditor.FindOrCreateProfile()));
            else
            {
                var tp = cam.GetComponent<ThirdPersonCamera>();
                if (tp == null || tp.Target == null || !tp.Target.IsChildOf(player.transform))
                    add(Severity.Error, "The main camera does not follow the Player.", cam.gameObject, "Wire",
                        () => CharacterSetupBuilder.WireCamera(player.gameObject, CharacterSetupProfileEditor.FindOrCreateProfile()));
                if (player.CameraTransform == null)
                    add(Severity.Warning, "The Player moves relative to no camera (Camera Transform is empty).", player.gameObject, "Wire",
                        () => CharacterSetupBuilder.WireCamera(player.gameObject, CharacterSetupProfileEditor.FindOrCreateProfile()));
                if (cam.GetComponents<MonoBehaviour>().Any(m => m != null && m.GetType().Name.StartsWith("Cinemachine")))
                    add(Severity.Warning, "The main camera has a Cinemachine Brain; it will fight the ThirdPersonCamera.", cam.gameObject, null, null);
            }

            // Ground under the player.
            var origin = player.transform.position + Vector3.up * 0.5f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 50f, player.GroundLayers, QueryTriggerInteraction.Ignore)
                .Where(h => !h.transform.IsChildOf(player.transform)).ToArray();
            if (hits.Length == 0)
            {
                bool anyBelow = Physics.RaycastAll(origin, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore)
                    .Any(h => !h.transform.IsChildOf(player.transform));
                add(Severity.Error, anyBelow
                        ? "There is ground under the Player, but not on its Ground Layers. Add that layer to Ground Layers."
                        : "No collider under the Player: it will fall. Give the floor a collider.",
                    player.gameObject, null, null);
            }
            else if (hits.Min(h => h.distance) > 1.5f)
                add(Severity.Info, $"The Player starts {hits.Min(h => h.distance) - 0.5f:F1} m above the ground.", player.gameObject, null, null);

            // Ladders and water named so but not marked up.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("ladder") && t.GetComponentInParent<Ladder>() == null && t.GetComponentInChildren<Ladder>() == null && t.GetComponent<Renderer>() != null)
                    add(Severity.Warning, $"'{t.name}' looks like a ladder but has no Ladder component, so it cannot be climbed.", t.gameObject, "Add Ladder", () =>
                    {
                        var ladder = Undo.AddComponent<Ladder>(t.gameObject);
                        var r = t.GetComponent<Renderer>();
                        if (r != null) ladder.Height = r.bounds.size.y;
                    });
                if ((n.Contains("water") || n.Contains("pool") || n.Contains("lake")) && t.GetComponent<WaterVolume>() == null &&
                    t.GetComponentInParent<WaterVolume>() == null && t.GetComponent<Renderer>() != null && !n.Contains("effect"))
                    add(Severity.Warning, $"'{t.name}' looks like water but has no WaterVolume, so the character will walk on or through it.", t.gameObject, "Add Water", () =>
                    {
                        var r = t.GetComponent<Renderer>();
                        var holder = new GameObject(t.name + " Volume");
                        Undo.RegisterCreatedObjectUndo(holder, "Add Water");
                        var b = r.bounds;
                        holder.transform.position = new Vector3(b.center.x, b.max.y - 1.5f, b.center.z);
                        var box = holder.AddComponent<BoxCollider>();
                        box.isTrigger = true;
                        box.size = new Vector3(b.size.x, 3f, b.size.z);
                        holder.AddComponent<WaterVolume>();
                    });
            }
            foreach (var w in Object.FindObjectsByType<WaterVolume>(FindObjectsInactive.Exclude))
                if (w.Bounds.size.y < 1.2f)
                    add(Severity.Info, $"Water '{w.name}' is {w.Bounds.size.y:F1} m deep: wading only, too shallow to swim.", w.gameObject, null, null);

            // Mounts.
            foreach (var m in Object.FindObjectsByType<CharacterMount>(FindObjectsInactive.Exclude))
            {
                if (m.Seat == null)
                    add(Severity.Info, $"Mount '{m.name}' has no Seat; the rider's hips go to its pivot.", m.gameObject, null, null);
                if (m.GetComponentInParent<Rigidbody>() is Rigidbody rb && !rb.isKinematic && m.GetComponentsInChildren<Collider>().Length == 0)
                    add(Severity.Warning, $"Mount '{m.name}' has a Rigidbody but no colliders.", m.gameObject, null, null);
            }

            // Moving non-convex mesh colliders break physics.
            foreach (var mc in Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Exclude))
            {
                var rb = mc.attachedRigidbody;
                if (rb != null && !rb.isKinematic && !mc.convex)
                    add(Severity.Warning, $"'{mc.name}' is a non-convex Mesh Collider on a moving Rigidbody; Unity does not support that.", mc.gameObject,
                        "Make Convex", () => { Undo.RecordObject(mc, "Convex"); mc.convex = true; });
            }
        }
    }
}
