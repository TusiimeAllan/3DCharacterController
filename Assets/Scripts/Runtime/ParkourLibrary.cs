using System;
using System.Collections.Generic;
using UnityEngine;

namespace CharacterControllerSystem
{
    public enum ParkourKind
    {
        /// <summary>Jump over something low and thin, landing on the far side.</summary>
        Hurdle,
        /// <summary>Climb onto a ledge of about 0.6 to 1.75 m.</summary>
        ClimbMedium,
        /// <summary>Climb onto a ledge of about 1.75 to 2.5 m.</summary>
        ClimbHigh,
        /// <summary>Hop down from a ledge of about 0.6 to 1.5 m.</summary>
        DropLow,
        /// <summary>Jump down from a ledge of about 1.5 to 3.2 m and roll.</summary>
        DropHigh,
        /// <summary>Slide along the ground (sprint, then crouch).</summary>
        Slide,
    }

    /// <summary>
    /// One parkour animation, analysed in the Editor (ParkourBuilder): the part of the clip where the
    /// move happens and the path its root takes, so the capsule can follow the same path scaled to the
    /// real obstacle.
    /// </summary>
    [Serializable]
    public class ParkourMove
    {
        public string Name;
        public ParkourKind Kind;
        public AnimationClip Clip;
        [Tooltip("Animator state that plays this move (base layer)")]
        public string StateName;
        [Tooltip("Seconds into the clip where the move starts and ends")]
        public float WindowStart, WindowEnd;
        [Tooltip("Rise (climb), drop (drop) or jump height (hurdle) of the recorded move, metres")]
        public float Height;
        [Tooltip("Horizontal distance from the window start to the obstacle (wall face, ledge edge or hurdle centre)")]
        public float ContactDistance;
        [Tooltip("Root path over the window, evenly spaced in time: x = forward, y = up, relative to the start")]
        public Vector2[] Path;

        public float Duration => WindowEnd - WindowStart;

        /// <summary>Root offset at progress p (0..1) through the move.</summary>
        public Vector2 Sample(float p)
        {
            if (Path == null || Path.Length == 0) return Vector2.zero;
            float f = Mathf.Clamp01(p) * (Path.Length - 1);
            int i = Mathf.Min((int)f, Path.Length - 2);
            return i < 0 ? Path[0] : Vector2.Lerp(Path[i], Path[i + 1], f - i);
        }
    }

    [CreateAssetMenu(fileName = "ParkourLibrary", menuName = "Character Controller/Parkour Library")]
    public class ParkourLibrary : ScriptableObject
    {
        public List<ParkourMove> Moves = new List<ParkourMove>();
    }
}
