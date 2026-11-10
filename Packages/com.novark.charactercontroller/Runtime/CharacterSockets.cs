using System;
using System.Collections.Generic;
using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Named attachment points on the skeleton (hands, back, hips, head) for weapons, tools, backpacks or
    /// hats. Sockets are defined by humanoid bone, so they work on any character, and attached items follow
    /// when the character switcher changes the body.
    /// <code>GetComponent&lt;CharacterSockets&gt;().Attach(sword, "RightHand");</code>
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterSockets : MonoBehaviour
    {
        [Serializable]
        public class Socket
        {
            public string Name;
            public HumanBodyBones Bone;
            [Tooltip("Offset from the bone, in the bone's space (scaled with the body)")]
            public Vector3 Position;
            public Vector3 Rotation;
        }

        public List<Socket> Sockets = new List<Socket>
        {
            new Socket { Name = "RightHand", Bone = HumanBodyBones.RightHand, Position = new Vector3(0f, 0.08f, 0.02f) },
            new Socket { Name = "LeftHand", Bone = HumanBodyBones.LeftHand, Position = new Vector3(0f, 0.08f, 0.02f) },
            new Socket { Name = "Back", Bone = HumanBodyBones.UpperChest, Position = new Vector3(0f, 0f, -0.18f) },
            new Socket { Name = "Hips", Bone = HumanBodyBones.Hips, Position = new Vector3(0.18f, 0f, 0f) },
            new Socket { Name = "Head", Bone = HumanBodyBones.Head, Position = new Vector3(0f, 0.12f, 0f) },
        };

        private readonly Dictionary<GameObject, string> _attached = new Dictionary<GameObject, string>();
        private Animator _animator;
        private Avatar _avatar;

        public IReadOnlyDictionary<GameObject, string> Attached => _attached;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _avatar = _animator != null ? _animator.avatar : null;
        }

        private void LateUpdate()
        {
            // The switcher swapped the body: move everything onto the new skeleton.
            if (_animator == null || _animator.avatar == _avatar) return;
            _avatar = _animator.avatar;
            foreach (var pair in new List<KeyValuePair<GameObject, string>>(_attached))
                if (pair.Key != null) Place(pair.Key, pair.Value);
        }

        /// <summary>Parents the item to the socket (keeps its own local offset under the socket). Returns the socket transform.</summary>
        public Transform Attach(GameObject item, string socketName)
        {
            if (item == null) return null;
            var t = Place(item, socketName);
            if (t != null) _attached[item] = socketName;
            return t;
        }

        public void Detach(GameObject item, bool keepWorldPosition = true)
        {
            if (item == null || !_attached.Remove(item)) return;
            item.transform.SetParent(null, keepWorldPosition);
        }

        public void DetachAll()
        {
            foreach (var item in new List<GameObject>(_attached.Keys)) Detach(item);
        }

        /// <summary>The socket's anchor transform on the current body, created on first use.</summary>
        public Transform SocketTransform(string socketName)
        {
            var socket = Sockets.Find(s => s.Name == socketName);
            if (socket == null || _animator == null || !_animator.isHuman) return null;
            var bone = _animator.GetBoneTransform(socket.Bone);
            if (bone == null) return null;
            string anchorName = "Socket_" + socket.Name;
            var anchor = bone.Find(anchorName);
            if (anchor == null)
            {
                anchor = new GameObject(anchorName).transform;
                anchor.SetParent(bone, false);
            }
            float scale = TryGetComponent<CharacterDimensions>(out var d) ? d.Scale : 1f;
            // Bone space may be scaled by the model's import; keep the offset in metres.
            Vector3 lossy = bone.lossyScale;
            anchor.localPosition = new Vector3(
                socket.Position.x * scale / Mathf.Max(1e-4f, lossy.x),
                socket.Position.y * scale / Mathf.Max(1e-4f, lossy.y),
                socket.Position.z * scale / Mathf.Max(1e-4f, lossy.z));
            anchor.localRotation = Quaternion.Euler(socket.Rotation);
            return anchor;
        }

        private Transform Place(GameObject item, string socketName)
        {
            var anchor = SocketTransform(socketName);
            if (anchor == null)
            {
                Debug.LogWarning($"[{nameof(CharacterSockets)}] No socket '{socketName}' on this character.", this);
                return null;
            }
            item.transform.SetParent(anchor, false);
            return anchor;
        }
    }
}
