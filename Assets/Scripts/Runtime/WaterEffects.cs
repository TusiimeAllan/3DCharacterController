using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Splash particles and water sounds for a character. The project has no audio files, so the sounds are
    /// synthesised once at start-up: a splash (a burst of filtered noise with a few falling droplet chirps)
    /// and a softer stroke swish.
    /// </summary>
    [DisallowMultipleComponent]
    public class WaterEffects : MonoBehaviour
    {
        [Tooltip("Particle material (URP Particles/Unlit). Created at runtime when empty.")]
        public Material SplashMaterial;
        [Range(0f, 1f)] public float Volume = 0.8f;

        private ParticleSystem _splash;
        private AudioSource _audio;
        private static AudioClip _splashClip;
        private static AudioClip _strokeClip;

        private void Awake()
        {
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 1f;
            _audio.minDistance = 2f;
            _audio.maxDistance = 30f;
            if (_splashClip == null) _splashClip = MakeSplashClip();
            if (_strokeClip == null) _strokeClip = MakeStrokeClip();
            _splash = MakeParticles();
        }

        /// <summary>Splash at a point on the surface; strength 0..1 (a dive or fall is about 1).</summary>
        public void Splash(Vector3 surfacePoint, float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (_splash != null)
            {
                var emit = new ParticleSystem.EmitParams
                {
                    applyShapeToPosition = true,
                    position = surfacePoint,
                };
                var main = _splash.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(1f + 2f * strength, 2.5f + 4f * strength);
                main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.08f + 0.1f * strength);
                _splash.Emit(emit, Mathf.RoundToInt(8 + 70 * strength));
            }
            Play(_splashClip, 0.25f + 0.75f * strength, Random.Range(0.9f, 1.15f) - 0.15f * strength, surfacePoint);
        }

        /// <summary>A small splash and swish for a swim stroke or a wading step.</summary>
        public void Stroke(Vector3 surfacePoint, float strength = 0.3f)
        {
            if (_splash != null)
            {
                var main = _splash.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.06f);
                _splash.Emit(new ParticleSystem.EmitParams { applyShapeToPosition = true, position = surfacePoint },
                    Mathf.RoundToInt(4 + 10 * strength));
            }
            Play(_strokeClip, 0.25f + 0.5f * strength, Random.Range(0.85f, 1.2f), surfacePoint);
        }

        private void Play(AudioClip clip, float volume, float pitch, Vector3 at)
        {
            if (_audio == null || clip == null) return;
            _audio.transform.position = transform.position;
            _audio.pitch = pitch;
            _audio.PlayOneShot(clip, volume * Volume);
        }

        // ---------------------------------------------------------------- particles

        private ParticleSystem MakeParticles()
        {
            var go = new GameObject("Splash");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.0f);
            main.gravityModifier = 1f;
            main.maxParticles = 400;
            main.startColor = new Color(0.85f, 0.93f, 1f, 0.85f);

            var emission = ps.emission;
            emission.enabled = false; // emitted on demand

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 28f;
            shape.radius = 0.25f;
            shape.rotation = new Vector3(-90f, 0f, 0f); // spray upwards

            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.7f, 0.6f), new GradientAlphaKey(0f, 1f) });
            fade.color = g;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (SplashMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader != null) SplashMaterial = new Material(shader);
            }
            renderer.sharedMaterial = SplashMaterial;
            return ps;
        }

        // ---------------------------------------------------------------- synthesised sounds

        private static AudioClip MakeSplashClip()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.9f);
            var data = new float[n];
            var rng = new System.Random(7);
            float low = 0f, band = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                // Cutoff falls over time: a bright hit settling into a dull wash.
                float cutoff = Mathf.Lerp(0.55f, 0.08f, Mathf.Clamp01(t / 0.5f));
                low += cutoff * (noise - low);
                band += 0.5f * (low - band);
                float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-t * 6.5f);
                data[i] = (low - band * 0.6f) * env * 1.6f;
            }
            // Droplets: short chirps falling in pitch.
            for (int d = 0; d < 9; d++)
            {
                float start = 0.05f + (float)rng.NextDouble() * 0.55f;
                float f0 = 900f + (float)rng.NextDouble() * 1400f;
                float amp = 0.08f + (float)rng.NextDouble() * 0.12f;
                int s0 = (int)(start * rate), len = (int)(0.05f * rate);
                float phase = 0f;
                for (int k = 0; k < len && s0 + k < n; k++)
                {
                    float u = k / (float)len;
                    phase += 2f * Mathf.PI * f0 * (1f + 1.5f * u) / rate;
                    data[s0 + k] += Mathf.Sin(phase) * amp * Mathf.Sin(Mathf.PI * u);
                }
            }
            Normalize(data, 0.9f);
            var clip = AudioClip.Create("Splash", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip MakeStrokeClip()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.35f);
            var data = new float[n];
            var rng = new System.Random(11);
            float low = 0f, band = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                low += 0.18f * (noise - low);
                band += 0.35f * (low - band);
                float env = Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.6f));
                data[i] = (low - band) * env;
            }
            Normalize(data, 0.6f);
            var clip = AudioClip.Create("Stroke", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void Normalize(float[] data, float peak)
        {
            float max = 0f;
            foreach (float v in data) max = Mathf.Max(max, Mathf.Abs(v));
            if (max <= 0f) return;
            float k = peak / max;
            for (int i = 0; i < data.Length; i++) data[i] *= k;
        }
    }
}
