using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>A component whose distances and speeds were tuned for the reference body and scale with it.</summary>
    public interface IBodyScaled
    {
        /// <summary>
        /// Called with the body's size relative to the reference (1 = X Bot). Implementations multiply their
        /// distances and speeds by the change from the previous scale, so values set at runtime are kept.
        /// </summary>
        void ApplyBodyScale(float scale);
    }

    /// <summary>A component with sizes that follow the character's overall height (capsule, camera framing).</summary>
    public interface IBodyHeightScaled
    {
        /// <summary>Called with the character's height relative to the reference (1 = X Bot).</summary>
        void ApplyBodyHeight(float heightScale);
    }

    /// <summary>
    /// Fits the whole rig to the character's body. Every distance and speed in the kit was tuned on X Bot
    /// (a 1.8 m humanoid); this measures the current avatar's human scale (the same factor Unity scales
    /// humanoid animation by) and scales the capsule, the camera, step, climb, vault, swim and IK distances,
    /// and movement speeds to match. Two measures: the human scale (leg length) drives strides, speeds and
    /// reach; the model's real height (from its meshes) drives the capsule, crouch and slide heights and the
    /// camera, so a big-headed or long-haired character still fits its capsule. Re-measures when the character
    /// switcher changes the avatar, so a child and a giant can share one Player.
    /// </summary>
    [DefaultExecutionOrder(-200)] // before every component reads its tuning
    [DisallowMultipleComponent]
    public class CharacterDimensions : MonoBehaviour
    {
        /// <summary>Animator.humanScale of the bundled X Bot, the body the kit is tuned for.</summary>
        public const float XBotHumanScale = 1.050076f;
        /// <summary>Height of the bundled X Bot as <see cref="MeasureHeight"/> reads it.</summary>
        public const float XBotHeight = 1.8089f;

        [Tooltip("Human scale of the body the tuning was made for (X Bot)")]
        public float ReferenceHumanScale = XBotHumanScale;
        [Tooltip("Measure the avatar and scale to it. Off = use Scale Multiplier alone")]
        public bool AutoMeasure = true;
        [Tooltip("Extra factor on top of the measured scale")]
        public float ScaleMultiplier = 1f;

        /// <summary>Current size relative to the reference body.</summary>
        public float Scale { get; private set; } = 1f;
        /// <summary>Current height relative to the reference body (capsule, camera).</summary>
        public float HeightScale { get; private set; } = 1f;
        /// <summary>The character's measured height in metres (0 when it has no meshes).</summary>
        public float Height { get; private set; }
        public event System.Action<float> ScaleChanged;

        private Animator _animator;
        private Avatar _measuredAvatar;
        private bool _applied;

        // The capsule and camera socket as authored (for the reference body), captured on first apply.
        private bool _captured;
        private float _radius, _stepOffset, _skinWidth;
        private Vector3 _cameraRoot;
        private Transform _cameraRootTransform;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            Measure(force: true);
        }

        private void LateUpdate()
        {
            if (_animator != null && _animator.avatar != _measuredAvatar) Measure(force: false);
        }

        /// <summary>Measures the avatar now and applies the result if it changed.</summary>
        public void Measure(bool force)
        {
            _measuredAvatar = _animator != null ? _animator.avatar : null;
            float s = 1f;
            if (AutoMeasure && _animator != null && _animator.isHuman && ReferenceHumanScale > 0f)
                s = _animator.humanScale / ReferenceHumanScale;
            s = Mathf.Clamp(s * ScaleMultiplier, 0.2f, 5f);
            Height = MeasureHeight(transform);
            float h = AutoMeasure && Height > 0.05f ? Height / XBotHeight : 1f;
            if (Mathf.Abs(h - 1f) < 0.01f) h = 1f; // the reference body stays exactly as tuned
            h = Mathf.Clamp(h * ScaleMultiplier, 0.2f, 5f);
            if (!force && _applied && Mathf.Abs(s - Scale) < 0.001f && Mathf.Abs(h - HeightScale) < 0.001f) return;
            Apply(s, h);
        }

        /// <summary>
        /// Top of the active character's meshes above the root in their bind pose (whatever the current
        /// animation), in metres; 0 when there are none.
        /// </summary>
        public static float MeasureHeight(Transform root)
        {
            float top = 0f;
            bool any = false;
            void Add(Bounds b, Transform space)
            {
                Vector3 c = b.center, e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                    top = Mathf.Max(top, root.InverseTransformPoint(space.TransformPoint(corner)).y);
                    any = true;
                }
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                if (smr.enabled && smr.sharedMesh != null) Add(smr.sharedMesh.bounds, smr.transform);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
            {
                var r = mf.GetComponent<MeshRenderer>();
                if (r != null && r.enabled && mf.sharedMesh != null) Add(mf.sharedMesh.bounds, mf.transform);
            }
            return any ? top : 0f;
        }

        private void Apply(float s, float h)
        {
            Scale = s;
            HeightScale = h;
            _applied = true;
            TryGetComponent<CharacterController>(out var cc);
            if (!_captured)
            {
                _captured = true;
                if (cc != null)
                {
                    _radius = cc.radius;
                    _stepOffset = cc.stepOffset;
                    _skinWidth = cc.skinWidth;
                }
                _cameraRootTransform = transform.Find("CameraRoot");
                if (_cameraRootTransform != null) _cameraRoot = _cameraRootTransform.localPosition;
            }

            if (cc != null)
            {
                cc.radius = _radius * s;
                cc.stepOffset = _stepOffset * s;
                cc.skinWidth = _skinWidth * s;
                // Height and centre follow ThirdPersonController.StandingHeight, scaled below.
            }

            foreach (var c in GetComponents<MonoBehaviour>())
            {
                if (c is IBodyScaled scaled) scaled.ApplyBodyScale(s);
                if (c is IBodyHeightScaled tall) tall.ApplyBodyHeight(h);
            }

            var tpc = GetComponent<ThirdPersonController>();
            if (cc != null && tpc != null)
            {
                cc.height = tpc.StandingHeight;
                cc.center = new Vector3(0f, cc.height * 0.5f, 0f);
            }

            if (_cameraRootTransform != null) _cameraRootTransform.localPosition = _cameraRoot * h;

            // The camera frames the whole body, so it follows the height.
            foreach (var cam in FindObjectsByType<ThirdPersonCamera>(FindObjectsInactive.Include))
                if (cam.Target != null && cam.Target.IsChildOf(transform)) cam.ApplyBodyScale(h);

            ScaleChanged?.Invoke(s);
        }
    }
}
