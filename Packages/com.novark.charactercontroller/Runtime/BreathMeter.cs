using UnityEngine;
using UnityEngine.UI;

namespace CharacterControllerSystem
{
    /// <summary>
    /// A small breath bar at the top of the screen, shown while the character is under water or still getting
    /// its breath back. Turns red and pulses when breath is nearly gone. Built from code; SwimController adds it.
    /// </summary>
    public class BreathMeter : MonoBehaviour
    {
        public SwimController Swim;

        private CanvasGroup _group;
        private Image _fill;
        private float _alpha;

        private void Start()
        {
            var canvasGo = new GameObject("BreathCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _group = canvasGo.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            var frame = NewImage("Breath", canvasGo.transform, new Color(0.02f, 0.08f, 0.12f, 0.75f));
            var rt = frame.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -36f);
            rt.sizeDelta = new Vector2(320f, 18f);

            _fill = NewImage("Fill", frame.transform, new Color(0.45f, 0.85f, 1f, 0.95f));
            var fr = _fill.rectTransform;
            fr.anchorMin = Vector2.zero;
            fr.anchorMax = Vector2.one;
            fr.offsetMin = new Vector2(3f, 3f);
            fr.offsetMax = new Vector2(-3f, -3f);
            fr.pivot = new Vector2(0f, 0.5f);

            var label = new GameObject("Label", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            label.transform.SetParent(frame.transform, false);
            label.text = "BREATH";
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 13;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = new Color(1f, 1f, 1f, 0.85f);
            label.raycastTarget = false;
            var lr = label.rectTransform;
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
        }

        private static Image NewImage(string name, Transform parent, Color color)
        {
            var img = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            img.transform.SetParent(parent, false);
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private void Update()
        {
            if (Swim == null || _fill == null) return;
            float breath = Swim.Breath;
            bool show = Swim.IsUnderwater || breath < 0.995f;
            _alpha = Mathf.MoveTowards(_alpha, show ? 1f : 0f, Time.unscaledDeltaTime * 3f);
            _group.alpha = _alpha;

            _fill.rectTransform.localScale = new Vector3(Mathf.Max(0.001f, breath), 1f, 1f);
            bool low = breath < 0.25f;
            float pulse = low ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) : 1f;
            _fill.color = Color.Lerp(new Color(1f, 0.3f, 0.25f, 0.95f), new Color(0.45f, 0.85f, 1f, 0.95f),
                Mathf.InverseLerp(0.1f, 0.4f, breath)) * new Color(1f, 1f, 1f, pulse);
        }
    }
}
