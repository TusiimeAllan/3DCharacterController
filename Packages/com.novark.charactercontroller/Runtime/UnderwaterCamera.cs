using UnityEngine;
#if CCS_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// What it feels like with the camera under water (any WaterVolume, below its surface):
    /// - blue-green fog that swallows the distance;
    /// - a post-processing look: colour grade towards blue-green, darker edges, a little colour fringing,
    ///   a slow lens wobble and slight blur;
    /// - muffled sound (low-pass filter on the listener);
    /// - a gentle sway of the camera.
    /// Everything blends in and out over a short time; the scene's own fog comes back on surfacing.
    /// The post-processing look needs URP (CCS_URP is defined when the URP package is installed); on other
    /// pipelines the fog, sound and sway still apply.
    /// Added to the main camera by SwimController. Runs after ThirdPersonCamera so the sway sits on top.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class UnderwaterCamera : MonoBehaviour
    {
        [Header("Fog")]
        public Color FogColor = new Color(0.06f, 0.26f, 0.36f);
        public float FogDensity = 0.22f;

        [Header("Look")]
        public Color Tint = new Color(0.55f, 0.85f, 0.95f);
        [Range(0f, 1f)] public float VignetteIntensity = 0.42f;
        [Range(0f, 1f)] public float ChromaticAberration = 0.35f;
        [Range(0f, 1f)] public float WobbleAmount = 0.12f;
        public float WobbleSpeed = 1.3f;
        [Tooltip("Seconds to blend the look in or out")]
        public float BlendTime = 0.35f;

        [Header("Sound")]
        [Tooltip("Low-pass cutoff under water (Hz); 22000 above")]
        public float MuffledCutoff = 900f;

        [Header("Sway")]
        public float SwayRoll = 1.2f;
        public float SwayBob = 0.04f;

        public bool IsUnderwater { get; private set; }

        private bool _savedFog;
        private Color _savedColor;
        private FogMode _savedMode;
        private float _savedDensity;
        private float _blend;
#if CCS_URP
        private Volume _volume;
        private LensDistortion _lens;
#endif
        private AudioLowPassFilter _lowPass;

        private void Awake()
        {
#if CCS_URP
            // URP camera post-processing must be on for the volume to show.
            var data = GetComponent<Camera>() != null ? GetComponent<Camera>().GetUniversalAdditionalCameraData() : null;
            if (data != null) data.renderPostProcessing = true;

            var go = new GameObject("UnderwaterVolume") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.weight = 0f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.DontSave;
            var grade = profile.Add<ColorAdjustments>(true);
            grade.colorFilter.Override(Tint);
            grade.saturation.Override(-25f);
            grade.contrast.Override(-10f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(VignetteIntensity);
            vignette.smoothness.Override(0.6f);
            vignette.color.Override(new Color(0.02f, 0.12f, 0.18f));
            var fringe = profile.Add<ChromaticAberration>(true);
            fringe.intensity.Override(ChromaticAberration);
            _lens = profile.Add<LensDistortion>(true);
            _lens.intensity.Override(0f);
            var blur = profile.Add<DepthOfField>(true);
            blur.mode.Override(DepthOfFieldMode.Gaussian);
            blur.gaussianStart.Override(4f);
            blur.gaussianEnd.Override(18f);
            blur.gaussianMaxRadius.Override(1f);
            _volume.sharedProfile = profile;
#endif

            if (GetComponent<AudioListener>() != null)
            {
                _lowPass = GetComponent<AudioLowPassFilter>();
                if (_lowPass == null) _lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            }
            if (_lowPass != null) _lowPass.cutoffFrequency = 22000f;
        }

        private void LateUpdate()
        {
            var water = WaterVolume.At(transform.position);
            bool under = water != null && transform.position.y < water.SurfaceY;
            if (under != IsUnderwater)
            {
                IsUnderwater = under;
                if (under) SaveAndSetFog();
                else Restore();
            }

            _blend = Mathf.MoveTowards(_blend, under ? 1f : 0f, Time.unscaledDeltaTime / Mathf.Max(0.01f, BlendTime));
#if CCS_URP
            if (_volume != null) _volume.weight = _blend;
#endif
            if (_lowPass != null) _lowPass.cutoffFrequency = Mathf.Lerp(22000f, MuffledCutoff, _blend);
            if (_blend <= 0.001f) return;

            float t = Time.time;
#if CCS_URP
            if (_lens != null) _lens.intensity.value = Mathf.Sin(t * WobbleSpeed) * WobbleAmount * _blend;
#endif
            // Sway on top of wherever the follow camera put us this frame.
            transform.rotation = transform.rotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t * 0.9f) * SwayRoll * _blend);
            transform.position += Vector3.up * (Mathf.Sin(t * 1.4f) * SwayBob * _blend);
        }

        private void SaveAndSetFog()
        {
            _savedFog = RenderSettings.fog;
            _savedColor = RenderSettings.fogColor;
            _savedMode = RenderSettings.fogMode;
            _savedDensity = RenderSettings.fogDensity;
            RenderSettings.fog = true;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = FogDensity;
        }

        private void OnDisable()
        {
            if (IsUnderwater) Restore();
            IsUnderwater = false;
            _blend = 0f;
#if CCS_URP
            if (_volume != null) _volume.weight = 0f;
#endif
            if (_lowPass != null) _lowPass.cutoffFrequency = 22000f;
        }

        private void Restore()
        {
            RenderSettings.fog = _savedFog;
            RenderSettings.fogColor = _savedColor;
            RenderSettings.fogMode = _savedMode;
            RenderSettings.fogDensity = _savedDensity;
        }
    }
}
