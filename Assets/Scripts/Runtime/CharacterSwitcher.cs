using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Swaps the visual character model under the Player while keeping the shared Humanoid
    /// Animator running. Current state, normalized time, layer weights and parameters are
    /// captured before the swap and restored after rebinding, so the new character continues
    /// the exact same animation frame.
    /// Tab / Gamepad RB = next, Gamepad LB = previous, 1-9 = pick directly.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class CharacterSwitcher : MonoBehaviour
    {
        [Serializable]
        public class CharacterEntry
        {
            public string Name;
            [Tooltip("Humanoid model (FBX root or prefab)")]
            public GameObject Model;
            [Tooltip("Humanoid avatar. Falls back to the model's own Animator avatar if empty.")]
            public Avatar Avatar;
        }

        public CharacterEntry[] Characters = Array.Empty<CharacterEntry>();
        public int StartIndex;
        [Tooltip("Parent for the spawned model. Defaults to this transform.")]
        public Transform MeshParent;
        [Tooltip("Name of the existing model child to replace on the first switch.")]
        public string MeshChildName = "Mesh";
        public bool ShowHint = true;

        public int CurrentIndex { get; private set; } = -1;
        public event Action<int> CharacterChanged;

        private Animator _animator;
        private GameObject _current;

        private struct LayerSnapshot
        {
            public int StateHash;
            public float NormalizedTime;
            public float Weight;
        }

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            if (MeshParent == null) MeshParent = transform;
            var existing = MeshParent.Find(MeshChildName);
            if (existing != null) _current = existing.gameObject;
        }

        private void Start()
        {
            if (Characters.Length > 0) SwitchTo(Mathf.Clamp(StartIndex, 0, Characters.Length - 1));
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.tabKey.wasPressedThisFrame) Next();
                for (int i = 0; i < Characters.Length && i < 9; i++)
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

        public void Next() => SwitchTo((CurrentIndex + 1) % Characters.Length);
        public void Previous() => SwitchTo((CurrentIndex - 1 + Characters.Length) % Characters.Length);

        public void SwitchTo(int index)
        {
            if (Characters.Length == 0 || index < 0 || index >= Characters.Length) return;
            if (index == CurrentIndex) return;

            var entry = Characters[index];
            if (entry == null || entry.Model == null)
            {
                Debug.LogWarning($"[CharacterSwitcher] Character {index} has no model assigned.");
                return;
            }

            bool preserve = CurrentIndex >= 0 && _animator.isActiveAndEnabled && _animator.runtimeAnimatorController != null;
            LayerSnapshot[] layers = preserve ? CaptureLayers() : null;
            AnimatorControllerParameter[] parameters = preserve ? _animator.parameters : null;
            object[] values = preserve ? CaptureParameters(parameters) : null;

            // Remove the old model from the hierarchy right now so Rebind does not see it.
            if (_current != null)
            {
                _current.SetActive(false);
                _current.transform.SetParent(null, false);
                Destroy(_current);
            }

            var model = Instantiate(entry.Model, MeshParent, false);
            model.name = MeshChildName;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            Avatar avatar = entry.Avatar;
            if (model.TryGetComponent<Animator>(out var childAnimator))
            {
                if (avatar == null) avatar = childAnimator.avatar;
                // Only the root Animator drives the skeleton.
                childAnimator.enabled = false;
                Destroy(childAnimator);
            }

            if (avatar == null || !avatar.isHuman)
                Debug.LogWarning($"[CharacterSwitcher] '{entry.Name}' has no Humanoid avatar; animation retargeting may fail.");

            _current = model;
            _animator.avatar = avatar;
            _animator.Rebind();

            if (preserve)
            {
                RestoreParameters(parameters, values);
                for (int i = 0; i < layers.Length && i < _animator.layerCount; i++)
                {
                    if (i > 0) _animator.SetLayerWeight(i, layers[i].Weight);
                    _animator.Play(layers[i].StateHash, i, layers[i].NormalizedTime);
                }
            }

            // Evaluate immediately so the new model never shows a bind pose frame.
            _animator.Update(0f);

            CurrentIndex = index;
            CharacterChanged?.Invoke(index);
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
                if (_animator.IsParameterControlledByCurve(p.nameHash)) continue;
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
            if (!ShowHint || Characters.Length == 0 || CurrentIndex < 0) return;
            string name = Characters[CurrentIndex].Name;
            GUI.Label(new Rect(12, 12, 600, 24),
                $"Character: {name}   [Tab / 1-{Mathf.Min(Characters.Length, 9)} / LB-RB to switch]");
        }
    }
}
