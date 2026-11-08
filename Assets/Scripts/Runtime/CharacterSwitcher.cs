using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Inspector-driven character switcher. Every character model lives as a child of the
    /// Player in the hierarchy (nothing is spawned at runtime); only the active one is enabled.
    /// All models share the Player's Humanoid Animator. Changing Active Index in the Inspector
    /// applies immediately in Edit Mode, and in Play Mode it also carries over the current
    /// animation state, time and parameters so the new character continues the same frame.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    public class CharacterSwitcher : MonoBehaviour
    {
        [Serializable]
        public class CharacterEntry
        {
            public string Name;
            [Tooltip("The character model child object under the Player")]
            public GameObject Root;
            [Tooltip("Humanoid avatar for this model")]
            public Avatar Avatar;
        }

        [SerializeField] private Animator _animator;
        public List<CharacterEntry> Characters = new List<CharacterEntry>();
        [SerializeField] private int _activeIndex;

        [Header("Play Mode")]
        [Tooltip("Tab / 1-9 / Gamepad LB-RB switch characters while playing")]
        public bool EnableHotkeys = true;
        public bool ShowHint = true;

        public event Action<int> CharacterChanged;

        public int ActiveIndex
        {
            get => _activeIndex;
            set => SwitchTo(value);
        }

        public CharacterEntry ActiveCharacter =>
            _activeIndex >= 0 && _activeIndex < Characters.Count ? Characters[_activeIndex] : null;

        private int _appliedIndex = -1;

        private struct LayerSnapshot
        {
            public int StateHash;
            public float NormalizedTime;
            public float Weight;
        }

        private void Reset() => _animator = GetComponent<Animator>();

        private void OnEnable() => Apply(_activeIndex, false);

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_animator == null) _animator = GetComponent<Animator>();
            if (Characters.Count > 0) _activeIndex = Mathf.Clamp(_activeIndex, 0, Characters.Count - 1);
            if (_activeIndex == _appliedIndex) return;

            // Hierarchy changes are not allowed inside OnValidate, so apply on the next editor tick.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                Apply(_activeIndex, Application.isPlaying);
                if (!Application.isPlaying)
                {
                    foreach (var c in Characters)
                        if (c?.Root != null) UnityEditor.EditorUtility.SetDirty(c.Root);
                    UnityEditor.EditorUtility.SetDirty(_animator);
                }
            };
        }
#endif

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (!Application.isPlaying || !EnableHotkeys || Characters.Count == 0) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.tabKey.wasPressedThisFrame) Next();
                for (int i = 0; i < Characters.Count && i < 9; i++)
                    if (kb[Key.Digit1 + i].wasPressedThisFrame) SwitchTo(i);
            }

            var pad = Gamepad.current;
            if (pad != null)
            {
                if (pad.rightShoulder.wasPressedThisFrame) Next();
                if (pad.leftShoulder.wasPressedThisFrame) Previous();
            }
#endif
        }

        public void Next() { if (Characters.Count > 0) SwitchTo((_activeIndex + 1) % Characters.Count); }
        public void Previous() { if (Characters.Count > 0) SwitchTo((_activeIndex - 1 + Characters.Count) % Characters.Count); }

        public void SwitchTo(int index)
        {
            if (index == _appliedIndex) return;
            Apply(index, Application.isPlaying);
        }

        private void Apply(int index, bool preserveAnimation)
        {
            if (_animator == null || Characters.Count == 0) return;
            index = Mathf.Clamp(index, 0, Characters.Count - 1);
            var entry = Characters[index];
            if (entry == null || entry.Root == null) return;

            bool canPreserve = preserveAnimation && _appliedIndex >= 0 &&
                               _animator.isActiveAndEnabled && _animator.runtimeAnimatorController != null;
            LayerSnapshot[] layers = canPreserve ? CaptureLayers() : null;
            AnimatorControllerParameter[] parameters = canPreserve ? _animator.parameters : null;
            object[] values = canPreserve ? CaptureParameters(parameters) : null;

            for (int i = 0; i < Characters.Count; i++)
            {
                var root = Characters[i]?.Root;
                if (root != null && root.activeSelf != (i == index)) root.SetActive(i == index);
            }

            bool avatarChanged = _animator.avatar != entry.Avatar;
            _animator.avatar = entry.Avatar;

            if (Application.isPlaying && (avatarChanged || canPreserve))
            {
                _animator.Rebind();
                if (canPreserve)
                {
                    RestoreParameters(parameters, values);
                    for (int i = 0; i < layers.Length && i < _animator.layerCount; i++)
                    {
                        if (i > 0) _animator.SetLayerWeight(i, layers[i].Weight);
                        _animator.Play(layers[i].StateHash, i, layers[i].NormalizedTime);
                    }
                }
                // Evaluate now so the new model never shows a bind pose frame.
                _animator.Update(0f);
            }

            bool changed = _appliedIndex != index;
            _activeIndex = index;
            _appliedIndex = index;
            if (changed) CharacterChanged?.Invoke(index);
        }

        private LayerSnapshot[] CaptureLayers()
        {
            var snaps = new LayerSnapshot[_animator.layerCount];
            for (int i = 0; i < snaps.Length; i++)
            {
                // Mid-transition: jump straight to where we are heading.
                var info = _animator.IsInTransition(i)
                    ? _animator.GetNextAnimatorStateInfo(i)
                    : _animator.GetCurrentAnimatorStateInfo(i);
                snaps[i] = new LayerSnapshot
                {
                    StateHash = info.fullPathHash,
                    NormalizedTime = info.loop ? Mathf.Repeat(info.normalizedTime, 1f) : info.normalizedTime,
                    Weight = _animator.GetLayerWeight(i)
                };
            }
            return snaps;
        }

        private object[] CaptureParameters(AnimatorControllerParameter[] parameters)
        {
            var values = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: values[i] = _animator.GetFloat(p.nameHash); break;
                    case AnimatorControllerParameterType.Int: values[i] = _animator.GetInteger(p.nameHash); break;
                    case AnimatorControllerParameterType.Bool: values[i] = _animator.GetBool(p.nameHash); break;
                }
            }
            return values;
        }

        private void RestoreParameters(AnimatorControllerParameter[] parameters, object[] values)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (values[i] == null || _animator.IsParameterControlledByCurve(p.nameHash)) continue;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: _animator.SetFloat(p.nameHash, (float)values[i]); break;
                    case AnimatorControllerParameterType.Int: _animator.SetInteger(p.nameHash, (int)values[i]); break;
                    case AnimatorControllerParameterType.Bool: _animator.SetBool(p.nameHash, (bool)values[i]); break;
                }
            }
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || !ShowHint || ActiveCharacter == null) return;
            GUI.Label(new Rect(12, 12, 600, 24),
                $"Character: {ActiveCharacter.Name}   [Tab / 1-{Mathf.Min(Characters.Count, 9)} / LB-RB to switch]");
        }
    }
}
