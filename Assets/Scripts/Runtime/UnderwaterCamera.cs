using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Blue fog while the camera is below a WaterVolume's surface; the scene's own fog settings come back
    /// on surfacing. Added to the main camera by SwimController.
    /// </summary>
    [DisallowMultipleComponent]
    public class UnderwaterCamera : MonoBehaviour
    {
        public Color FogColor = new Color(0.08f, 0.28f, 0.42f);
        public float FogDensity = 0.22f;

        public bool IsUnderwater { get; private set; }

        private bool _savedFog;
        private Color _savedColor;
        private FogMode _savedMode;
        private float _savedDensity;

        private void LateUpdate()
        {
            var water = WaterVolume.At(transform.position);
            bool under = water != null && transform.position.y < water.SurfaceY;
            if (under == IsUnderwater) return;
            IsUnderwater = under;
            if (under)
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
            else Restore();
        }

        private void OnDisable()
        {
            if (IsUnderwater) Restore();
            IsUnderwater = false;
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
