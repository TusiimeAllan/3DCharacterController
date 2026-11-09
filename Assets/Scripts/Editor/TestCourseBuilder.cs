using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Builds a ProBuilder test course in the OpenWorld scene. Every station brackets one controller
    /// limit (step offset 0.3 m, slope 45 deg, jump 1.2 m, radius 0.28 m, crouch 1.2 m) with values
    /// below, at and beyond it, so you can see exactly where movement or animation breaks.
    /// Re-running replaces the whole "TestCourse" object.
    /// </summary>
    public static class TestCourseBuilder
    {
        private const string Tag = "[TestCourseBuilder]";
        private const string ScenePath = "Assets/Scenes/OpenWorld.unity";
        private const string ArtRoot = "Assets/Art/TestCourse";
        private const string RootName = "TestCourse";

        private static Transform _root;
        private static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();

        [MenuItem("Tools/Character Controller/Build Test Course (OpenWorld)")]
        public static void Build()
        {
            var previousActive = EditorSceneManager.GetActiveScene();
            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            bool opened = !(scene.IsValid() && scene.isLoaded);
            if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            EditorSceneManager.SetActiveScene(scene);

            try
            {
                foreach (var go in scene.GetRootGameObjects())
                    if (go.name == RootName) Object.DestroyImmediate(go);

                CreateMaterials();
                _root = new GameObject(RootName).transform;

                Bounds? groundBounds = null;
                foreach (var go in scene.GetRootGameObjects())
                {
                    if (go.name != "Ground") continue;
                    var r = go.GetComponent<Renderer>();
                    r.sharedMaterial = Mats["Ground"];
                    groundBounds = r.bounds;
                }
                if (groundBounds.HasValue) BoundaryWalls(groundBounds.Value);
                else Debug.LogWarning($"{Tag} No 'Ground' object found, boundary walls were not added.");

                Curbs(-40f);
                Stairs(-31f);
                Ramps(-22f);
                JumpUp(-13f);
                Gaps(-4f);
                CrouchTunnels(5f);
                Beams(14f);
                Corridors(23f);
                DropTower(32f);
                WallSlide(41f);
                UnevenGround();
                PillarForest();
                MovingPlatforms();
                PushCrates();
                Parkour();
                LedgesAndLadder();
                Pool();

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"{Tag} Test course built in {ScenePath}.");
            }
            finally
            {
                if (opened && previousActive.IsValid() && previousActive.isLoaded)
                {
                    EditorSceneManager.SetActiveScene(previousActive);
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        // ================================================================ Stations (in front of spawn)

        /// <summary>Single curbs around the 0.30 m step offset.</summary>
        private static void Curbs(float x)
        {
            var g = Group("01 Curbs");
            Sign(g, "CURBS", "Step offset is 0.30 m", new Vector3(x, 3.4f, 5f), 0f);
            float[] heights = { 0.1f, 0.2f, 0.3f, 0.35f, 0.4f, 0.5f };
            for (int i = 0; i < heights.Length; i++)
            {
                float z = 10f + i * 4f;
                Box(g, $"Curb_{heights[i]:0.00}", new Vector3(x - 1.5f, 0f, z), new Vector3(x + 1.5f, heights[i], z + 1.5f), "Orange");
                Label(g, $"{heights[i]:0.00} m", new Vector3(x, heights[i] + 0.7f, z + 0.75f), 0f);
            }
        }

        /// <summary>Flights with rising step heights, all about 1.5 m tall, then a drop off the back.</summary>
        private static void Stairs(float x)
        {
            var g = Group("02 Stairs");
            Sign(g, "STAIRS", "Step rise vs 0.30 m step offset", new Vector3(x, 3.4f, 5f), 0f);
            float[] rises = { 0.15f, 0.25f, 0.30f, 0.35f, 0.45f };
            const float width = 1.5f, gap = 0.2f, tread = 0.3f;
            float x0 = x - (rises.Length * width + (rises.Length - 1) * gap) * 0.5f;
            for (int i = 0; i < rises.Length; i++)
            {
                float left = x0 + i * (width + gap);
                int steps = Mathf.Max(1, Mathf.RoundToInt(1.5f / rises[i]));
                var flight = Group($"Flight_{rises[i]:0.00}", g);
                float zEnd = StairFlight(flight, left, 10f, width, rises[i], tread, steps, "Blue");
                Box(flight, "Landing", new Vector3(left, 0f, zEnd), new Vector3(left + width, steps * rises[i], zEnd + 2f), "Blue");
                Label(g, $"{rises[i]:0.00}", new Vector3(left + width * 0.5f, 1.2f, 9.3f), 0f);
            }
        }

        /// <summary>Ramps around the 45 deg slope limit, each climbing 2.5 m to a landing.</summary>
        private static void Ramps(float x)
        {
            var g = Group("03 Ramps");
            Sign(g, "RAMPS", "Slope limit is 45 deg", new Vector3(x, 3.4f, 5f), 0f);
            float[] angles = { 15f, 25f, 35f, 45f, 50f, 60f };
            const float width = 1.2f, gap = 0.15f, height = 2.5f;
            float x0 = x - (angles.Length * width + (angles.Length - 1) * gap) * 0.5f;
            for (int i = 0; i < angles.Length; i++)
            {
                float left = x0 + i * (width + gap);
                float length = height / Mathf.Tan(angles[i] * Mathf.Deg2Rad);
                Wedge(g, $"Ramp_{angles[i]:0}", new Vector3(left, 0f, 10f), width, length, height, "Green");
                Box(g, $"Ramp_{angles[i]:0}_Top", new Vector3(left, 0f, 10f + length), new Vector3(left + width, height, 12f + length), "Green");
                Label(g, $"{angles[i]:0}°", new Vector3(left + width * 0.5f, 1.2f, 9.3f), 0f);
            }
        }

        /// <summary>Boxes to jump onto around the 1.2 m jump height (ledge-grab territory above it).</summary>
        private static void JumpUp(float x)
        {
            var g = Group("04 Jump Up");
            Sign(g, "JUMP UP", "Jump height is 1.2 m", new Vector3(x, 3.4f, 5f), 0f);
            float[] heights = { 0.6f, 0.9f, 1.2f, 1.4f, 1.7f, 2.2f };
            const float width = 1.2f, gap = 0.2f;
            float x0 = x - (heights.Length * width + (heights.Length - 1) * gap) * 0.5f;
            for (int i = 0; i < heights.Length; i++)
            {
                float left = x0 + i * (width + gap);
                Box(g, $"Box_{heights[i]:0.0}", new Vector3(left, 0f, 12f), new Vector3(left + width, heights[i], 14.5f), "Purple");
                Label(g, $"{heights[i]:0.0} m", new Vector3(left + width * 0.5f, heights[i] + 0.6f, 11.6f), 0f);
            }
        }

        /// <summary>Raised runway with widening gaps (a sprint jump covers roughly 4.8 m).</summary>
        private static void Gaps(float x)
        {
            var g = Group("05 Gaps");
            Sign(g, "GAP JUMPS", "Sprint jump reaches about 4.8 m", new Vector3(x, 3.4f, 5f), 0f);
            const float half = 1.5f, h = 1f;
            float rampLen = h / Mathf.Tan(15f * Mathf.Deg2Rad);
            Wedge(g, "Ramp", new Vector3(x - half, 0f, 9f), half * 2f, rampLen, h, "Yellow");
            float z = 9f + rampLen;
            Box(g, "Runway", new Vector3(x - half, 0f, z), new Vector3(x + half, h, z + 6f), "Yellow");
            z += 6f;
            foreach (float gap in new[] { 1f, 2f, 3f, 4f, 5f })
            {
                Label(g, $"{gap:0} m", new Vector3(x, h + 0.9f, z + gap * 0.5f), 0f);
                z += gap;
                Box(g, $"Landing_{gap:0}", new Vector3(x - half, 0f, z), new Vector3(x + half, h, z + 2.5f), "Yellow");
                z += 2.5f;
            }
        }

        /// <summary>Tunnels with lower and lower ceilings (standing 1.8 m, crouched 1.2 m).</summary>
        private static void CrouchTunnels(float x)
        {
            var g = Group("06 Crouch Tunnels");
            Sign(g, "CROUCH TUNNELS", "Stand 1.8 m / crouch 1.2 m", new Vector3(x, 3.4f, 5f), 0f);
            float[] clearances = { 2.0f, 1.6f, 1.3f, 1.1f };
            const float inner = 1.5f, wall = 0.3f, top = 2.4f, z0 = 12f, z1 = 17f;
            float cursor = x - (clearances.Length * inner + (clearances.Length + 1) * wall) * 0.5f;
            for (int i = 0; i <= clearances.Length; i++)
            {
                Box(g, $"Wall_{i}", new Vector3(cursor, 0f, z0), new Vector3(cursor + wall, top, z1), "Red");
                cursor += wall;
                if (i == clearances.Length) break;
                Box(g, $"Roof_{clearances[i]:0.0}", new Vector3(cursor, clearances[i], z0), new Vector3(cursor + inner, top, z1), "Red");
                Label(g, $"{clearances[i]:0.0} m", new Vector3(cursor + inner * 0.5f, top + 0.5f, z0), 0f);
                cursor += inner;
            }
        }

        /// <summary>Raised beams narrower than the 0.56 m capsule.</summary>
        private static void Beams(float x)
        {
            var g = Group("07 Beams");
            Sign(g, "BEAMS", "Capsule is 0.56 m wide", new Vector3(x, 3.4f, 5f), 0f);
            const float h = 1f;
            float rampLen = h / Mathf.Tan(20f * Mathf.Deg2Rad);
            Wedge(g, "Ramp", new Vector3(x - 4f, 0f, 10f - rampLen), 8f, rampLen, h, "Teal");
            Box(g, "Start", new Vector3(x - 4f, 0f, 10f), new Vector3(x + 4f, h, 12f), "Teal");
            float[] widths = { 1.0f, 0.6f, 0.35f };
            for (int i = 0; i < widths.Length; i++)
            {
                float cx = x - 2.5f + i * 2.5f;
                Box(g, $"Beam_{widths[i]:0.00}", new Vector3(cx - widths[i] * 0.5f, h - 0.2f, 12f), new Vector3(cx + widths[i] * 0.5f, h, 22f), "Teal");
                Label(g, $"{widths[i]:0.00} m", new Vector3(cx, h + 0.8f, 12.3f), 0f);
            }
            Box(g, "End", new Vector3(x - 4f, 0f, 22f), new Vector3(x + 4f, h, 24f), "Teal");
        }

        /// <summary>Corridors down to the capsule width, one roofed, for wall contact and camera collision.</summary>
        private static void Corridors(float x)
        {
            var g = Group("08 Corridors");
            Sign(g, "CORRIDORS", "Wall contact and camera", new Vector3(x, 3.4f, 5f), 0f);
            float[] widths = { 1.2f, 0.8f, 0.6f };
            const float wall = 0.3f, h = 3f, z0 = 12f, z1 = 22f;
            float cursor = x - (widths.Length * 0f + (widths.Length + 1) * wall + 2.6f) * 0.5f;
            for (int i = 0; i <= widths.Length; i++)
            {
                Box(g, $"Wall_{i}", new Vector3(cursor, 0f, z0), new Vector3(cursor + wall, h, z1), "Grey");
                cursor += wall;
                if (i == widths.Length) break;
                Label(g, $"{widths[i]:0.0} m", new Vector3(cursor + widths[i] * 0.5f, h + 0.5f, z0), 0f);
                if (i == 1) Box(g, "Roof", new Vector3(cursor - wall, 2.4f, 15f), new Vector3(cursor + widths[i] + wall, 2.6f, 19f), "Grey");
                cursor += widths[i];
            }
        }

        /// <summary>Stairs up to 8 m, then plateaus stepping down 2 m at a time (or jump off the side).</summary>
        private static void DropTower(float x)
        {
            var g = Group("09 Drop Tower");
            Sign(g, "DROP TOWER", "Falls of 2, 4, 6 and 8 m", new Vector3(x, 3.4f, 5f), 0f);
            float zEnd = StairFlight(Group("Stairs", g), x - 3.5f, 10f, 2f, 0.25f, 0.3f, 32, "Orange");
            float z = zEnd;
            foreach (float h in new[] { 8f, 6f, 4f, 2f })
            {
                float depth = h >= 8f ? 4f : 3f;
                Box(g, $"Plateau_{h:0}", new Vector3(x - 3.5f, 0f, z), new Vector3(x + 3.5f, h, z + depth), "Orange");
                Label(g, $"{h:0} m", new Vector3(x + 1.5f, h + 0.8f, z + 0.5f), 0f);
                z += depth;
            }
        }

        /// <summary>Walls met head on, at 60 deg and at 30 deg, to judge sliding along walls.</summary>
        private static void WallSlide(float x)
        {
            var g = Group("10 Wall Slide");
            Sign(g, "WALL SLIDE", "Run into each wall", new Vector3(x, 3.4f, 5f), 0f);
            float[] yaw = { 0f, 30f, 60f };
            string[] text = { "90°", "60°", "30°" };
            for (int i = 0; i < yaw.Length; i++)
            {
                var pb = Box(g, $"Wall_{text[i]}", new Vector3(-2.5f, -1.5f, -0.15f), new Vector3(2.5f, 1.5f, 0.15f), "Grey");
                pb.transform.SetPositionAndRotation(new Vector3(x, 1.5f, 14f + i * 8f), Quaternion.Euler(0f, yaw[i], 0f));
                Label(g, text[i], new Vector3(x, 3.6f, 14f + i * 8f), 0f);
            }
        }

        // ================================================================ Stations (behind spawn)

        private static void UnevenGround()
        {
            var g = Group("11 Uneven Ground");
            Sign(g, "UNEVEN GROUND", "Foot placement and ground check", new Vector3(-25f, 3.4f, -6f), 180f);
            var rng = new System.Random(42);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            for (int i = 0; i < 110; i++)
            {
                var size = new Vector3(R(0.6f, 2.2f), R(0.06f, 0.4f), R(0.6f, 2.2f));
                var pb = Box(g, $"Rock_{i}", -size * 0.5f, size * 0.5f, i % 3 == 0 ? "Brown" : "Sand");
                pb.transform.SetPositionAndRotation(
                    new Vector3(R(-35f, -15f), size.y * 0.35f, R(-40f, -12f)),
                    Quaternion.Euler(R(-9f, 9f), R(0f, 360f), R(-9f, 9f)));
            }
        }

        private static void PillarForest()
        {
            var g = Group("12 Pillar Forest");
            Sign(g, "PILLAR FOREST", "Steering and camera collision", new Vector3(0f, 3.4f, -6f), 180f);
            var rng = new System.Random(7);
            for (float px = -8f; px <= 8f; px += 2.4f)
            for (float pz = -12f; pz >= -40f; pz -= 2.4f)
            {
                var pb = ShapeGenerator.GenerateCylinder(PivotLocation.Center, 12, 0.35f, 4f, 0);
                pb.name = "Pillar";
                Finish(pb, g, "Grey", convex: false);
                float jx = (float)rng.NextDouble() * 1.2f - 0.6f, jz = (float)rng.NextDouble() * 1.2f - 0.6f;
                pb.transform.position = new Vector3(px + jx, 2f, pz + jz);
            }
        }

        private static void MovingPlatforms()
        {
            var g = Group("13 Moving Platforms");
            Sign(g, "MOVING PLATFORMS", "Does the character ride along?", new Vector3(27f, 3.4f, -6f), 180f);

            // Elevator to a 3.3 m ledge.
            Mover(g, "Elevator", new Vector3(20f, 0.15f, -20f), new Vector3(3f, 0.3f, 3f), new Vector3(0f, 3f, 0f), 3f, 1.5f, 0f);
            Box(g, "ElevatorLedge", new Vector3(18.5f, 0f, -24.5f), new Vector3(21.5f, 3.3f, -21.5f), "Blue");
            Label(g, "Elevator", new Vector3(20f, 1.4f, -17.8f), 180f);

            // Shuttle between two 1 m platforms.
            Box(g, "ShuttleA", new Vector3(25f, 0f, -22f), new Vector3(28f, 1f, -18f), "Blue");
            Box(g, "ShuttleB", new Vector3(36.8f, 0f, -22f), new Vector3(39.8f, 1f, -18f), "Blue");
            Mover(g, "Shuttle", new Vector3(29.4f, 0.85f, -20f), new Vector3(2.5f, 0.3f, 2.5f), new Vector3(6f, 0f, 0f), 3f, 1f, 0f);
            Label(g, "Shuttle", new Vector3(32.4f, 2f, -17.8f), 180f);

            // Turntable.
            var disc = ShapeGenerator.GenerateCylinder(PivotLocation.Center, 32, 3f, 0.3f, 0);
            disc.name = "Turntable";
            Finish(disc, g, "Yellow", convex: true);
            disc.transform.position = new Vector3(27f, 0.15f, -32f);
            AddMover(disc.gameObject, Vector3.zero, 0f, 0f, 30f);
            Label(g, "Turntable", new Vector3(27f, 1.6f, -28.6f), 180f);
        }

        private static void PushCrates()
        {
            var g = Group("14 Push Crates");
            Sign(g, "PUSH CRATES", "Walk into a crate. Max push mass 150 kg", new Vector3(13f, 3.4f, -6f), 180f);
            var crates = new (float size, float mass, string mat)[] { (0.8f, 30f, "Yellow"), (1.2f, 80f, "Orange"), (1.8f, 400f, "Red") };
            for (int i = 0; i < crates.Length; i++)
            {
                var (size, mass, mat) = crates[i];
                var pb = ShapeGenerator.GenerateCube(PivotLocation.Center, Vector3.one * size);
                pb.name = $"Crate_{mass:0}kg";
                Finish(pb, g, mat, convex: true);
                float x = 10.5f + i * 2.7f;
                pb.transform.position = new Vector3(x, size * 0.5f, -14f);
                var rb = pb.gameObject.AddComponent<Rigidbody>();
                rb.mass = mass;
                rb.linearDamping = 1f;
                rb.angularDamping = 2f;
                rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                Label(g, mass > 150f ? $"{mass:0} kg (too heavy)" : $"{mass:0} kg", new Vector3(x, size + 0.6f, -14f), 180f);
            }
        }

        /// <summary>
        /// Lanes to run along (towards -z): hurdles, ledges to climb at medium and high heights, and drops
        /// back down from each platform.
        /// </summary>
        private static void Parkour()
        {
            var g = Group("15 Parkour");
            Sign(g, "PARKOUR", "Run at it: hurdle, climb, drop (P toggles)", new Vector3(13f, 3.4f, -18f), 180f);
            const float z0 = -22f;
            // Lane 1: hurdles of 0.5 and 0.8 m.
            float x = 9.5f;
            Box(g, "Hurdle_0.5", new Vector3(x - 1f, 0f, z0 - 4.4f), new Vector3(x + 1f, 0.5f, z0 - 4f), "Yellow");
            Label(g, "0.5 m", new Vector3(x, 1.1f, z0 - 4.2f), 180f);
            Box(g, "Hurdle_0.8", new Vector3(x - 1f, 0f, z0 - 11.4f), new Vector3(x + 1f, 0.8f, z0 - 11f), "Yellow");
            Label(g, "0.8 m", new Vector3(x, 1.4f, z0 - 11.2f), 180f);
            // Lanes 2 to 4: a platform to climb onto and drop off the far end.
            var lanes = new (float x, float h, string mat)[] { (12f, 1.1f, "Orange"), (14.5f, 1.6f, "Purple"), (17f, 2.2f, "Red") };
            foreach (var (lx, h, mat) in lanes)
            {
                Box(g, $"Platform_{h:0.0}", new Vector3(lx - 1f, 0f, z0 - 10f), new Vector3(lx + 1f, h, z0 - 5f), mat);
                Label(g, $"{h:0.0} m", new Vector3(lx, h + 0.6f, z0 - 5f), 180f);
            }
        }

        /// <summary>
        /// A 3 m wall to jump at, grab and shimmy along (too tall to vault), and a 4 m platform with a ladder.
        /// Run towards -z.
        /// </summary>
        private static void LedgesAndLadder()
        {
            var g = Group("16 Ledges and Ladder");
            Sign(g, "LEDGES + LADDER", "Jump at the wall to grab. A/D shimmy, W climb, S drop", new Vector3(-44f, 3.4f, -10f), 180f);
            Box(g, "LedgeWall_3.0", new Vector3(-47f, 0f, -21f), new Vector3(-41f, 3f, -19f), "Blue");
            Label(g, "3.0 m", new Vector3(-44f, 3.6f, -19f), 180f);

            Box(g, "LadderPlatform_4.0", new Vector3(-47f, 0f, -42f), new Vector3(-41f, 4f, -36f), "Purple");
            Label(g, "4.0 m", new Vector3(-42f, 4.6f, -36f), 180f);
            var ladderGo = new GameObject("Ladder");
            ladderGo.transform.SetParent(g, false);
            ladderGo.transform.position = new Vector3(-44f, 0f, -35.8f); // stands 0.2 m off the wall
            ladderGo.transform.rotation = Quaternion.LookRotation(Vector3.forward); // climbing face points +z
            var ladder = ladderGo.AddComponent<CharacterControllerSystem.Ladder>();
            ladder.Height = 4f;
            ladder.HalfWidth = 0.35f;
            // Rails and rungs are decoration only: no colliders, so they don't catch the capsule.
            void Part(string name, Vector3 min, Vector3 max)
            {
                var part = Box(g, name, min, max, "Grey");
                var col = part.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
                part.transform.SetParent(ladderGo.transform, true);
            }
            Part("Rail_L", new Vector3(-44.4f, 0f, -36f), new Vector3(-44.33f, 4.4f, -35.76f));
            Part("Rail_R", new Vector3(-43.67f, 0f, -36f), new Vector3(-43.6f, 4.4f, -35.76f));
            for (int i = 1; i <= 13; i++)
                Part($"Rung_{i}", new Vector3(-44.33f, i * 0.3f, -35.82f), new Vector3(-43.67f, i * 0.3f + 0.04f, -35.78f));
        }

        /// <summary>
        /// A raised pool, 1.8 m of water in 2 m walls. Climb onto the wide west deck (run at it heading +x) and
        /// walk off into the water; swim; walk out up the shallow ramp at the north-east, or Jump at a wall to
        /// climb out.
        /// </summary>
        private static void Pool()
        {
            var g = Group("17 Pool");
            Sign(g, "POOL", "Climb the deck, walk in. Jump at a wall to climb out", new Vector3(21.5f, 3.4f, -48.3f), -90f);
            const float h = 2f, surface = 1.8f;
            Box(g, "Deck_West", new Vector3(24f, 0f, -47.5f), new Vector3(25f, h, -38.5f), "Blue");
            Box(g, "Wall_East", new Vector3(35f, 0f, -47.5f), new Vector3(35.5f, h, -38.5f), "Blue");
            Box(g, "Wall_South", new Vector3(25f, 0f, -47.5f), new Vector3(35f, h, -47f), "Blue");
            Box(g, "Wall_North", new Vector3(25f, 0f, -39f), new Vector3(35f, h, -38.5f), "Blue");
            Wedge(g, "Beach", new Vector3(31f, 0f, -44.5f), 4f, 5.5f, 1.9f, "Sand");

            var water = GameObject.CreatePrimitive(PrimitiveType.Cube);
            water.name = "Water";
            water.transform.SetParent(g, false);
            water.transform.position = new Vector3(30f, surface * 0.5f, -43f);
            water.transform.localScale = new Vector3(10f, surface, 8f);
            water.GetComponent<BoxCollider>().isTrigger = true;
            water.GetComponent<Renderer>().sharedMaterial = WaterMaterial();
            water.AddComponent<CharacterControllerSystem.WaterVolume>();
        }

        private static Material WaterMaterial()
        {
            string path = $"{ArtRoot}/Materials/Course_Water.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                EnsureFolder($"{ArtRoot}/Materials");
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            // URP Lit, transparent alpha blend.
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetColor("_BaseColor", new Color(0.15f, 0.45f, 0.75f, 0.45f));
            mat.SetFloat("_Smoothness", 0.9f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Adds only the pool station to the loaded OpenWorld scene, without saving it.</summary>
        [MenuItem("Tools/Character Controller/Add Pool Station (OpenWorld)")]
        public static void AddPoolOnly()
        {
            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"{Tag} Open {ScenePath} first.");
                return;
            }
            GameObject root = null;
            foreach (var go in scene.GetRootGameObjects()) if (go.name == RootName) root = go;
            if (root == null)
            {
                Debug.LogError($"{Tag} No {RootName} in the scene, run Build Test Course instead.");
                return;
            }
            var old = root.transform.Find("17 Pool");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            CreateMaterials();
            _root = root.transform;
            Pool();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary>Adds only the ledge and ladder station to the loaded OpenWorld scene, without saving it.</summary>
        [MenuItem("Tools/Character Controller/Add Ledge and Ladder Station (OpenWorld)")]
        public static void AddClimbStationOnly()
        {
            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"{Tag} Open {ScenePath} first.");
                return;
            }
            GameObject root = null;
            foreach (var go in scene.GetRootGameObjects()) if (go.name == RootName) root = go;
            if (root == null)
            {
                Debug.LogError($"{Tag} No {RootName} in the scene, run Build Test Course instead.");
                return;
            }
            var old = root.transform.Find("16 Ledges and Ladder");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            CreateMaterials();
            _root = root.transform;
            LedgesAndLadder();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary>Adds only the parkour station to the loaded OpenWorld scene, without saving it.</summary>
        [MenuItem("Tools/Character Controller/Add Parkour Station (OpenWorld)")]
        public static void AddParkourOnly()
        {
            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"{Tag} Open {ScenePath} first.");
                return;
            }
            GameObject root = null;
            foreach (var go in scene.GetRootGameObjects()) if (go.name == RootName) root = go;
            if (root == null)
            {
                Debug.LogError($"{Tag} No {RootName} in the scene, run Build Test Course instead.");
                return;
            }
            var old = root.transform.Find("15 Parkour");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            CreateMaterials();
            _root = root.transform;
            Parkour();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary>Adds only the push crate station to the loaded OpenWorld scene, without saving it.</summary>
        [MenuItem("Tools/Character Controller/Add Push Crates (OpenWorld)")]
        public static void AddPushCratesOnly()
        {
            var scene = EditorSceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"{Tag} Open {ScenePath} first.");
                return;
            }
            GameObject root = null;
            foreach (var go in scene.GetRootGameObjects()) if (go.name == RootName) root = go;
            if (root == null)
            {
                Debug.LogError($"{Tag} No {RootName} in the scene, run Build Test Course instead.");
                return;
            }
            var old = root.transform.Find("14 Push Crates");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            CreateMaterials();
            _root = root.transform;
            PushCrates();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        // ================================================================ Boundary

        /// <summary>
        /// Invisible walls (colliders only) along every edge of the ground, tall enough that even a
        /// jump from the drop tower cannot clear them, so nothing can leave the map and fall forever.
        /// </summary>
        private static void BoundaryWalls(Bounds ground)
        {
            var g = Group("00 Boundary");
            const float height = 30f, thickness = 1f;
            float y = ground.max.y + height * 0.5f;
            float spanX = ground.size.x + thickness * 2f, spanZ = ground.size.z + thickness * 2f;

            void Wall(string name, Vector3 centre, Vector3 size)
            {
                var go = new GameObject(name);
                go.transform.SetParent(g, false);
                go.transform.position = centre;
                go.AddComponent<BoxCollider>().size = size;
            }

            // Walls sit just outside the ground so the full surface stays walkable.
            Wall("Boundary_North", new Vector3(ground.center.x, y, ground.max.z + thickness * 0.5f), new Vector3(spanX, height, thickness));
            Wall("Boundary_South", new Vector3(ground.center.x, y, ground.min.z - thickness * 0.5f), new Vector3(spanX, height, thickness));
            Wall("Boundary_East", new Vector3(ground.max.x + thickness * 0.5f, y, ground.center.z), new Vector3(thickness, height, spanZ));
            Wall("Boundary_West", new Vector3(ground.min.x - thickness * 0.5f, y, ground.center.z), new Vector3(thickness, height, spanZ));
        }

        // ================================================================ Geometry helpers

        private static Transform Group(string name, Transform parent = null)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent != null ? parent : _root, false);
            return t;
        }

        /// <summary>Axis-aligned ProBuilder box from its min and max corners.</summary>
        private static ProBuilderMesh Box(Transform parent, string name, Vector3 min, Vector3 max, string mat)
        {
            var size = max - min;
            var pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
            pb.name = name;
            Finish(pb, parent, mat, convex: false);
            pb.transform.position = (min + max) * 0.5f;
            return pb;
        }

        /// <summary>Ramp rising towards +z: low edge at origin.z, high edge (height) at origin.z + length.</summary>
        private static ProBuilderMesh Wedge(Transform parent, string name, Vector3 origin, float width, float length,
            float height, string mat)
        {
            Vector3 a = new Vector3(0, 0, 0), b = new Vector3(width, 0, 0), c = new Vector3(width, 0, length),
                d = new Vector3(0, 0, length), e = new Vector3(0, height, length), f = new Vector3(width, height, length);
            var quads = new[] { new[] { a, b, c, d }, new[] { a, b, f, e }, new[] { d, c, f, e } };
            var tris = new[] { new[] { a, d, e }, new[] { b, c, f } };

            var positions = new List<Vector3>();
            var faces = new List<Face>();
            foreach (var q in quads)
            {
                int i = positions.Count;
                positions.AddRange(q);
                faces.Add(new Face(new[] { i, i + 1, i + 2, i, i + 2, i + 3 }));
            }
            foreach (var t in tris)
            {
                int i = positions.Count;
                positions.AddRange(t);
                faces.Add(new Face(new[] { i, i + 1, i + 2 }));
            }

            // Flip any face whose winding points into the solid.
            var centre = new Vector3(width * 0.5f, height * 0.3f, length * 0.6f);
            foreach (var face in faces)
            {
                var idx = face.indexes;
                Vector3 p0 = positions[idx[0]], p1 = positions[idx[1]], p2 = positions[idx[2]];
                var n = Vector3.Cross(p1 - p0, p2 - p0);
                var fc = Vector3.zero;
                foreach (var k in face.distinctIndexes) fc += positions[k];
                fc /= face.distinctIndexes.Count;
                if (Vector3.Dot(n, fc - centre) < 0f) face.Reverse();
            }

            var pb = ProBuilderMesh.Create(positions, faces);
            pb.name = name;
            Finish(pb, parent, mat, convex: false);
            pb.transform.position = origin;
            return pb;
        }

        /// <summary>A flight of solid steps rising towards +z. Returns the z where the flight ends.</summary>
        private static float StairFlight(Transform parent, float left, float z0, float width, float rise, float tread,
            int steps, string mat)
        {
            for (int s = 0; s < steps; s++)
            {
                float z = z0 + s * tread;
                Box(parent, $"Step_{s}", new Vector3(left, 0f, z), new Vector3(left + width, (s + 1) * rise, z + tread), mat);
            }
            return z0 + steps * tread;
        }

        private static void Finish(ProBuilderMesh pb, Transform parent, string mat, bool convex)
        {
            pb.transform.SetParent(parent, true);
            pb.SetMaterial(pb.faces, Mats[mat]);
            pb.ToMesh();
            pb.Refresh();
            var mc = pb.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = pb.GetComponent<MeshFilter>().sharedMesh;
            mc.convex = convex;
            GameObjectUtility.SetStaticEditorFlags(pb.gameObject, convex ? 0 : StaticEditorFlags.BatchingStatic);
        }

        private static void Mover(Transform parent, string name, Vector3 centre, Vector3 size, Vector3 offset,
            float duration, float pause, float spin)
        {
            var pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
            pb.name = name;
            Finish(pb, parent, "Yellow", convex: true);
            pb.transform.position = centre;
            AddMover(pb.gameObject, offset, duration, pause, spin);
        }

        private static void AddMover(GameObject go, Vector3 offset, float duration, float pause, float spin)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            var m = go.AddComponent<PlatformMover>();
            m.Offset = offset;
            m.Duration = duration;
            m.Pause = pause;
            m.SpinSpeed = spin;
        }

        // ================================================================ Signs

        private static void Sign(Transform parent, string title, string subtitle, Vector3 pos, float yaw)
        {
            var sign = new GameObject($"Sign_{title}").transform;
            sign.SetParent(parent, false);
            sign.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

            var board = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(4.4f, 1.1f, 0.12f));
            board.name = "Board";
            Finish(board, sign, "Board", convex: false);
            board.transform.localPosition = Vector3.zero;
            board.transform.localRotation = Quaternion.identity;

            var post = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(0.15f, pos.y, 0.15f));
            post.name = "Post";
            Finish(post, sign, "Board", convex: false);
            post.transform.localPosition = new Vector3(0f, -pos.y * 0.5f, 0.1f);
            post.transform.localRotation = Quaternion.identity;

            Text(sign, title, new Vector3(0f, 0.16f, -0.08f), 5f, new Vector2(4.1f, 0.55f), Color.white, FontStyles.Bold);
            Text(sign, subtitle, new Vector3(0f, -0.3f, -0.08f), 2.6f, new Vector2(4.1f, 0.32f),
                new Color(0.75f, 0.88f, 0.95f), FontStyles.Normal);
        }

        private static void Label(Transform parent, string text, Vector3 pos, float yaw)
        {
            var t = new GameObject($"Label_{text}").transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            Text(t, text, Vector3.zero, 4f, new Vector2(3f, 0.6f), new Color(0.06f, 0.08f, 0.1f), FontStyles.Bold);
        }

        private static void Text(Transform parent, string text, Vector3 localPos, float size, Vector2 rect, Color color,
            FontStyles style)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.rectTransform.sizeDelta = rect;
            // Shrink long text to fit its board instead of overflowing it.
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = size * 0.4f;
            tmp.fontSizeMax = size;
        }

        // ================================================================ Materials

        private static void CreateMaterials()
        {
            var grid = GridTexture();
            Mats.Clear();
            void M(string name, Color c, float tiling = 1f)
            {
                string path = $"{ArtRoot}/Materials/Course_{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    EnsureFolder($"{ArtRoot}/Materials");
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.SetTexture("_BaseMap", grid);
                mat.SetTextureScale("_BaseMap", Vector2.one * tiling);
                mat.SetColor("_BaseColor", c);
                mat.SetFloat("_Smoothness", 0.15f);
                EditorUtility.SetDirty(mat);
                Mats[name] = mat;
            }

            M("Ground", new Color(0.62f, 0.64f, 0.66f), 100f);
            M("Orange", new Color(0.95f, 0.6f, 0.3f));
            M("Blue", new Color(0.45f, 0.62f, 0.95f));
            M("Green", new Color(0.5f, 0.82f, 0.5f));
            M("Purple", new Color(0.7f, 0.55f, 0.92f));
            M("Yellow", new Color(0.96f, 0.85f, 0.35f));
            M("Red", new Color(0.92f, 0.45f, 0.42f));
            M("Teal", new Color(0.4f, 0.82f, 0.8f));
            M("Grey", new Color(0.78f, 0.79f, 0.8f));
            M("Brown", new Color(0.6f, 0.47f, 0.36f));
            M("Sand", new Color(0.85f, 0.77f, 0.6f));
            M("Board", new Color(0.12f, 0.15f, 0.18f));
            AssetDatabase.SaveAssets();
        }

        /// <summary>White 1 m grid tile with a faint half-metre line, tinted per material.</summary>
        private static Texture2D GridTexture()
        {
            string path = $"{ArtRoot}/Textures/Grid_1m.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool edge = x < 3 || y < 3 || x >= size - 3 || y >= size - 3;
                bool half = Mathf.Abs(x - size / 2) < 1 || Mathf.Abs(y - size / 2) < 1;
                byte v = edge ? (byte)150 : half ? (byte)215 : (byte)255;
                px[y * size + x] = new Color32(v, v, v, 255);
            }
            tex.SetPixels32(px);
            tex.Apply();

            EnsureFolder($"{ArtRoot}/Textures");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 8;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
