using System.Collections.Generic;
using UnityEngine;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Marks a climbable ladder. The transform sits at the bottom centre of the climbing face, its forward
    /// pointing out of the face (towards the climber) and Height is how far the ladder goes up. ClimbController
    /// attaches to it when the character walks into it.
    /// </summary>
    public class Ladder : MonoBehaviour
    {
        public static readonly List<Ladder> All = new List<Ladder>();

        [Tooltip("Height of the ladder from its bottom to the top it leads onto (m)")]
        public float Height = 4f;
        [Tooltip("Half the ladder's width: how far left or right of its centre the climber may start")]
        public float HalfWidth = 0.4f;

        public Vector3 Bottom => transform.position;
        public Vector3 Top => transform.position + Vector3.up * Height;
        public Vector3 Out => Flat(transform.forward);

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Vector3 right = Vector3.Cross(Vector3.up, Out) * HalfWidth;
            Gizmos.DrawLine(Bottom - right, Top - right);
            Gizmos.DrawLine(Bottom + right, Top + right);
            Gizmos.DrawLine(Bottom + Vector3.up * 0.5f, Bottom + Vector3.up * 0.5f + Out * 0.5f);
        }
    }
}
