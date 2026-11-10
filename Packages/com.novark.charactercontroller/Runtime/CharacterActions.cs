using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharacterControllerSystem
{
    /// <summary>
    /// Gameplay actions on top of locomotion: hit reactions, health and death, sitting, interacting,
    /// picking up, punching, repairing, emotes and pushing crates.
    ///
    /// Full-body actions are Animator Controller states (built by ActionStatesBuilder) chosen with the
    /// Action / Dead / Sitting / Pushing parameters. States tagged "Action" lock movement until they end,
    /// "Emote" loops are cancelled by moving. Hit reactions are upper body only, so the legs keep walking:
    /// with a locomotion host (motion matching) they play on its layer above everything, otherwise on the controller's
    /// UpperBody layer.
    /// </summary>
    [DefaultExecutionOrder(90)] // before LocomotionAnimationHost reads NeedsControllerLayer
    [DisallowMultipleComponent]
    public class CharacterActions : MonoBehaviour, IBodyScaled
    {
        public enum ActionId
        {
            None = 0,
            Interact = 1,
            PickUp = 2,
            Jab = 3,
            Cross = 4,
            Repair = 5,
            Dance = 10,
            Talk = 11
        }

        [Header("References")]
        public ThirdPersonController Controller;
        public PlayerInputs Inputs;

        [Header("Health")]
        public float MaxHealth = 100f;
        [Tooltip("Damage dealt by the test hit key")]
        public float HitDamage = 25f;

        [Header("Hit Reactions")]
        public AnimationClip HitChestClip;
        public AnimationClip HitHeadClip;
        [Tooltip("Spine, arms and head; legs keep their locomotion")]
        public AvatarMask UpperBodyMask;
        public float HitBlendIn = 0.05f;

        [Header("Pushing")]
        [Tooltip("Heaviest rigidbody the character can push (kg)")]
        public float MaxPushMass = 150f;
        [Tooltip("Walking speed while pushing (m/s)")]
        public float PushSpeed = 1.1f;
        [Tooltip("Seconds pushing continues without contact, so a bump does not drop the pose")]
        public float PushContactGrace = 0.3f;

#if ENABLE_INPUT_SYSTEM
        [Header("Test Keys (Interact and Attack come from the input actions)")]
        public Key HitKey = Key.H;
        public Key DieKey = Key.K;
        public Key PickUpKey = Key.F;
        public Key SitKey = Key.X;
        public Key RepairKey = Key.R;
        public Key DanceKey = Key.V;
        public Key TalkKey = Key.T;
#endif

        public float Health { get; private set; }
        public bool IsDead { get; private set; }
        public bool IsSitting { get; private set; }
        public bool IsPushing { get; private set; }
        public ActionId Emote => _emote;

        /// <summary>True while a full-body action, emote or pose (death, sitting) owns the skeleton.</summary>
        public bool OverridesPose => IsDead || IsSitting || _inAction || _inEmote || _emote != ActionId.None ||
                                     _requested != ActionId.None;

        /// <summary>True when the Animator Controller must be fully visible (MxM hands over to it).</summary>
        public bool NeedsControllerLayer => OverridesPose || IsPushing || _inPush;

        public event System.Action<float> HealthChanged;

        private static readonly int ActionParam = Animator.StringToHash("Action");
        private static readonly int DeadParam = Animator.StringToHash("Dead");
        private static readonly int DeathIndexParam = Animator.StringToHash("DeathIndex");
        private static readonly int SittingParam = Animator.StringToHash("Sitting");
        private static readonly int PushingParam = Animator.StringToHash("Pushing");
        private static readonly int HitParam = Animator.StringToHash("Hit");
        private static readonly int HitIndexParam = Animator.StringToHash("HitIndex");

        private Animator _animator;
        private LocomotionAnimationHost _mm;

        private ActionId _requested;   // one-shot asked for, cleared once its state starts
        private float _requestTimeout;
        private ActionId _emote;       // looping emote currently on
        private bool _inAction, _inEmote, _inPush;
        private bool _nextPunchIsCross;
        private float _punchBufferedUntil;
        private float _lastPunchTime = -10f;
        private bool _headNext;

        private bool _hitOnHost;
        private float _hitOutAt = -1f;
        private float _hitOutDuration;

        private Rigidbody _pushBody;
        private Vector3 _pushDirection;
        private float _pushContactTime = -1f;

        private void Reset() => AutoAssign();

        private void AutoAssign()
        {
            if (Controller == null) Controller = GetComponent<ThirdPersonController>();
            if (Inputs == null) Inputs = GetComponent<PlayerInputs>();
        }

        private void Awake()
        {
            AutoAssign();
            _animator = GetComponent<Animator>();
            _mm = GetComponent<LocomotionAnimationHost>();
            Health = MaxHealth;
            if (Controller == null || Inputs == null)
            {
                Debug.LogError($"[{nameof(CharacterActions)}] Missing references, disabling.", this);
                enabled = false;
            }
        }

        private void OnDisable()
        {
            if (Controller == null) return;
            Controller.MovementLocked = false;
            Controller.SpeedScale = 1f;
        }

        private void Update()
        {
            if (!ReadStates()) return;

            HandleInput();
            UpdateRequests();
            UpdatePushing();
            UpdateHitLayer();

            Controller.MovementLocked = IsDead || IsSitting || _inAction || _requested != ActionId.None;
        }

        // ---------------------------------------------------------------- Public API

        public void TakeHit(float damage, bool head)
        {
            if (IsDead) return;
            Health = Mathf.Max(0f, Health - damage);
            HealthChanged?.Invoke(Health);
            if (Health <= 0f)
            {
                Die();
                return;
            }
            PlayHit(head);
        }

        public void Die()
        {
            if (IsDead) return;
            IsDead = true;
            Health = 0f;
            HealthChanged?.Invoke(Health);
            CancelAll();
            SetInt(DeathIndexParam, Random.Range(0, 2));
            SetBool(DeadParam, true);
        }

        public void Revive()
        {
            if (!IsDead) return;
            IsDead = false;
            Health = MaxHealth;
            HealthChanged?.Invoke(Health);
            SetBool(DeadParam, false);
        }

        public bool Play(ActionId action)
        {
            if (!CanAct) return false;
            bool loop = IsLoop(action);
            if (loop)
            {
                _emote = _emote == action ? ActionId.None : action; // pressing again stops it
                SetInt(ActionParam, (int)_emote);
                return true;
            }
            _emote = ActionId.None;
            _requested = action;
            _requestTimeout = Time.time + 0.3f;
            SetInt(ActionParam, (int)action);
            return true;
        }

        public void Sit(bool sit)
        {
            if (sit == IsSitting || IsDead) return;
            if (sit && !CanAct) return;
            IsSitting = sit;
            SetBool(SittingParam, sit);
        }

        // ---------------------------------------------------------------- Internals

        private bool CanAct => !IsDead && !IsSitting && !_inAction && _requested == ActionId.None &&
                               Controller.Grounded && !Controller.IsCrouching && !Controller.IsJumping && !Controller.ExternallyDriven;

        private static bool IsLoop(ActionId action) => action == ActionId.Dance || action == ActionId.Talk;

        private void HandleInput()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb[HitKey].wasPressedThisFrame) { TakeHit(HitDamage, _headNext); _headNext = !_headNext; }
                if (kb[DieKey].wasPressedThisFrame) { if (IsDead) Revive(); else Die(); }
                if (kb[SitKey].wasPressedThisFrame) Sit(!IsSitting);
                if (kb[PickUpKey].wasPressedThisFrame) Play(ActionId.PickUp);
                if (kb[RepairKey].wasPressedThisFrame) Play(ActionId.Repair);
                if (kb[DanceKey].wasPressedThisFrame) Play(ActionId.Dance);
                if (kb[TalkKey].wasPressedThisFrame) Play(ActionId.Talk);
            }
#endif
            if (Inputs.interact)
            {
                Inputs.interact = false;
                if (IsSitting) Sit(false);
                else Play(ActionId.Interact);
            }

            if (Inputs.attack)
            {
                Inputs.attack = false;
                _punchBufferedUntil = Time.time + 0.5f; // a press during a punch chains into the next one
            }
            if (_punchBufferedUntil > Time.time && CanAct)
            {
                _punchBufferedUntil = 0f;
                if (Time.time - _lastPunchTime > 1.2f) _nextPunchIsCross = false; // a fresh combo starts with a jab
                _lastPunchTime = Time.time;
                Play(_nextPunchIsCross ? ActionId.Cross : ActionId.Jab);
                _nextPunchIsCross = !_nextPunchIsCross;
            }

            // Moving, jumping or crouching stands up from a seat and ends an emote.
            bool moving = Inputs.move.sqrMagnitude > 0.25f;
            if (IsSitting && moving && _inAction) Sit(false);
            if (_emote != ActionId.None && (moving || Inputs.jump || Controller.IsCrouching || !Controller.Grounded))
            {
                _emote = ActionId.None;
                SetInt(ActionParam, 0);
            }
        }

        private void UpdateRequests()
        {
            if (_requested == ActionId.None) return;
            // Once the state has started, clear the parameter so it does not fire again on the way out.
            if (_inAction || Time.time > _requestTimeout)
            {
                _requested = ActionId.None;
                SetInt(ActionParam, (int)_emote);
            }
        }

        private void PlayHit(bool head)
        {
            var clip = head ? HitHeadClip : HitChestClip;
            if (_mm != null && clip != null && _mm.PlayUpperBodyClip(clip, UpperBodyMask, HitBlendIn))
            {
                _hitOnHost = true;
                _hitOutAt = Time.time + clip.length * 0.5f;
                _hitOutDuration = clip.length * 0.5f + 0.1f;
                return;
            }
            SetInt(HitIndexParam, head ? 1 : 0);
            SetTrigger(HitParam);
        }

        private void UpdateHitLayer()
        {
            if (_hitOutAt < 0f || Time.time < _hitOutAt) return;
            _hitOutAt = -1f;
            if (_hitOnHost) _mm.StopUpperBodyClip(_hitOutDuration);
        }

        private void CancelAll()
        {
            _requested = ActionId.None;
            _emote = ActionId.None;
            _punchBufferedUntil = 0f;
            SetInt(ActionParam, 0);
            if (IsSitting)
            {
                IsSitting = false;
                SetBool(SittingParam, false);
            }
            if (_hitOnHost) _mm.StopUpperBodyClip(0.1f);
            _hitOutAt = -1f;
        }

        // ---------------------------------------------------------------- Pushing

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            var body = hit.rigidbody;
            if (body == null || body.isKinematic || body.mass > MaxPushMass) return;
            if (hit.moveDirection.y < -0.3f) return; // standing on it, not pushing it
            Vector3 direction = -hit.normal;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) return;
            direction.Normalize();
            if (Vector3.Dot(transform.forward, direction) < 0.5f) return; // only what we face

            _pushBody = body;
            _pushDirection = direction;
            _pushContactTime = Time.time;
        }

        private void UpdatePushing()
        {
            bool pushing = !IsDead && !IsSitting && !_inAction && Controller.Grounded && !Controller.IsCrouching &&
                           _pushBody != null && Time.time - _pushContactTime < PushContactGrace &&
                           Inputs.move.sqrMagnitude > 0.01f;

            if (pushing)
            {
                // Drive the crate a little slower than we walk so the hands stay on it (contact every frame);
                // gravity keeps its vertical velocity.
                Vector3 v = _pushBody.linearVelocity;
                Vector3 target = _pushDirection * (PushSpeed * 0.9f * Mathf.Clamp01(Inputs.move.magnitude));
                _pushBody.linearVelocity = new Vector3(target.x, v.y, target.z);
                _pushBody.WakeUp();
            }

            if (pushing != IsPushing)
            {
                IsPushing = pushing;
                SetBool(PushingParam, pushing);
            }

            float baseSpeed = Inputs.sprint ? Controller.SprintSpeed : Controller.MoveSpeed;
            Controller.SpeedScale = pushing && baseSpeed > 0f ? Mathf.Min(1f, PushSpeed / baseSpeed) : 1f;
        }

        // ---------------------------------------------------------------- Animator access

        /// <summary>
        /// With motion matching the Animator Controller runs inside MxM's playable graph, so its
        /// parameters and states are read through that playable; otherwise through the Animator.
        /// </summary>
        private bool ReadStates()
        {
            AnimatorStateInfo current, next;
            bool transition;
            if (_mm != null)
            {
                if (!_mm.TryGetControllerLayer(out var layer)) return false;
                current = layer.GetCurrentAnimatorStateInfo(0);
                transition = layer.IsInTransition(0);
                next = transition ? layer.GetNextAnimatorStateInfo(0) : default;
            }
            else
            {
                if (_animator == null || _animator.runtimeAnimatorController == null) return false;
                current = _animator.GetCurrentAnimatorStateInfo(0);
                transition = _animator.IsInTransition(0);
                next = transition ? _animator.GetNextAnimatorStateInfo(0) : default;
            }

            // A state counts while we are in it or blending into it (not while blending out of it).
            _inAction = transition ? next.IsTag("Action") : current.IsTag("Action");
            _inEmote = transition ? next.IsTag("Emote") : current.IsTag("Emote");
            _inPush = transition ? next.IsTag("Push") : current.IsTag("Push");
            return true;
        }

        private void SetInt(int id, int value)
        {
            if (_mm != null) { if (_mm.TryGetControllerLayer(out var l)) l.SetInteger(id, value); }
            else if (_animator != null) _animator.SetInteger(id, value);
        }

        private void SetBool(int id, bool value)
        {
            if (_mm != null) { if (_mm.TryGetControllerLayer(out var l)) l.SetBool(id, value); }
            else if (_animator != null) _animator.SetBool(id, value);
        }

        private void SetTrigger(int id)
        {
            if (_mm != null) { if (_mm.TryGetControllerLayer(out var l)) l.SetTrigger(id); }
            else if (_animator != null) _animator.SetTrigger(id);
        }
        // ---------------------------------------------------------------- body scale

        private float _bs = 1f; // body size relative to the reference (X Bot); scales the distances below

        /// <summary>Scales the distances and speeds to the body (see CharacterDimensions), relative to the current scale.</summary>
        public void ApplyBodyScale(float s)
        {
            float k = s / _bs; // relative, so values changed at runtime survive a re-scale
            PushSpeed *= k;
            _bs = s;
        }
    }
}
