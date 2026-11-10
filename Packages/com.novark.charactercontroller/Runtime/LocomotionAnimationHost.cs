using UnityEngine;
using UnityEngine.Animations;

namespace CharacterControllerSystem
{
    /// <summary>
    /// Optional component that takes over ground locomotion animation (for example motion matching) and
    /// hosts the Animator Controller inside its own playable graph. The core components only talk to this
    /// base class, so they work the same with or without a host on the character.
    /// </summary>
    public abstract class LocomotionAnimationHost : MonoBehaviour
    {
        /// <summary>True while the host is driving the animation (it can be toggled off for comparison).</summary>
        public abstract bool UseHost { get; }

        /// <summary>Key name that toggles the host in play mode, for the HUD legend (empty when there is none).</summary>
        public virtual string ToggleKeyName => string.Empty;

        /// <summary>True while the Animator Controller plays a landing or roll (states tagged "Landing").</summary>
        public abstract bool InLandingState { get; }

        /// <summary>The hosted Animator Controller playable, when the host runs it in its own graph.</summary>
        public abstract bool TryGetControllerLayer(out AnimatorControllerPlayable layer);

        /// <summary>
        /// Plays an upper body clip above everything the host outputs (hit reactions).
        /// Returns false when the host has no such layer, so the caller falls back to the controller.
        /// </summary>
        public virtual bool PlayUpperBodyClip(AnimationClip clip, AvatarMask mask, float blendIn) => false;

        /// <summary>Fades out the clip started with <see cref="PlayUpperBodyClip"/>.</summary>
        public virtual void StopUpperBodyClip(float blendOut) { }

        /// <summary>Transform the host reads movement input relative to (normally the camera).</summary>
        public virtual void SetInputReference(Transform reference) { }
    }
}
