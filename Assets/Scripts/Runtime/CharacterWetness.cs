using System.Collections.Generic;
using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// How wet the character is (0 dry .. 1 soaked) and what that looks like: wet surfaces go darker and
    /// glossier (a MaterialPropertyBlock over the character's own materials, so no material is changed) and
    /// drops fall from the hands, elbows, head, hips and knees while it dries. SwimController soaks it in
    /// water and wades it up to the water line; it then dries over DryTime.
    /// Follows CharacterSwitcher model swaps by refreshing its renderer list.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterWetness : MonoBehaviour
    {
        [Range(0f, 1f)] public float Wetness;
        [Tooltip("Seconds to dry from soaked")]
        public float DryTime = 30f;
        [Tooltip("How much darker soaked surfaces get (multiplier on their colour)")]
        [Range(0.3f, 1f)] public float WetDarkening = 0.6f;
        [Tooltip("Smoothness of soaked surfaces")]
        [Range(0f, 1f)] public float WetSmoothness = 0.85f;
        [Tooltip("Drops per second when soaked")]
        public float MaxDripRate = 14f;
        public WaterEffects Effects;

        /// <summary>Set while the character is in water, so it doesn't dry or drip.</summary>
        [System.NonSerialized] public bool InWater;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly HumanBodyBones[] DripBones =
        {
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        };

        private struct Slot
        {
            public Renderer Renderer;
            public int Index;
            public Color Color;
            public float Smoothness;
        }

        private readonly List<Slot> _slots = new List<Slot>();
        private MaterialPropertyBlock _block;
        private float _applied = -1f;
        private float _refresh;
        private float _dripCarry;
        private Animator _animator;

        private void Awake()
        {
            _block = new MaterialPropertyBlock();
            if (Effects == null) Effects = GetComponent<WaterEffects>();
        }

        /// <summary>Soak up to this level (never makes it drier).</summary>
        public void SoakTo(float level) => Wetness = Mathf.Max(Wetness, Mathf.Clamp01(level));

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (!InWater && Wetness > 0f) Wetness = Mathf.Max(0f, Wetness - dt / Mathf.Max(1f, DryTime));

            _refresh -= dt;
            if (_refresh <= 0f || _slots.Count == 0 || _slots[0].Renderer == null)
            {
                _refresh = 1f;
                Collect();
            }
            if (Mathf.Abs(_applied - Wetness) > 0.005f) Apply();

            if (!InWater && Wetness > 0.15f) Drip(dt);
        }

        private void Drip(float dt)
        {
            if (Effects == null) return;
            if (_animator == null || !_animator.isActiveAndEnabled) _animator = GetComponentInChildren<Animator>();
            if (_animator == null || !_animator.isHuman) return;
            _dripCarry += MaxDripRate * Wetness * Wetness * dt;
            while (_dripCarry >= 1f)
            {
                _dripCarry -= 1f;
                var bone = _animator.GetBoneTransform(DripBones[Random.Range(0, DripBones.Length)]);
                if (bone != null) Effects.Drip(bone.position + Random.insideUnitSphere * 0.05f);
            }
        }

        private void Collect()
        {
            _slots.Clear();
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || !r.enabled) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !m.HasProperty(BaseColorId)) continue;
                    _slots.Add(new Slot
                    {
                        Renderer = r, Index = i, Color = m.GetColor(BaseColorId),
                        Smoothness = m.HasProperty(SmoothnessId) ? m.GetFloat(SmoothnessId) : 0.5f,
                    });
                }
            }
            _applied = -1f;
        }

        private void Apply()
        {
            _applied = Wetness;
            foreach (var slot in _slots)
            {
                if (slot.Renderer == null) continue;
                slot.Renderer.GetPropertyBlock(_block, slot.Index);
                if (Wetness <= 0.001f)
                {
                    _block.Clear();
                }
                else
                {
                    Color c = slot.Color * Mathf.Lerp(1f, WetDarkening, Wetness);
                    c.a = slot.Color.a;
                    _block.SetColor(BaseColorId, c);
                    _block.SetFloat(SmoothnessId, Mathf.Lerp(slot.Smoothness, WetSmoothness, Wetness));
                }
                slot.Renderer.SetPropertyBlock(_block, slot.Index);
            }
        }
    }
}
