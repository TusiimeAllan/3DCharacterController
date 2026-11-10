using MxM;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Motion matching module of the character kit: when the profile enables it, puts MxM on the Player
    /// (MxMAnimator, MxMTrajectoryGenerator and the MotionMatchingLocomotion bridge) with the profile's anim
    /// data and input profiles. ThirdPersonController stays in charge of movement (MxM root motion is off).
    /// </summary>
    [InitializeOnLoad]
    public static class MotionMatchingSetup
    {
        static MotionMatchingSetup() => CharacterKitModules.Register(SetUpPlayer);

        public static string SetUpPlayer(GameObject root, CharacterSetupProfile p, AnimatorController controller)
        {
            var animData = p.MotionMatchingData as MxMAnimData;
            if (!p.EnableMotionMatching || animData == null)
            {
                Remove(root);
                return p.EnableMotionMatching ? "Motion matching skipped: the profile has no MxM anim data." : null;
            }

            var animator = root.GetComponent<Animator>();
            animator.applyRootMotion = false; // the CharacterController moves the character

            // Start from the template's tuned settings, then change only what our controller needs.
            var templateMxm = p.MotionMatchingTemplate != null ? p.MotionMatchingTemplate.GetComponentInChildren<MxMAnimator>(true) : null;
            var templateTraj = p.MotionMatchingTemplate != null ? p.MotionMatchingTemplate.GetComponentInChildren<MxMTrajectoryGenerator>(true) : null;

            bool fresh = root.GetComponent<MxMAnimator>() == null; // an update keeps the tuning on an existing one
            var mxm = Kit.Ensure<MxMAnimator>(root);
            if (fresh && templateMxm != null) EditorUtility.CopySerialized(templateMxm, mxm);
            var so = new SerializedObject(mxm);
            var data = so.FindProperty("m_animData");
            data.arraySize = 1;
            data.GetArrayElementAtIndex(0).objectReferenceValue = animData;
            so.FindProperty("m_rootMotionMode").intValue = (int)EMxMRootMotion.Off;
            so.FindProperty("m_animControllerLayer").intValue = 1;
            so.FindProperty("m_autoCreateAnimatorController").boolValue = false;
            so.FindProperty("m_applyPlayableIK").boolValue = true; // foot grounding and body IK
            ClearForeignReferences(so, root);
            so.ApplyModifiedPropertiesWithoutUndo();

            var walk = p.WalkInputProfile as MxMInputProfile;
            bool freshTraj = root.GetComponent<MxMTrajectoryGenerator>() == null;
            var traj = Kit.Ensure<MxMTrajectoryGenerator>(root);
            if (freshTraj && templateTraj != null) EditorUtility.CopySerialized(templateTraj, traj);
            var st = new SerializedObject(traj);
            st.FindProperty("m_customInput").boolValue = true; // fed by MotionMatchingLocomotion
            st.FindProperty("m_camTransform").objectReferenceValue = null; // set at runtime from the controller
            st.FindProperty("m_mxmInputProfile").objectReferenceValue = walk;
            ClearForeignReferences(st, root);
            st.ApplyModifiedPropertiesWithoutUndo();

            var bridge = Kit.Ensure<MotionMatchingLocomotion>(root);
            bridge.Controller = root.GetComponent<ThirdPersonController>();
            bridge.Inputs = root.GetComponent<PlayerInputs>();
            bridge.Mxm = mxm;
            bridge.Trajectory = traj;
            bridge.WalkProfile = walk;
            bridge.SprintProfile = p.SprintInputProfile as MxMInputProfile;
            bridge.StrafeProfile = p.StrafeInputProfile as MxMInputProfile;
            return "Motion matching set up on the Player.";
        }

        /// <summary>Takes motion matching off a Player (profile turned it off).</summary>
        private static void Remove(GameObject root)
        {
            void Strip<T>() where T : Component
            {
                var c = root.GetComponent<T>();
                if (c == null) return;
                if (Kit.UseUndo) Undo.DestroyObjectImmediate(c);
                else Object.DestroyImmediate(c);
            }
            Strip<MotionMatchingLocomotion>();
            Strip<MxMTrajectoryGenerator>();
            Strip<MxMAnimator>();
        }

        /// <summary>Nulls references copied from the template that point at objects outside our hierarchy.</summary>
        private static void ClearForeignReferences(SerializedObject so, GameObject root)
        {
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                var go = it.objectReferenceValue is Component c ? c.gameObject : it.objectReferenceValue as GameObject;
                if (go != null && !go.transform.IsChildOf(root.transform)) it.objectReferenceValue = null;
            }
        }
    }
}
