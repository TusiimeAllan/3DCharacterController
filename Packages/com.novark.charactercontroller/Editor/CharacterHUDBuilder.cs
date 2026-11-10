using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Adds the runtime HUD (character picker + controls legend) to the open scene as a normal,
    /// editable uGUI hierarchy, wired to the CharacterHUD component.
    /// </summary>
    public static class CharacterHUDBuilder
    {
        private const string Tag = "[CharacterHUDBuilder]";
        private const string RootName = "HUD";

        private static readonly Color PanelColor = new Color(0.04f, 0.06f, 0.07f, 0.72f);
        private static readonly Color Accent = new Color(0.55f, 0.86f, 1f, 1f);
        private static readonly Color Text = new Color(0.9f, 0.95f, 0.97f, 1f);
        private static readonly Color Muted = new Color(0.9f, 0.95f, 0.97f, 0.6f);
        private static readonly Color ButtonBg = new Color(1f, 1f, 1f, 0.08f);

        private static Sprite _rounded;

        [MenuItem("Tools/Character Controller/Add Character HUD")]
        public static void AddToOpenScene() => Build(EditorSceneManager.GetActiveScene());

        public static GameObject Build(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name != RootName) continue;
                Debug.Log($"{Tag} '{RootName}' already exists in '{scene.name}', leaving it as is.", go);
                return go;
            }

            if (TMP_Settings.defaultFontAsset == null)
                Debug.LogWarning($"{Tag} TMP Essentials are missing, text will not render until they are imported.");

            _rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            var previousActive = EditorSceneManager.GetActiveScene();
            EditorSceneManager.SetActiveScene(scene);

            EnsureEventSystem(scene);

            // Canvas
            var root = new GameObject(RootName, typeof(RectTransform), typeof(Canvas),
                typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var hud = root.AddComponent<CharacterHUD>();

            BuildCharacterPanel(root.transform, hud);
            BuildLegend(root.transform, hud);

            // Sources
            hud.Switcher = FindInScene<CharacterSwitcher>(scene);
            hud.Inputs = FindInScene<PlayerInputs>(scene);
#if ENABLE_INPUT_SYSTEM
            hud.PlayerInput = FindInScene<UnityEngine.InputSystem.PlayerInput>(scene);
#endif
            if (hud.Switcher != null)
            {
                Undo.RecordObject(hud.Switcher, "Hide switcher hint");
                hud.Switcher.ShowHint = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(hud.Switcher);
            }

            Undo.RegisterCreatedObjectUndo(root, "Add Character HUD");
            EditorSceneManager.MarkSceneDirty(scene);
            if (previousActive.IsValid() && previousActive.isLoaded) EditorSceneManager.SetActiveScene(previousActive);
            Debug.Log($"{Tag} HUD added to '{scene.name}'.", root);
            return root;
        }

        // ---------------------------------------------------------------- Character panel

        private static void BuildCharacterPanel(Transform parent, CharacterHUD hud)
        {
            var panel = Panel("CharacterPanel", parent);
            var rt = (RectTransform)panel.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, -24f);

            var vlg = panel.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 16, 14, 16);
            vlg.spacing = 10f;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fit = panel.AddComponent<UnityEngine.UI.ContentSizeFitter>();
            fit.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            fit.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;

            var title = Label("Title", panel.transform, "CHARACTER", 14f, Accent, FontStyles.Bold);
            title.characterSpacing = 8f;

            // < Name >
            var row = new GameObject("Selector", typeof(RectTransform));
            row.transform.SetParent(panel.transform, false);
            var hlg = row.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;

            hud.PrevButton = Button("PrevButton", row.transform, "<", 22f, 44f, 44f);
            hud.NameLabel = Label("NameLabel", row.transform, "-", 26f, Text, FontStyles.Bold);
            hud.NameLabel.alignment = TextAlignmentOptions.Center;
            var nameLe = hud.NameLabel.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            nameLe.minWidth = nameLe.preferredWidth = 220f;
            nameLe.flexibleWidth = 1f;
            hud.NextButton = Button("NextButton", row.transform, ">", 22f, 44f, 44f);

            // One button per character, cloned at runtime from the template.
            var list = new GameObject("CharacterButtons", typeof(RectTransform));
            list.transform.SetParent(panel.transform, false);
            var listVlg = list.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            listVlg.spacing = 6f;
            listVlg.childControlWidth = listVlg.childControlHeight = true;
            listVlg.childForceExpandWidth = true;
            listVlg.childForceExpandHeight = false;
            hud.CharacterButtonContainer = (RectTransform)list.transform;

            var template = Button("CharacterButtonTemplate", list.transform, "Character", 17f, 0f, 38f);
            var label = template.GetComponentInChildren<TMP_Text>();
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.margin = new Vector4(14f, 0f, 14f, 0f);
            template.gameObject.SetActive(false);
            hud.CharacterButtonTemplate = template;

            Label("Hint", panel.transform, "Press Esc to use the mouse", 13f, Muted, FontStyles.Italic);
        }

        // ---------------------------------------------------------------- Legend

        private static void BuildLegend(Transform parent, CharacterHUD hud)
        {
            var bar = Panel("LegendBar", parent);
            var img = bar.GetComponent<UnityEngine.UI.Image>();
            img.sprite = null; // full-width strip, square edges
            var rt = (RectTransform)bar.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(0f, 60f);
            rt.anchoredPosition = Vector2.zero;

            var hlg = bar.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(24, 24, 12, 12);
            hlg.spacing = 32f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            hud.LegendContainer = rt;

            // Template: [KEY] Label
            var entry = new GameObject("LegendEntryTemplate", typeof(RectTransform));
            entry.transform.SetParent(bar.transform, false);
            var ehlg = entry.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            ehlg.spacing = 10f;
            ehlg.childAlignment = TextAnchor.MiddleLeft;
            ehlg.childControlWidth = ehlg.childControlHeight = true;
            ehlg.childForceExpandWidth = ehlg.childForceExpandHeight = false;

            var keyBg = new GameObject("KeyBg", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            keyBg.transform.SetParent(entry.transform, false);
            var keyImg = keyBg.GetComponent<UnityEngine.UI.Image>();
            keyImg.sprite = _rounded;
            keyImg.type = UnityEngine.UI.Image.Type.Sliced;
            keyImg.color = new Color(1f, 1f, 1f, 0.12f);
            keyImg.raycastTarget = false;
            var kh = keyBg.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            kh.padding = new RectOffset(10, 10, 4, 4);
            kh.childControlWidth = kh.childControlHeight = true;
            kh.childForceExpandWidth = kh.childForceExpandHeight = false;
            var keyLe = keyBg.AddComponent<UnityEngine.UI.LayoutElement>();
            keyLe.minWidth = 34f;
            keyLe.minHeight = 30f;
            var key = Label("Key", keyBg.transform, "KEY", 15f, Text, FontStyles.Bold);
            key.alignment = TextAlignmentOptions.Center;

            Label("Label", entry.transform, "Action", 16f, Muted, FontStyles.Normal);
            entry.SetActive(false);
            hud.LegendEntryTemplate = entry;
        }

        // ---------------------------------------------------------------- Helpers

        private static GameObject Panel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<UnityEngine.UI.Image>();
            img.sprite = _rounded;
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.color = PanelColor;
            img.raycastTarget = true; // clicks on the panel never reach the game
            return go;
        }

        private static TextMeshProUGUI Label(string name, Transform parent, string text, float size, Color color,
            FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            return t;
        }

        private static UnityEngine.UI.Button Button(string name, Transform parent, string text, float fontSize,
            float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image),
                typeof(UnityEngine.UI.Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<UnityEngine.UI.Image>();
            img.sprite = _rounded;
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.color = ButtonBg;

            var button = go.GetComponent<UnityEngine.UI.Button>();
            button.targetGraphic = img;
            button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.selectedColor = Color.white;
            colors.colorMultiplier = 1.5f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
            if (width > 0f) le.minWidth = le.preferredWidth = width;
            le.minHeight = le.preferredHeight = height;

            var label = Label("Label", go.transform, text, fontSize, Text, FontStyles.Bold);
            label.alignment = TextAlignmentOptions.Center;
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            return button;
        }

        private static void EnsureEventSystem(UnityEngine.SceneManagement.Scene scene)
        {
            if (FindInScene<EventSystem>(scene) != null) return;
#if ENABLE_INPUT_SYSTEM
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
#else
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
#endif
            Undo.RegisterCreatedObjectUndo(es, "Add EventSystem");
        }

        private static T FindInScene<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            foreach (var go in scene.GetRootGameObjects())
            {
                var c = go.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }
    }
}
