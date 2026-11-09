using System.Collections.Generic;
using TMPro;
using Transport;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Showroom display: one vehicle at a time on this transform, with the player seated on it.
    /// A panel (a copy of the HUD's character panel, top right) switches vehicles and gets on or off;
    /// B cycles vehicles, E gets on or off, A/D turn the handlebars. The vehicle never moves.
    /// </summary>
    [DefaultExecutionOrder(-50)] // reads Interact before CharacterActions does
    public class BikeShowroom : MonoBehaviour
    {
        [System.Serializable]
        public class Entry
        {
            public string Name;
            public GameObject Prefab;
        }

        public List<Entry> Vehicles = new List<Entry>();
        public BikeRider Rider;
        public CharacterHUD Hud;
        [SerializeField] private int _activeIndex;
        public bool RideOnStart = true;
        [Tooltip("Get on with E from this close")]
        public float MountDistance = 2f;
        [Tooltip("Camera yaw from the vehicle's heading when the scene starts (a three-quarter side view)")]
        public float StartCameraYaw = 135f;
#if ENABLE_INPUT_SYSTEM
        public Key NextKey = Key.B;
#endif

        public int ActiveIndex => _activeIndex;
        public RideableVehicle Current { get; private set; }

        private TMP_Text _nameLabel;
        private Button _template;
        private RectTransform _container;
        private readonly List<Button> _buttons = new List<Button>();
        private Button _rideButton;

        private void Start()
        {
            if (Rider == null) Rider = FindAnyObjectByType<BikeRider>();
            if (Hud == null) Hud = FindAnyObjectByType<CharacterHUD>();
            Show(_activeIndex);
            if (RideOnStart && Rider != null && Current != null) Rider.Mount(Current);
            var cam = FindAnyObjectByType<ThirdPersonCamera>();
            if (cam != null) cam.SetOrientation(transform.eulerAngles.y + StartCameraYaw, 12f);
            BuildPanel();
            Refresh();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb[NextKey].wasPressedThisFrame) Show(_activeIndex + 1);
#endif
            var inputs = Rider != null ? Rider.Inputs : null;
            if (inputs != null && inputs.interact)
            {
                bool near = Current != null &&
                            Vector3.Distance(Rider.transform.position, Current.transform.position) < MountDistance;
                if (Rider.IsRiding || near)
                {
                    inputs.interact = false; // ours, not an Interact gesture
                    ToggleRide();
                }
            }
        }

        public void ToggleRide()
        {
            if (Rider == null || Current == null) return;
            if (Rider.IsRiding) Rider.Dismount();
            else Rider.Mount(Current);
            Refresh();
        }

        /// <summary>Replaces the vehicle on display; a seated rider moves onto the new one.</summary>
        public void Show(int index)
        {
            if (Vehicles.Count == 0) return;
            _activeIndex = ((index % Vehicles.Count) + Vehicles.Count) % Vehicles.Count;
            bool riding = Rider != null && Rider.IsRiding;
            if (Current != null) Destroy(Current.gameObject);
            Current = null;

            var prefab = Vehicles[_activeIndex].Prefab;
            if (prefab != null)
            {
                var go = Instantiate(prefab, transform.position, transform.rotation, transform);
                Current = go.GetComponent<RideableVehicle>();
                if (Current != null)
                {
                    Current.autoFromMotion = false; // on display: the wheels only turn if it moves
                    Current.Bind();
                }
            }
            if (riding && Current != null) Rider.Mount(Current);
            else if (riding) Rider.Dismount();
            Refresh();
        }

        // ---------------------------------------------------------------- panel

        /// <summary>Copies the HUD's character panel to the top right and turns it into the vehicle picker.</summary>
        private void BuildPanel()
        {
            if (Hud == null || Hud.NameLabel == null) return;
            var canvas = Hud.NameLabel.canvas;
            if (canvas == null) return;
            Transform source = Hud.NameLabel.transform;
            while (source.parent != null && source.parent != canvas.transform) source = source.parent;

            string namePath = PathFrom(source, Hud.NameLabel.transform);
            string prevPath = Hud.PrevButton != null ? PathFrom(source, Hud.PrevButton.transform) : null;
            string nextPath = Hud.NextButton != null ? PathFrom(source, Hud.NextButton.transform) : null;
            string containerPath = Hud.CharacterButtonContainer != null ? PathFrom(source, Hud.CharacterButtonContainer) : null;
            string templatePath = Hud.CharacterButtonTemplate != null ? PathFrom(source, Hud.CharacterButtonTemplate.transform) : null;

            var panel = Instantiate(source.gameObject, source.parent).transform as RectTransform;
            panel.name = "VehiclePanel";
            var from = source as RectTransform;
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1f, 1f);
            panel.anchoredPosition = new Vector2(-from.anchoredPosition.x, from.anchoredPosition.y);

            _nameLabel = Find<TMP_Text>(panel, namePath);
            _container = Find<RectTransform>(panel, containerPath);
            _template = Find<Button>(panel, templatePath);
            var prev = Find<Button>(panel, prevPath);
            var next = Find<Button>(panel, nextPath);
            if (prev != null) Rewire(prev, () => Show(_activeIndex - 1));
            if (next != null) Rewire(next, () => Show(_activeIndex + 1));

            foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.text.Trim().ToUpperInvariant() == "CHARACTER") text.text = "VEHICLE";
                else if (text.text.Contains("Esc")) text.text = "B next   E get on / off   A/D steer";
            }

            // The copy carries the character buttons the HUD made; start from the template only.
            if (_container != null)
                foreach (Transform child in _container)
                    if (_template == null || child != _template.transform) Destroy(child.gameObject);
            if (_template == null || _container == null) return;
            _template.gameObject.SetActive(false);
            for (int i = 0; i < Vehicles.Count; i++)
            {
                int index = i;
                _buttons.Add(MakeButton($"{i + 1}  {Label(i)}", () => Show(index)));
            }
            _rideButton = MakeButton("Get off", ToggleRide);
        }

        private Button MakeButton(string text, System.Action action)
        {
            var b = Instantiate(_template, _container);
            b.gameObject.SetActive(true);
            var label = b.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = text;
            Rewire(b, action);
            return b;
        }

        private static void Rewire(Button button, System.Action action)
        {
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() =>
            {
                action();
                // Clear focus so Space / Enter never re-press the last clicked button.
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            });
        }

        private void Refresh()
        {
            if (_nameLabel != null) _nameLabel.text = Label(_activeIndex);
            for (int i = 0; i < _buttons.Count; i++)
            {
                bool on = i == _activeIndex;
                if (_buttons[i].targetGraphic is Image img) img.color = on ? Hud.AccentColor : Hud.ButtonColor;
                var label = _buttons[i].GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.color = on ? Hud.ActiveTextColor : Hud.TextColor;
            }
            if (_rideButton != null)
            {
                var label = _rideButton.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.text = Rider != null && Rider.IsRiding ? "Get off  (E)" : "Ride  (E)";
            }
        }

        private string Label(int i)
        {
            if (i < 0 || i >= Vehicles.Count) return "-";
            var e = Vehicles[i];
            if (!string.IsNullOrWhiteSpace(e.Name)) return e.Name;
            return e.Prefab != null ? e.Prefab.name.Replace('_', ' ') : $"Vehicle {i + 1}";
        }

        private static string PathFrom(Transform root, Transform t)
        {
            if (t == root) return "";
            string path = t.name;
            while (t.parent != null && t.parent != root) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }

        private static T Find<T>(Transform root, string path) where T : Component
        {
            if (path == null) return null;
            var t = path.Length == 0 ? root : root.Find(path);
            return t != null ? t.GetComponent<T>() : null;
        }
    }
}
