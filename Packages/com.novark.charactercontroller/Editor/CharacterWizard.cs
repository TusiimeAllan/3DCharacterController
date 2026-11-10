using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// One window to set up the player: pick your own characters (rigged or not) or the bundled X Bot, pick
    /// the features, press Set Up. Characters that are not Humanoid yet are converted or auto-rigged; the
    /// Animator Controller and Player prefab are built from the kit profile; the Player can go straight into
    /// the open scene. With your own characters set, X Bot is not part of the Player.
    /// </summary>
    public class CharacterWizard : EditorWindow
    {
        private class Row
        {
            public GameObject Model;
            public string Name;
            public CharacterPreparer.Facing Facing;
            public bool UseKitSkeleton;
            public CharacterPreparer.Report? Report;
            public GameObject InspectedFor;
        }

        private CharacterSetupProfile _profile;
        private SerializedObject _so;
        private bool _useOwn = true;
        private readonly List<Row> _rows = new List<Row>();
        private int _default;
        private float _rigHeight = 1.8f;
        private bool _keepRigSize = true;
        private bool _addToScene = true;
        private Vector2 _scroll;

        [MenuItem("Tools/Character Controller/Character Wizard", priority = 0)]
        public static void OpenFromMenu() => Open(null);

        public static void Open(CharacterSetupProfile profile)
        {
            var w = GetWindow<CharacterWizard>(true, "Character Wizard");
            w.minSize = new Vector2(520f, 560f);
            w.Load(profile != null ? profile : CharacterSetupProfileEditor.FindOrCreateProfile());
            w.Show();
        }

        private void Load(CharacterSetupProfile profile)
        {
            _profile = profile;
            _so = new SerializedObject(profile);
            _rows.Clear();
            foreach (var c in profile.Characters.Where(c => c != null && c.Model != null))
                _rows.Add(new Row { Model = c.Model, Name = c.Name });
            _useOwn = true; // your own characters are the recommended setup
            _default = Mathf.Clamp(profile.DefaultCharacter, 0, Mathf.Max(0, _rows.Count - 1));
        }

        private void OnGUI()
        {
            if (_profile == null) { Load(CharacterSetupProfileEditor.FindOrCreateProfile()); }
            _so.Update();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            var newProfile = (CharacterSetupProfile)EditorGUILayout.ObjectField("Kit Profile", _profile, typeof(CharacterSetupProfile), false);
            if (newProfile != null && newProfile != _profile) Load(newProfile);

            // ------------------------------------------------ characters
            Header("1. Characters");
            int mode = GUILayout.Toolbar(_useOwn ? 0 : 1, new[] { "My characters (recommended)", "Bundled X Bot" });
            _useOwn = mode == 0;
            if (_useOwn)
            {
                EditorGUILayout.HelpBox("Add one or more characters from your project or scene: Humanoid, rigged (any skeleton) " +
                                        "or not rigged at all. X Bot will not be part of your Player.", MessageType.None);
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (DrawRow(_rows[i], i)) { _rows.RemoveAt(i); i--; }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("+ Add Character")) _rows.Add(new Row());
                    GUI.enabled = Selection.gameObjects.Length > 0;
                    if (GUILayout.Button("+ Add Selected"))
                        foreach (var go in Selection.gameObjects)
                            if (_rows.All(r => r.Model != go)) _rows.Add(new Row { Model = go, Name = go.name });
                    GUI.enabled = true;
                }
                if (_rows.Count > 1)
                    _default = EditorGUILayout.Popup("Starts As", Mathf.Clamp(_default, 0, _rows.Count - 1),
                        _rows.Select((r, i) => $"{i + 1}. {(string.IsNullOrEmpty(r.Name) ? "(empty)" : r.Name)}").ToArray());
                if (_rows.Any(r => r.Report?.Status == CharacterPreparer.Status.AutoRig || r.UseKitSkeleton))
                {
                    _keepRigSize = EditorGUILayout.Toggle(new GUIContent("Keep Model Size", "Rig at the model's own height (unit mix-ups are corrected)"), _keepRigSize);
                    if (!_keepRigSize) _rigHeight = EditorGUILayout.FloatField(new GUIContent("Rigged Height (m)", "Height to rig unrigged models at"), _rigHeight);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("The Player uses the bundled X Bot. Good for trying the kit out; switch to your own " +
                                        "characters for your game.", MessageType.Info);
            }

            // ------------------------------------------------ features
            Header("2. Features");
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    Toggle("EnableMotionMatching", "Motion matching");
                    Toggle("EnableCrouch", "Crouch");
                    Toggle("EnableStrafe", "Strafe");
                    Toggle("EnableParkour", "Parkour (vault, climb, drop, slide)");
                    Toggle("EnableClimbing", "Ledges and ladders");
                    Toggle("EnableSwimming", "Swimming");
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    Toggle("EnableSlopeSlide", "Steep slope slide");
                    Toggle("EnableActions", "Actions and emotes");
                    Toggle("EnableMounts", "Mounts (bikes, cars, seats)");
                    Toggle("EnableGrounding", "Foot grounding and look-at");
                    Toggle("EnableBodyContact", "Hands and body stay out of walls");
                }
            }
            EditorGUILayout.PropertyField(_so.FindProperty("GroundLayers"));

            // ------------------------------------------------ scene
            Header("3. Scene");
            _addToScene = EditorGUILayout.Toggle(new GUIContent("Add Player To Open Scene", "Replaces any Player already in it"), _addToScene);
            _so.ApplyModifiedProperties();

            EditorGUILayout.Space(14);
            bool ready = !_useOwn || (_rows.Count > 0 && _rows.All(r => r.Model != null && r.Report?.Status != CharacterPreparer.Status.Unsupported));
            GUI.enabled = ready;
            if (GUILayout.Button("Set Up Character", GUILayout.Height(34))) Run();
            GUI.enabled = true;
            if (!ready && _useOwn) EditorGUILayout.HelpBox("Add at least one usable character.", MessageType.Warning);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Edit Clips In Profile")) { Selection.activeObject = _profile; EditorGUIUtility.PingObject(_profile); }
                if (GUILayout.Button("Validate Scene")) SceneValidator.ValidateAndReport();
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Draws a character row; returns true when it should be removed.</summary>
        private bool DrawRow(Row row, int index)
        {
            bool remove = false;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var model = (GameObject)EditorGUILayout.ObjectField(row.Model, typeof(GameObject), true);
                    if (model != row.Model)
                    {
                        row.Model = model;
                        if (model != null && string.IsNullOrEmpty(row.Name)) row.Name = model.name;
                    }
                    row.Name = EditorGUILayout.TextField(row.Name, GUILayout.Width(150));
                    if (GUILayout.Button("X", GUILayout.Width(22))) remove = true;
                }
                if (row.Model != null)
                {
                    if (row.InspectedFor != row.Model)
                    {
                        row.Report = CharacterPreparer.Inspect(SourceAsset(row.Model));
                        row.InspectedFor = row.Model;
                    }
                    var r = row.Report.Value;
                    var type = r.Status == CharacterPreparer.Status.Unsupported ? MessageType.Error
                        : r.Status == CharacterPreparer.Status.Ready ? MessageType.None : MessageType.Info;
                    EditorGUILayout.HelpBox(r.Message, type);
                    if (r.Status == CharacterPreparer.Status.Ready || r.Status == CharacterPreparer.Status.ConvertToHumanoid)
                        row.UseKitSkeleton = EditorGUILayout.ToggleLeft(new GUIContent("Replace its bones with the kit skeleton",
                            "Ignore the model's own rig and skin it to the kit's skeleton instead. Use it when the model's rig is broken or unusual; a good rig of its own usually looks better."),
                            row.UseKitSkeleton);
                    if (r.Status == CharacterPreparer.Status.AutoRig || row.UseKitSkeleton)
                        row.Facing = (CharacterPreparer.Facing)EditorGUILayout.EnumPopup(new GUIContent("Model Faces", "Which way the model looks in its own space"), row.Facing);
                }
            }
            return remove;
        }

        private void Run()
        {
            Kit.UseUndo = false;
            var prepared = new List<CharacterSetupProfile.CharacterModel>();
            if (_useOwn)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    EditorUtility.DisplayProgressBar("Character Wizard", $"Preparing {row.Name}", (float)i / _rows.Count);
                    var model = CharacterPreparer.Prepare(SourceAsset(row.Model), row.Name, _profile, row.Facing, _keepRigSize ? 0f : _rigHeight, row.UseKitSkeleton);
                    if (model == null)
                    {
                        EditorUtility.ClearProgressBar();
                        EditorUtility.DisplayDialog("Character Wizard", $"{row.Name} could not be made Humanoid. See the Console.", "OK");
                        return;
                    }
                    row.InspectedFor = null;
                    prepared.Add(new CharacterSetupProfile.CharacterModel { Name = row.Name, Model = model });
                }
            }

            try
            {
                EditorUtility.DisplayProgressBar("Character Wizard", "Building the Animator Controller", 0.5f);
                Undo.RecordObject(_profile, "Character Wizard");
                _profile.Characters = prepared; // empty = bundled X Bot
                _profile.DefaultCharacter = _default;
                CharacterSetupBuilder.ApplyKitDefaults(_profile, overwrite: false);
                EditorUtility.SetDirty(_profile);
                AssetDatabase.SaveAssets();

                CharacterSetupBuilder.BuildAnimator(_profile);
                EditorUtility.DisplayProgressBar("Character Wizard", "Building the Player", 0.8f);
                var prefab = CharacterSetupBuilder.BuildPlayerPrefab(_profile);
                if (prefab == null) return;
                if (_addToScene) CharacterSetupBuilder.AddPlayerToOpenScene(_profile);
                else { Selection.activeObject = prefab; EditorGUIUtility.PingObject(prefab); }
            }
            finally { EditorUtility.ClearProgressBar(); }

            SceneValidator.ValidateAndReport(quietWhenClean: true);
            ShowNotification(new GUIContent("Player ready"));
        }

        /// <summary>The asset behind a scene object (its prefab or model), or the object itself.</summary>
        public static GameObject SourceAsset(GameObject go)
        {
            if (go == null || EditorUtility.IsPersistent(go)) return go;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            return source != null && PrefabUtility.GetOutermostPrefabInstanceRoot(go) == go ? source : go;
        }

        private void Toggle(string property, string label)
        {
            var p = _so.FindProperty(property);
            p.boolValue = EditorGUILayout.ToggleLeft(label, p.boolValue);
        }

        private static void Header(string text)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
        }

        // ================================================================ Make Playable

        [MenuItem("GameObject/Character Controller/Make Playable", false, 0)]
        private static void MakePlayableMenu() => MakePlayable(Selection.activeGameObject);

        [MenuItem("GameObject/Character Controller/Make Playable", true)]
        private static bool MakePlayableValidate() =>
            !EditorApplication.isPlaying && Selection.activeGameObject != null && !EditorUtility.IsPersistent(Selection.activeGameObject) &&
            Selection.activeGameObject.GetComponentInParent<ThirdPersonController>() == null;

        /// <summary>
        /// Turns a character standing in the scene into the player, where it stands: makes it Humanoid if
        /// needed, puts it under a new Player with every component the profile asks for, and wires the camera.
        /// </summary>
        public static GameObject MakePlayable(GameObject target)
        {
            if (target == null) return null;
            var p = CharacterSetupProfileEditor.FindOrCreateProfile();
            CharacterSetupBuilder.ApplyKitDefaults(p, overwrite: false);

            var source = SourceAsset(target);
            var report = CharacterPreparer.Inspect(source);
            if (report.Status == CharacterPreparer.Status.Unsupported)
            {
                EditorUtility.DisplayDialog("Make Playable", report.Message, "OK");
                return null;
            }
            var model = CharacterPreparer.Prepare(source, target.name, p, CharacterPreparer.Facing.PositiveZ, 0f);
            if (model == null) return null;

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(p.ControllerPath) ?? CharacterSetupBuilder.BuildAnimator(p);

            Kit.UseUndo = true;
            try
            {
                var root = new GameObject(target.name + " (Player)");
                Undo.RegisterCreatedObjectUndo(root, "Make Playable");
                root.transform.SetPositionAndRotation(target.transform.position, Quaternion.Euler(0f, target.transform.eulerAngles.y, 0f));
                SceneManagerMove(root, target);

                var cc = root.AddComponent<CharacterController>();
                cc.height = p.Height;
                cc.center = new Vector3(0f, p.Height * 0.5f, 0f);
                cc.radius = p.Radius;
                cc.slopeLimit = p.SlopeLimit;
                cc.stepOffset = p.StepOffset;
                var animator = root.AddComponent<Animator>();

                var switcher = root.AddComponent<CharacterSwitcher>();
                var entry = CharacterSetupBuilder.CreateCharacterChild(root.transform, model, target.name, true);
                switcher.Characters.Add(entry);
                var swSo = new SerializedObject(switcher);
                swSo.FindProperty("_animator").objectReferenceValue = animator;
                swSo.ApplyModifiedPropertiesWithoutUndo();
                animator.avatar = entry.Avatar;

                CharacterSetupBuilder.ConfigurePlayer(root, p, controller);
                Undo.DestroyObjectImmediate(target);
                CharacterSetupBuilder.WireCamera(root, p);
                Selection.activeGameObject = root;
                Debug.Log($"[CharacterKit] {root.name} is playable. Press Play.", root);
                return root;
            }
            finally { Kit.UseUndo = false; }
        }

        private static void SceneManagerMove(GameObject go, GameObject like)
        {
            if (go.scene != like.scene) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, like.scene);
        }
    }
}
