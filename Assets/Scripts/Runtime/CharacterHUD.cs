using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Runtime HUD: a character picker (prev / next and one button per character) and a
    /// controls legend along the bottom. Legend keys come from the PlayerInput actions, follow
    /// the active control scheme (keyboard or gamepad) and light up while their input is held.
    /// Build the hierarchy with Tools > Character Controller > Add Character HUD.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterHUD : MonoBehaviour
    {
        [System.Serializable]
        public class LegendAction
        {
            [Tooltip("Action name in the PlayerInput actions asset")]
            public string Action;
            public string Label;
            [Tooltip("Optional override for the key text on keyboard & mouse")]
            public string KeyboardKeyOverride;
        }

        [Header("Sources")]
        public CharacterSwitcher Switcher;
#if ENABLE_INPUT_SYSTEM
        public PlayerInput PlayerInput;
#endif
        public PlayerInputs Inputs;
        public string GamepadScheme = "Gamepad";

        [Header("Character Panel")]
        public TMP_Text NameLabel;
        public UnityEngine.UI.Button PrevButton;
        public UnityEngine.UI.Button NextButton;
        public RectTransform CharacterButtonContainer;
        [Tooltip("Inactive template cloned once per character")]
        public UnityEngine.UI.Button CharacterButtonTemplate;

        [Header("Legend")]
        public RectTransform LegendContainer;
        [Tooltip("Inactive template with children 'KeyBg/Key' (TMP) and 'Label' (TMP)")]
        public GameObject LegendEntryTemplate;
        public List<LegendAction> LegendActions = new List<LegendAction>
        {
            new LegendAction { Action = "Move", Label = "Move" },
            new LegendAction { Action = "Look", Label = "Look", KeyboardKeyOverride = "Mouse" },
            new LegendAction { Action = "Sprint", Label = "Sprint" },
            new LegendAction { Action = "Jump", Label = "Jump" },
            new LegendAction { Action = "Crouch", Label = "Crouch (toggle)" },
        };

        [Header("Colors")]
        public Color AccentColor = new Color(0.55f, 0.86f, 1f, 1f);
        public Color IdleKeyColor = new Color(1f, 1f, 1f, 0.12f);
        public Color ButtonColor = new Color(1f, 1f, 1f, 0.08f);
        public Color ActiveTextColor = new Color(0.04f, 0.07f, 0.09f, 1f);
        public Color TextColor = new Color(0.9f, 0.95f, 0.97f, 1f);

        private struct LegendEntry
        {
            public UnityEngine.UI.Image KeyBg;
            public TMP_Text Key;
#if ENABLE_INPUT_SYSTEM
            public InputAction Action;
#endif
            public System.Func<bool> IsOn; // toggles (crouch, motion matching) stay lit while on
        }

        private readonly List<UnityEngine.UI.Button> _characterButtons = new List<UnityEngine.UI.Button>();
        private readonly List<LegendEntry> _legend = new List<LegendEntry>();
        private string _builtForScheme;

        private void Awake()
        {
            if (Switcher == null) Switcher = FindAnyObjectByType<CharacterSwitcher>();
            if (Inputs == null) Inputs = FindAnyObjectByType<PlayerInputs>();
#if ENABLE_INPUT_SYSTEM
            if (PlayerInput == null) PlayerInput = FindAnyObjectByType<PlayerInput>();
#endif
            if (PrevButton != null) PrevButton.onClick.AddListener(() => Click(() => Switcher.Previous()));
            if (NextButton != null) NextButton.onClick.AddListener(() => Click(() => Switcher.Next()));
        }

        private void OnEnable()
        {
            if (Switcher != null)
            {
                Switcher.CharacterChanged += OnCharacterChanged;
                // The on-screen buttons replace the switcher's debug label.
                Switcher.ShowHint = false;
            }
            BuildCharacterButtons();
            RefreshCharacter();
            BuildLegend();
        }

        // Other components finish setting up in their Awake (e.g. the LaFAN1 database), which can run
        // after this OnEnable; rebuild once everything is awake.
        private void Start() => BuildLegend();

        private void OnDisable()
        {
            if (Switcher != null) Switcher.CharacterChanged -= OnCharacterChanged;
        }

        private void Update()
        {
            if (CurrentScheme() != _builtForScheme) BuildLegend();
            UpdateLegendHighlights();
        }

        // ---------------------------------------------------------------- Character panel

        private void Click(System.Action action)
        {
            if (Switcher != null) action();
            // Clear UI focus so Space / Enter (jump) never re-presses the last clicked button.
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }

        private void OnCharacterChanged(int index) => RefreshCharacter();

        private void BuildCharacterButtons()
        {
            foreach (var b in _characterButtons) if (b != null) Destroy(b.gameObject);
            _characterButtons.Clear();
            if (Switcher == null || CharacterButtonTemplate == null || CharacterButtonContainer == null) return;

            CharacterButtonTemplate.gameObject.SetActive(false);
            for (int i = 0; i < Switcher.Characters.Count; i++)
            {
                int index = i;
                var entry = Switcher.Characters[i];
                var button = Instantiate(CharacterButtonTemplate, CharacterButtonContainer);
                button.gameObject.name = $"Character_{i}";
                button.gameObject.SetActive(true);
                var label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.text = $"{i + 1}  {DisplayName(entry, i)}";
                button.onClick.AddListener(() => Click(() => Switcher.SwitchTo(index)));
                _characterButtons.Add(button);
            }
        }

        private void RefreshCharacter()
        {
            if (Switcher == null) return;
            int active = Switcher.ActiveIndex;
            if (NameLabel != null) NameLabel.text = DisplayName(Switcher.ActiveCharacter, active);

            for (int i = 0; i < _characterButtons.Count; i++)
            {
                bool on = i == active;
                var img = _characterButtons[i].targetGraphic as UnityEngine.UI.Image;
                if (img != null) img.color = on ? AccentColor : ButtonColor;
                var label = _characterButtons[i].GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.color = on ? ActiveTextColor : TextColor;
            }
        }

        private static string DisplayName(CharacterSwitcher.CharacterEntry entry, int index)
        {
            if (entry == null) return "-";
            if (!string.IsNullOrWhiteSpace(entry.Name)) return entry.Name;
            return entry.Root != null ? entry.Root.name : $"Character {index + 1}";
        }

        // ---------------------------------------------------------------- Legend

        private string CurrentScheme()
        {
#if ENABLE_INPUT_SYSTEM
            return PlayerInput != null ? PlayerInput.currentControlScheme ?? "" : "";
#else
            return "";
#endif
        }

        private bool IsGamepad => CurrentScheme() == GamepadScheme;

        private void BuildLegend()
        {
            _builtForScheme = CurrentScheme();
            if (LegendContainer == null || LegendEntryTemplate == null) return;

            EnsureLegendRows();
            LegendEntryTemplate.SetActive(false);
            var stale = new List<GameObject>();
            foreach (Transform child in LegendContainer)
                if (child.gameObject != LegendEntryTemplate) stale.Add(child.gameObject);
            foreach (var go in stale) DestroyImmediate(go); // now, so the row layout never sees old entries
            _legend.Clear();

            bool pad = IsGamepad;
#if ENABLE_INPUT_SYSTEM
            var actions = PlayerInput != null ? PlayerInput.actions : null;
            foreach (var la in LegendActions)
            {
                var action = actions?.FindAction(la.Action);
                string key = !pad && !string.IsNullOrEmpty(la.KeyboardKeyOverride)
                    ? la.KeyboardKeyOverride
                    : BindingText(action, pad);
                if (string.IsNullOrEmpty(key)) continue;
                var e = AddLegendEntry(key, la.Label);
                e.Action = action;
                if (la.Action == "Crouch") e.IsOn = () => Inputs != null && Inputs.crouch;
                _legend.Add(e);
            }
            var strafeAction = actions?.FindAction("Strafe");
            var tpc = FindAnyObjectByType<ThirdPersonController>();
            if (strafeAction != null && tpc != null && tpc.AllowStrafe)
            {
                string key = BindingText(strafeAction, pad);
                if (!string.IsNullOrEmpty(key))
                {
                    var e = AddLegendEntry(key, pad ? "Strafe (toggle)" : "Strafe (hold)");
                    e.IsOn = () => tpc.IsStrafing;
                    _legend.Add(e);
                }
            }
#endif
            int count = Switcher != null ? Mathf.Min(Switcher.Characters.Count, 9) : 0;
            if (count > 1) _legend.Add(AddLegendEntry(pad ? "LB / RB" : $"Tab / 1-{count}", "Change character"));
            if (!pad) _legend.Add(AddLegendEntry("Esc", "Free cursor (click to lock)"));
            var mm = FindAnyObjectByType<MotionMatchingLocomotion>();
            if (mm != null && !pad)
            {
                var e = AddLegendEntry(mm.ToggleKey.ToString(), "Motion matching");
                e.IsOn = () => mm.UseMotionMatching;
                _legend.Add(e);
                if (mm.HasAlternateAnimData)
                {
                    var db = AddLegendEntry(mm.AnimDataToggleKey.ToString(), "LaFAN1 database");
                    db.IsOn = () => mm.AnimDataIndex == 1;
                    _legend.Add(db);
                }
            }
            var grounding = FindAnyObjectByType<HumanoidGrounding>();
            if (grounding != null && !pad)
            {
                var e = AddLegendEntry(grounding.ToggleKey.ToString(), "Grounding + look-at");
                e.IsOn = () => grounding.Enabled;
                _legend.Add(e);
            }
            var parkour = FindAnyObjectByType<ParkourController>();
            if (parkour != null && !pad)
            {
                var e = AddLegendEntry(parkour.ToggleKey.ToString(), "Parkour: run at obstacles, sprint + crouch slides");
                e.IsOn = () => parkour.EnableParkour;
                _legend.Add(e);
                var climbing = parkour.GetComponent<ClimbController>();
                if (climbing != null)
                {
                    var c = AddLegendEntry("Ledge/Ladder", "Jump to grab, W/S climb, A/D shimmy");
                    c.IsOn = () => climbing.IsClimbing;
                    _legend.Add(c);
                }
                var swimming = parkour.GetComponent<SwimController>();
                if (swimming != null)
                {
                    var w = AddLegendEntry("Water", "Swim, Shift faster, Jump at edge to climb out");
                    w.IsOn = () => swimming.IsSwimming;
                    _legend.Add(w);
                }
            }

            var acts = FindAnyObjectByType<CharacterActions>();
            if (acts != null)
            {
#if ENABLE_INPUT_SYSTEM
                foreach (var (name, label) in new[] { ("Interact", "Interact / stand up"), ("Attack", "Punch (combo)") })
                {
                    var action = actions?.FindAction(name);
                    string key = BindingText(action, pad);
                    if (string.IsNullOrEmpty(key)) continue;
                    var e = AddLegendEntry(key, label);
                    e.Action = action;
                    _legend.Add(e);
                }
                if (!pad)
                {
                    void Add(Key k, string label, System.Func<bool> isOn = null)
                    {
                        var e = AddLegendEntry(k.ToString(), label);
                        e.IsOn = isOn ?? (() => Keyboard.current != null && Keyboard.current[k].isPressed);
                        _legend.Add(e);
                    }
                    Add(acts.HitKey, "Take hit");
                    Add(acts.DieKey, "Die / revive", () => acts.IsDead);
                    Add(acts.SitKey, "Sit", () => acts.IsSitting);
                    Add(acts.PickUpKey, "Pick up");
                    Add(acts.RepairKey, "Repair");
                    Add(acts.DanceKey, "Dance", () => acts.Emote == CharacterActions.ActionId.Dance);
                    Add(acts.TalkKey, "Talk", () => acts.Emote == CharacterActions.ActionId.Talk);
                }
#endif
                var push = AddLegendEntry("Crate", "Walk into it to push");
                push.IsOn = () => acts.IsPushing;
                _legend.Add(push);
            }

            _legendFlowPending = true; // sizes are only reliable once the canvas has laid out
        }

        // The legend outgrew one line: it wraps into centred rows that fit the bar's width.
        private float _legendSpacing = 32f;
        private bool _legendFlowPending;
        private float _legendFlowWidth = -1f;

        private void LateUpdate()
        {
            if (LegendContainer == null) return;
            float width = LegendContainer.rect.width;
            if (!_legendFlowPending && Mathf.Abs(width - _legendFlowWidth) < 1f) return;
            _legendFlowPending = false;
            _legendFlowWidth = width;
            FlowLegendIntoRows();
        }

        private void EnsureLegendRows()
        {
            var hlg = LegendContainer.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            if (hlg == null) return; // already converted
            _legendSpacing = Mathf.Min(hlg.spacing, 24f);
            var padding = hlg.padding;
            DestroyImmediate(hlg);

            var vlg = LegendContainer.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            vlg.padding = new RectOffset(padding.left, padding.right, 8, 8);
            vlg.spacing = 6f;
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = vlg.childForceExpandHeight = false;
            var fit = LegendContainer.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
            fit.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        }

        private void FlowLegendIntoRows()
        {
            // Take every entry out of the old rows (in order), then rebuild the rows.
            var entries = new List<RectTransform>();
            var rows = new List<GameObject>();
            foreach (Transform child in LegendContainer)
            {
                if (child.name.StartsWith("LegendRow_"))
                {
                    rows.Add(child.gameObject);
                    foreach (Transform e in child) entries.Add((RectTransform)e);
                }
                else if (child.gameObject != LegendEntryTemplate && child.gameObject.activeSelf &&
                         child.name.StartsWith("Legend_"))
                {
                    entries.Add((RectTransform)child);
                }
            }
            foreach (var e in entries) e.SetParent(LegendContainer, false);
            foreach (var r in rows) DestroyImmediate(r);

            var vlg = LegendContainer.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            float maxWidth = LegendContainer.rect.width - (vlg != null ? vlg.padding.horizontal : 48f);
            if (maxWidth <= 0f) maxWidth = 1800f;

            RectTransform row = null;
            float used = 0f;
            int rowIndex = 0;
            foreach (var e in entries)
            {
                // New text has no size until its mesh is generated.
                foreach (var t in e.GetComponentsInChildren<TMP_Text>(true)) t.ForceMeshUpdate();
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(e);
                float w = UnityEngine.UI.LayoutUtility.GetPreferredWidth(e);
                if (row == null || used + _legendSpacing + w > maxWidth)
                {
                    row = new GameObject($"LegendRow_{rowIndex++}", typeof(RectTransform)).GetComponent<RectTransform>();
                    row.SetParent(LegendContainer, false);
                    var h = row.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                    h.spacing = _legendSpacing;
                    h.childAlignment = TextAnchor.MiddleCenter;
                    h.childControlWidth = h.childControlHeight = true;
                    h.childForceExpandWidth = h.childForceExpandHeight = false;
                    used = -_legendSpacing;
                }
                e.SetParent(row, false);
                used += _legendSpacing + w;
            }
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(LegendContainer);
        }

#if ENABLE_INPUT_SYSTEM
        private string BindingText(InputAction action, bool gamepad)
        {
            if (action == null) return null;

            // Show the bindings of the active scheme (falls back to keyboard before any input arrives).
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isPartOfComposite) continue; // a composite head already lists its parts, e.g. W/A/S/D
                bool padBinding = b.isComposite
                    ? action.bindings[i + 1].groups?.Contains(GamepadScheme) == true
                    : b.groups?.Contains(GamepadScheme) == true;
                if (padBinding == gamepad) return action.GetBindingDisplayString(i);
            }
            return null;
        }
#endif

        private LegendEntry AddLegendEntry(string key, string label)
        {
            var go = Instantiate(LegendEntryTemplate, LegendContainer);
            go.name = $"Legend_{label}";
            go.SetActive(true);
            var e = new LegendEntry();
            var keyBg = go.transform.Find("KeyBg");
            if (keyBg != null)
            {
                e.KeyBg = keyBg.GetComponent<UnityEngine.UI.Image>();
                e.Key = keyBg.GetComponentInChildren<TMP_Text>(true);
            }
            if (e.Key != null) e.Key.text = key.ToUpperInvariant();
            var labelT = go.transform.Find("Label");
            if (labelT != null) labelT.GetComponent<TMP_Text>().text = label;
            return e;
        }

        private void UpdateLegendHighlights()
        {
            foreach (var e in _legend)
            {
                bool on = false;
#if ENABLE_INPUT_SYSTEM
                if (e.IsOn != null) on = e.IsOn();
                else if (e.Action != null) on = e.Action.IsPressed();
#endif
                if (e.KeyBg != null) e.KeyBg.color = on ? AccentColor : IdleKeyColor;
                if (e.Key != null) e.Key.color = on ? ActiveTextColor : TextColor;
            }
        }
    }
}
