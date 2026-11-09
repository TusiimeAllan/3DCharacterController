using System.Linq;
using Transport;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Puts the Transport vehicles in the Showroom: a "Ride" state (seated pose, entered from Any State on
    /// the Riding bool) in the Player Animator Controller, a BikeRider on the showroom's Player and a
    /// VehicleDisplay with every Transport prefab. Safe to re-run.
    /// </summary>
    public static class BikeShowroomBuilder
    {
        private const string Tag = "[BikeShowroomBuilder]";
        private const string ScenePath = "Assets/Scenes/Showroom.unity";
        private const string ControllerPath = "Assets/Art/Animations/PlayerAnimator.controller";
        private const string UalPath = "Assets/Art/Animations/UAL/UAL1_Standard.fbx";
        private const string PrefabFolder = "Assets/Packs/Transport/Prefabs";
        private const string DisplayName = "VehicleDisplay";

        private static readonly (string prefab, string label)[] Vehicles =
        {
            ("Sport_Motorbike", "Sport Motorbike"), ("Vespa_Scooter", "Vespa Scooter"), ("Downhill_MTB", "Downhill MTB"),
            ("BMX_Bike", "BMX"), ("Kids_Bike", "Kids Bike"),
        };

        [MenuItem("Tools/Character Controller/Add Vehicles To Showroom")]
        public static void Build()
        {
            if (!AddRideState()) return;

            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            var player = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<ThirdPersonController>(true)).FirstOrDefault();
            if (player == null)
            {
                Debug.LogError($"{Tag} No Player in {ScenePath}.");
                return;
            }
            var rider = player.GetComponent<BikeRider>() ?? player.gameObject.AddComponent<BikeRider>();
            rider.Controller = player;
            rider.Inputs = player.GetComponent<PlayerInputs>();
            EditorUtility.SetDirty(rider);

            var display = scene.GetRootGameObjects().FirstOrDefault(g => g.name == DisplayName) ?? new GameObject(DisplayName);
            EditorSceneManager.MoveGameObjectToScene(display, scene);
            display.transform.SetPositionAndRotation(new Vector3(0f, 0f, 0.5f), Quaternion.Euler(0f, 45f, 0f));
            var showroom = display.GetComponent<BikeShowroom>() ?? display.AddComponent<BikeShowroom>();
            showroom.Rider = rider;
            showroom.Hud = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<CharacterHUD>(true)).FirstOrDefault();
            showroom.Vehicles.Clear();
            foreach (var (prefab, label) in Vehicles)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/{prefab}.prefab");
                if (go == null || go.GetComponent<RideableVehicle>() == null)
                {
                    Debug.LogWarning($"{Tag} {prefab} not found under {PrefabFolder}, skipped.");
                    continue;
                }
                showroom.Vehicles.Add(new BikeShowroom.Entry { Name = label, Prefab = go });
            }
            if (showroom.Hud == null) Debug.LogWarning($"{Tag} No CharacterHUD in the scene: the vehicle panel needs it.");
            EditorUtility.SetDirty(showroom);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"{Tag} {showroom.Vehicles.Count} vehicles added to the Showroom.");
        }

        /// <summary>Any State -> Ride while Riding, back to Locomotion when it clears.</summary>
        private static bool AddRideState()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var clip = AssetDatabase.LoadAllAssetRepresentationsAtPath(UalPath).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == "Sitting_Idle_Loop");
            if (controller == null || clip == null)
            {
                Debug.LogError($"{Tag} Controller or the UAL Sitting_Idle_Loop clip is missing, nothing changed.");
                return false;
            }
            if (controller.parameters.All(p => p.name != "Riding"))
                controller.AddParameter("Riding", AnimatorControllerParameterType.Bool);

            var sm = controller.layers[0].stateMachine;
            var loco = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
            if (loco == null)
            {
                Debug.LogError($"{Tag} No Locomotion state.");
                return false;
            }
            var ride = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Ride") ?? sm.AddState("Ride", new Vector3(0f, 560f));
            foreach (var t in ride.transitions.ToArray()) ride.RemoveTransition(t);
            foreach (var t in sm.anyStateTransitions.Where(t => t.destinationState == ride).ToArray()) sm.RemoveAnyStateTransition(t);
            ride.motion = clip;
            ride.tag = "Ride";
            ride.writeDefaultValues = true;

            var enter = sm.AddAnyStateTransition(ride);
            enter.canTransitionToSelf = false;
            enter.hasExitTime = false;
            enter.duration = 0.2f;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "Riding");
            var exit = ride.AddTransition(loco);
            exit.hasExitTime = false;
            exit.duration = 0.25f;
            exit.AddCondition(AnimatorConditionMode.IfNot, 0f, "Riding");

            var layers = controller.layers;
            layers[0].iKPass = true; // BikeRider places hands and feet in OnAnimatorIK
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return true;
        }
    }
}
