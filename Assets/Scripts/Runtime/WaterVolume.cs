using System.Collections.Generic;
using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Swimmable water: a box (trigger) whose top is the water surface. SwimController starts swimming once
    /// the water is deep enough, ParkourController treats deep water as something to fall into.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class WaterVolume : MonoBehaviour
    {
        public static readonly List<WaterVolume> All = new List<WaterVolume>();

        private BoxCollider _box;

        public Bounds Bounds
        {
            get
            {
                if (_box == null) _box = GetComponent<BoxCollider>();
                return _box.bounds;
            }
        }
        public float SurfaceY => Bounds.max.y;

        private void Awake()
        {
            _box = GetComponent<BoxCollider>();
            _box.isTrigger = true;
        }

        private void OnEnable()
        {
            if (_box == null) Awake();
            All.Add(this);
        }

        private void OnDisable() => All.Remove(this);

        /// <summary>True when the point is inside the water horizontally and below its surface.</summary>
        public bool Contains(Vector3 point)
        {
            var b = Bounds;
            return point.x >= b.min.x && point.x <= b.max.x && point.z >= b.min.z && point.z <= b.max.z &&
                   point.y <= b.max.y && point.y >= b.min.y - 0.5f;
        }

        /// <summary>True when the point is over the water's area (ignoring height).</summary>
        public bool ContainsXZ(Vector3 point)
        {
            var b = Bounds;
            return point.x >= b.min.x && point.x <= b.max.x && point.z >= b.min.z && point.z <= b.max.z;
        }

        /// <summary>The water at this point, if any.</summary>
        public static WaterVolume At(Vector3 point)
        {
            foreach (var w in All)
                if (w != null && w.Contains(point)) return w;
            return null;
        }

        /// <summary>True when the point lies under at least this much water.</summary>
        public static bool IsDeep(Vector3 point, float depth = 1f)
        {
            var w = At(point);
            return w != null && w.SurfaceY - point.y >= depth;
        }
    }
}
