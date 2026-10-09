using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    public static class MainCitySceneBuilder
    {
        const string ScenePath = "Assets/Scenes/MainCity.unity";
        const string MaterialFolder = "Assets/Materials/ColorfulTown";
        const int EnvironmentLayer = 6;
        const int Seed = 20261002;

        static Material grassMaterial;
        static Material asphaltMaterial;
        static Material sidewalkMaterial;
        static Material stoneMaterial;
        static Material markingMaterial;
        static Material trunkMaterial;
        static Material windowLitMaterial;
        static Material windowDarkMaterial;
        static Material doorMaterial;
        static Material chimneyMaterial;
        static Material lampMetalMaterial;
        static Material bulbMaterial;
        static Material glowMaterial;
        static Material waterMaterial;
        static Material woodMaterial;
        static Material skyboxMaterial;
        static Material[] facadeMaterials;
        static Material[] roofMaterials;
        static Material[] foliageMaterials;
        static Material[] flowerMaterials;

        [MenuItem("BigWorld/生成主城场景")]
        public static void Build()
        {
            LoadAssets();

            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            RemoveStaleObjects();
            SetupEnvironment();

            GameObject root = new GameObject("CityRoot");
            SetLayer(root);
            System.Random rng = new System.Random(Seed);

            BuildGround(root.transform);
            BuildPortalGate(root.transform);
            BuildFountain(root.transform);
            BuildBenches(root.transform);
            BuildHouses(root.transform, rng);
            BuildClockTower(root.transform);
            BuildPavilion(root.transform, new Vector3(-14f, 0f, 14f), 135f, rng);
            BuildPavilion(root.transform, new Vector3(-14f, 0f, -14f), -135f, rng);
            BuildPavilion(root.transform, new Vector3(14f, 0f, -14f), 45f, rng);
            BuildHedge(root.transform);
            BuildRingGates(root.transform, rng);
            BuildTrees(root.transform, rng);
            BuildBushes(root.transform, rng);
            BuildFlowerPots(root.transform, rng);
            BuildStalls(root.transform, rng);
            BuildLamps(root.transform);
            MarkStatic(root);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            int count = root.GetComponentsInChildren<Transform>(true).Length;
            Debug.Log($"[MainCity] scene saved: {ScenePath}, {count} objects");
        }

        public static void BuildAndGenerateVoxels()
        {
            try
            {
                Build();
                ExportResult result = VoxelSceneExporter.GenerateForScene("MainCity");
                if (!result.Success)
                    throw new InvalidOperationException(result.Message);
                Debug.Log($"[MainCity] 体素导出完成:\n{result.Message}");
                CapturePreview();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MainCity] 批处理失败: {e.Message}\n{e.StackTrace}");
                EditorApplication.Exit(1);
            }
        }

        static void CapturePreview()
        {
            string outputDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            GameObject camGo = new GameObject("PreviewCamera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 900f;

            cam.fieldOfView = 48f;
            camGo.transform.position = new Vector3(24f, 22f, 24f);
            camGo.transform.LookAt(new Vector3(0f, 0f, 2f));
            Capture(cam, Path.Combine(outputDir, "MainCity_Overview.png"), 1600, 900);

            cam.fieldOfView = 55f;
            camGo.transform.position = new Vector3(1.5f, 2.0f, 1.5f);
            camGo.transform.LookAt(new Vector3(8.5f, 2.2f, 8.5f));
            Capture(cam, Path.Combine(outputDir, "MainCity_Portal.png"), 1600, 900);

            cam.fieldOfView = 60f;
            camGo.transform.position = new Vector3(-2.5f, 2.6f, 5.5f);
            camGo.transform.LookAt(new Vector3(-7f, 1.2f, -4f));
            Capture(cam, Path.Combine(outputDir, "MainCity_Plaza.png"), 1600, 900);

            UnityEngine.Object.DestroyImmediate(camGo);
        }

        static void Capture(Camera cam, string path, int width, int height)
        {
            RenderTexture rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        static void LoadAssets()
        {
            grassMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Grass.mat");
            asphaltMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Asphalt.mat");
            sidewalkMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Sidewalk.mat");
            stoneMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Stone.mat");
            markingMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Marking.mat");
            trunkMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Trunk.mat");
            windowLitMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_WindowLit.mat");
            windowDarkMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_WindowDark.mat");
            doorMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Door.mat");
            chimneyMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Chimney.mat");
            lampMetalMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_LampMetal.mat");
            bulbMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Bulb.mat");
            glowMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Glow.mat");
            waterMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Water.mat");
            woodMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Wood.mat");
            skyboxMaterial = LoadAsset<Material>($"{MaterialFolder}/Town_Skybox.mat");
            facadeMaterials = new[]
            {
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Coral.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_SkyBlue.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Cream.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Mint.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Pink.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Apricot.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Lavender.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Facade_Ivory.mat"),
            };
            roofMaterials = new[]
            {
                LoadAsset<Material>($"{MaterialFolder}/Town_Roof_0.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Roof_1.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Roof_2.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Roof_3.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Roof_4.mat"),
            };
            foliageMaterials = new[]
            {
                LoadAsset<Material>($"{MaterialFolder}/Town_Foliage_0.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Foliage_1.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Foliage_2.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Foliage_3.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Foliage_4.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Foliage_5.mat"),
            };
            flowerMaterials = new[]
            {
                LoadAsset<Material>($"{MaterialFolder}/Town_Flower_0.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Flower_1.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Flower_2.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Flower_3.mat"),
                LoadAsset<Material>($"{MaterialFolder}/Town_Flower_4.mat"),
            };
        }

        static T LoadAsset<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException($"缺少资源 {path}，先运行 BigWorld/生成缤纷小镇场景 生成共享材质");
            return asset;
        }

        static void RemoveStaleObjects()
        {
            GameObject stale = GameObject.Find("CityRoot");
            if (stale != null)
                UnityEngine.Object.DestroyImmediate(stale);

            GameObject flatGround = GameObject.Find("FlatGround");
            if (flatGround != null)
                UnityEngine.Object.DestroyImmediate(flatGround);
        }

        static Light SetupEnvironment()
        {
            Light sun = null;
            foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                Light light = go.GetComponent<Light>();
                if (light != null && light.type == LightType.Directional)
                    sun = light;
            }
            if (sun == null)
                throw new InvalidOperationException("MainCity 场景缺少 Directional Light");

            sun.transform.rotation = Quaternion.Euler(32f, 212f, 0f);
            sun.color = new Color(1.00f, 0.87f, 0.72f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;

            RenderSettings.skybox = skyboxMaterial;
            RenderSettings.sun = sun;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.0038f;
            RenderSettings.fogColor = new Color(0.88f, 0.80f, 0.70f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.56f, 0.62f);
            RenderSettings.ambientEquatorColor = new Color(0.75f, 0.68f, 0.60f);
            RenderSettings.ambientGroundColor = new Color(0.32f, 0.29f, 0.27f);
            return sun;
        }

        static void BuildGround(Transform parent)
        {
            CreatePrimitive(parent, PrimitiveType.Plane, "Ground", grassMaterial, Vector3.zero, Quaternion.identity, new Vector3(3.4f, 1f, 3.4f));
            CreatePrimitive(parent, PrimitiveType.Plane, "Courtyard", stoneMaterial, new Vector3(0f, 0.012f, 0f), Quaternion.identity, new Vector3(2.5f, 1f, 2.5f));

            CreatePrimitive(parent, PrimitiveType.Cylinder, "SpawnDisc", stoneMaterial, new Vector3(0f, 0.015f, 0f), Quaternion.identity, new Vector3(6f, 0.015f, 6f));
            CreatePrimitive(parent, PrimitiveType.Cylinder, "SpawnDiscInner", markingMaterial, new Vector3(0f, 0.028f, 0f), Quaternion.identity, new Vector3(2.6f, 0.01f, 2.6f));

            for (int i = 0; i < 4; i++)
            {
                bool xAxis = i < 2;
                float sign = i % 2 == 0 ? 1f : -1f;
                Vector3 pos = xAxis ? new Vector3(sign * 4f, 0.02f, 0f) : new Vector3(0f, 0.02f, sign * 4f);
                Vector3 scale = xAxis ? new Vector3(0.18f, 1f, 0.03f) : new Vector3(0.03f, 1f, 0.18f);
                CreatePrimitive(parent, PrimitiveType.Plane, "PlazaTrim", markingMaterial, pos, Quaternion.identity, scale);
            }

            for (int side = 0; side < 4; side++)
            {
                bool xAxis = side < 2;
                float sign = side % 2 == 0 ? 1f : -1f;
                Vector3 roadPos = xAxis ? new Vector3(sign * 14.2f, 0.016f, 0f) : new Vector3(0f, 0.016f, sign * 14.2f);
                Quaternion roadRot = xAxis ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                CreatePrimitive(parent, PrimitiveType.Plane, "RoadStub", asphaltMaterial, roadPos, roadRot, new Vector3(0.3f, 1f, 0.34f));

                for (int d = 0; d < 3; d++)
                {
                    float along = 13.1f + d * 1.1f;
                    Vector3 dashPos = xAxis ? new Vector3(sign * along, 0.028f, 0f) : new Vector3(0f, 0.028f, sign * along);
                    Quaternion dashRot = xAxis ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                    CreatePrimitive(parent, PrimitiveType.Cube, "Dash", markingMaterial, dashPos, dashRot, new Vector3(0.14f, 0.01f, 0.9f));
                }

                for (int s = 0; s < 2; s++)
                {
                    float lateral = s == 0 ? 2.3f : -2.3f;
                    Vector3 walkPos = xAxis ? new Vector3(sign * 14.2f, 0.02f, lateral) : new Vector3(lateral, 0.02f, sign * 14.2f);
                    Quaternion walkRot = xAxis ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                    CreatePrimitive(parent, PrimitiveType.Plane, "Sidewalk", sidewalkMaterial, walkPos, walkRot, new Vector3(0.16f, 1f, 0.34f));
                }

                Vector3 apronPos = xAxis ? new Vector3(sign * 15.95f, 0.014f, 0f) : new Vector3(0f, 0.014f, sign * 15.95f);
                Quaternion apronRot = xAxis ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                CreatePrimitive(parent, PrimitiveType.Plane, "GateApron", stoneMaterial, apronPos, apronRot, new Vector3(0.2f, 1f, 0.16f));
            }
        }

        static void BuildPortalGate(Transform parent)
        {
            GameObject gate = new GameObject("PortalGate");
            gate.transform.SetParent(parent, false);
            gate.transform.position = new Vector3(8.5f, 0f, 8.5f);
            SetLayer(gate);
            Transform t = gate.transform;

            CreatePrimitive(t, PrimitiveType.Cylinder, "PortalPlate", markingMaterial, new Vector3(0f, 0.02f, 0f), Quaternion.identity, new Vector3(2.6f, 0.02f, 2.6f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "PortalGlowDisc", glowMaterial, new Vector3(0f, 0.045f, 0f), Quaternion.identity, new Vector3(2.1f, 0.01f, 2.1f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarA", stoneMaterial, new Vector3(0f, 1.7f, -1.8f), Quaternion.identity, new Vector3(0.8f, 3.4f, 0.8f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarB", stoneMaterial, new Vector3(0f, 1.7f, 1.8f), Quaternion.identity, new Vector3(0.8f, 3.4f, 0.8f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarCapA", roofMaterials[2], new Vector3(0f, 3.5f, -1.8f), Quaternion.identity, new Vector3(1.0f, 0.22f, 1.0f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarCapB", roofMaterials[2], new Vector3(0f, 3.5f, 1.8f), Quaternion.identity, new Vector3(1.0f, 0.22f, 1.0f));
            CreatePrimitive(t, PrimitiveType.Cube, "Beam", stoneMaterial, new Vector3(0f, 3.85f, 0f), Quaternion.identity, new Vector3(1.0f, 0.5f, 5.2f));
            CreatePrimitive(t, PrimitiveType.Cube, "BeamTrim", roofMaterials[2], new Vector3(0f, 4.14f, 0f), Quaternion.identity, new Vector3(1.1f, 0.1f, 5.4f));
            CreatePrimitive(t, PrimitiveType.Cube, "BannerA", facadeMaterials[1], new Vector3(0f, 3.05f, -1.15f), Quaternion.identity, new Vector3(0.07f, 1.1f, 0.36f));
            CreatePrimitive(t, PrimitiveType.Cube, "BannerB", facadeMaterials[4], new Vector3(0f, 3.05f, 1.15f), Quaternion.identity, new Vector3(0.07f, 1.1f, 0.36f));
            CreatePrimitive(t, PrimitiveType.Sphere, "Crest", glowMaterial, new Vector3(0f, 4.5f, 0f), Quaternion.identity, Vector3.one * 0.44f);
        }

        static void BuildFountain(Transform parent)
        {
            GameObject fountain = new GameObject("Fountain");
            fountain.transform.SetParent(parent, false);
            fountain.transform.position = new Vector3(-8f, 0f, -2f);
            SetLayer(fountain);
            Transform t = fountain.transform;

            CreatePrimitive(t, PrimitiveType.Cylinder, "Pool", stoneMaterial, new Vector3(0f, 0.15f, 0f), Quaternion.identity, new Vector3(4.4f, 0.15f, 4.4f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Water", waterMaterial, new Vector3(0f, 0.20f, 0f), Quaternion.identity, new Vector3(4.0f, 0.10f, 4.0f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Pedestal", stoneMaterial, new Vector3(0f, 0.75f, 0f), Quaternion.identity, new Vector3(0.9f, 0.90f, 0.9f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Bowl", stoneMaterial, new Vector3(0f, 1.70f, 0f), Quaternion.identity, new Vector3(1.8f, 0.15f, 1.8f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "BowlWater", waterMaterial, new Vector3(0f, 1.74f, 0f), Quaternion.identity, new Vector3(1.55f, 0.06f, 1.55f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Spire", stoneMaterial, new Vector3(0f, 2.30f, 0f), Quaternion.identity, new Vector3(0.5f, 0.60f, 0.5f));
            CreatePrimitive(t, PrimitiveType.Sphere, "Orb", waterMaterial, new Vector3(0f, 2.75f, 0f), Quaternion.identity, Vector3.one * 0.5f);
        }

        static void BuildBenches(Transform parent)
        {
            BuildBench(parent, new Vector3(-8f, 0f, 0.95f), 180f);
            BuildBench(parent, new Vector3(-8f, 0f, -4.72f), 0f);
            BuildBench(parent, new Vector3(-11.35f, 0f, -2f), 90f);
            BuildBench(parent, new Vector3(-4.65f, 0f, -2f), -90f);
            BuildBench(parent, new Vector3(2f, 0f, 2f), 225f);
            BuildBench(parent, new Vector3(-2f, 0f, 2f), 135f);
            BuildBench(parent, new Vector3(2f, 0f, -2f), -45f);
            BuildBench(parent, new Vector3(-2f, 0f, -2f), 45f);
        }

        static void BuildBench(Transform parent, Vector3 position, float facingY)
        {
            GameObject bench = new GameObject("Bench");
            bench.transform.SetParent(parent, false);
            bench.transform.position = position;
            bench.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
            SetLayer(bench);
            Transform t = bench.transform;

            CreatePrimitive(t, PrimitiveType.Cube, "Seat", woodMaterial, new Vector3(0f, 0.50f, 0f), Quaternion.identity, new Vector3(1.7f, 0.08f, 0.45f));
            CreatePrimitive(t, PrimitiveType.Cube, "Back", woodMaterial, new Vector3(0f, 0.85f, -0.19f), Quaternion.identity, new Vector3(1.7f, 0.45f, 0.07f));
            CreatePrimitive(t, PrimitiveType.Cube, "LegL", lampMetalMaterial, new Vector3(-0.70f, 0.25f, 0f), Quaternion.identity, new Vector3(0.08f, 0.50f, 0.40f));
            CreatePrimitive(t, PrimitiveType.Cube, "LegR", lampMetalMaterial, new Vector3(0.70f, 0.25f, 0f), Quaternion.identity, new Vector3(0.08f, 0.50f, 0.40f));
        }

        static void BuildHouses(Transform parent, System.Random rng)
        {
            Vector3[] slots =
            {
                new Vector3(-9f, 0f, 14f), new Vector3(9f, 0f, 14f),
                new Vector3(-9f, 0f, -14f), new Vector3(9f, 0f, -14f),
                new Vector3(14f, 0f, 9f), new Vector3(14f, 0f, -9f),
                new Vector3(-14f, 0f, 9f), new Vector3(-14f, 0f, -9f),
            };
            float[] facings = { 180f, 180f, 0f, 0f, -90f, -90f, 90f, 90f };

            for (int i = 0; i < slots.Length; i++)
                BuildHouse(parent, rng, slots[i], facings[i], i);
        }

        static void BuildHouse(Transform parent, System.Random rng, Vector3 position, float facingY, int index)
        {
            GameObject house = new GameObject($"House_{index:00}");
            house.transform.SetParent(parent, false);
            house.transform.position = position;
            house.transform.rotation = Quaternion.Euler(0f, facingY + Range(rng, -2.5f, 2.5f), 0f);
            SetLayer(house);
            Transform t = house.transform;

            float w = Range(rng, 4.2f, 5.2f);
            bool twoFloors = rng.NextDouble() > 0.5;
            float h = twoFloors ? Range(rng, 3.8f, 4.4f) : Range(rng, 2.9f, 3.4f);
            float depth = 1.9f;
            Material facade = facadeMaterials[rng.Next(facadeMaterials.Length)];
            Material trim = roofMaterials[rng.Next(roofMaterials.Length)];

            CreatePrimitive(t, PrimitiveType.Cube, "Facade", facade, new Vector3(0f, h * 0.5f, 0f), Quaternion.identity, new Vector3(w, h, 0.3f));
            CreatePrimitive(t, PrimitiveType.Cube, "ReturnL", facade, new Vector3(-w * 0.5f + 0.15f, h * 0.5f, -0.55f), Quaternion.identity, new Vector3(0.3f, h, 1.1f));
            CreatePrimitive(t, PrimitiveType.Cube, "ReturnR", facade, new Vector3(w * 0.5f - 0.15f, h * 0.5f, -0.55f), Quaternion.identity, new Vector3(0.3f, h, 1.1f));
            CreatePrimitive(t, PrimitiveType.Cube, "Base", stoneMaterial, new Vector3(0f, 0.2f, 0.02f), Quaternion.identity, new Vector3(w + 0.08f, 0.4f, 0.38f));
            CreatePrimitive(t, PrimitiveType.Cube, "RoofSlab", trim, new Vector3(0f, h + 0.11f, -0.35f), Quaternion.identity, new Vector3(w + 0.5f, 0.22f, depth + 0.3f));
            CreatePrimitive(t, PrimitiveType.Cube, "Parapet", trim, new Vector3(0f, h + 0.36f, 0.08f), Quaternion.identity, new Vector3(w + 0.2f, 0.28f, 0.16f));

            if (twoFloors)
                CreatePrimitive(t, PrimitiveType.Cube, "StringCourse", stoneMaterial, new Vector3(0f, 2.55f, 0.03f), Quaternion.identity, new Vector3(w + 0.06f, 0.12f, 0.36f));

            float doorX = w * Range(rng, 0.14f, 0.24f) * (rng.NextDouble() > 0.5 ? 1f : -1f);
            CreatePrimitive(t, PrimitiveType.Cube, "Door", doorMaterial, new Vector3(doorX, 1.02f, 0.14f), Quaternion.identity, new Vector3(0.95f, 2.05f, 0.10f));
            CreatePrimitive(t, PrimitiveType.Cube, "DoorFrame", stoneMaterial, new Vector3(doorX, 1.08f, 0.12f), Quaternion.identity, new Vector3(1.15f, 2.2f, 0.06f));

            float[] windowXs = w >= 4.8f ? new[] { -w * 0.30f, w * 0.30f } : new[] { -w * 0.26f, w * 0.26f };
            float[] floorYs = twoFloors ? new[] { 1.75f, h - 1.05f } : new[] { 1.75f };
            foreach (float y in floorYs)
            {
                foreach (float wx in windowXs)
                {
                    if (Mathf.Abs(wx - doorX) < 0.9f && y < 2.5f)
                        continue;
                    Material win = rng.NextDouble() > 0.4 ? windowLitMaterial : windowDarkMaterial;
                    CreatePrimitive(t, PrimitiveType.Cube, "Window", win, new Vector3(wx, y, 0.14f), Quaternion.identity, new Vector3(0.75f, 1.05f, 0.08f));
                    CreatePrimitive(t, PrimitiveType.Cube, "WindowSill", stoneMaterial, new Vector3(wx, y - 0.62f, 0.15f), Quaternion.identity, new Vector3(0.9f, 0.08f, 0.14f));
                }
            }

            if (rng.NextDouble() > 0.55)
                CreatePrimitive(t, PrimitiveType.Cube, "Awning", facadeMaterials[rng.Next(facadeMaterials.Length)],
                    new Vector3(doorX, 2.42f, 0.42f), Quaternion.Euler(20f, 0f, 0f), new Vector3(1.5f, 0.08f, 0.8f));

            if (rng.NextDouble() > 0.5)
                CreatePrimitive(t, PrimitiveType.Cube, "Chimney", chimneyMaterial, new Vector3(w * 0.28f, h + 0.75f, -0.7f), Quaternion.identity, new Vector3(0.55f, 1.05f, 0.55f));

            if (rng.NextDouble() > 0.6)
                CreatePrimitive(t, PrimitiveType.Cube, "SignBoard", woodMaterial, new Vector3(-w * 0.24f, 2.5f, 0.18f), Quaternion.identity, new Vector3(1.4f, 0.5f, 0.08f));
        }

        static void BuildClockTower(Transform parent)
        {
            GameObject tower = new GameObject("ClockTower");
            tower.transform.SetParent(parent, false);
            tower.transform.position = new Vector3(14f, 0f, 14f);
            tower.transform.rotation = Quaternion.Euler(0f, 225f, 0f);
            SetLayer(tower);
            Transform t = tower.transform;

            CreatePrimitive(t, PrimitiveType.Cube, "Base", stoneMaterial, new Vector3(0f, 0.3f, 0f), Quaternion.identity, new Vector3(3.4f, 0.6f, 3.4f));
            CreatePrimitive(t, PrimitiveType.Cube, "WallFront", stoneMaterial, new Vector3(0f, 3.4f, 1.175f), Quaternion.identity, new Vector3(2.6f, 6.2f, 0.25f));
            CreatePrimitive(t, PrimitiveType.Cube, "WallBack", stoneMaterial, new Vector3(0f, 3.4f, -1.175f), Quaternion.identity, new Vector3(2.6f, 6.2f, 0.25f));
            CreatePrimitive(t, PrimitiveType.Cube, "WallL", stoneMaterial, new Vector3(-1.175f, 3.4f, 0f), Quaternion.identity, new Vector3(0.25f, 6.2f, 2.1f));
            CreatePrimitive(t, PrimitiveType.Cube, "WallR", stoneMaterial, new Vector3(1.175f, 3.4f, 0f), Quaternion.identity, new Vector3(0.25f, 6.2f, 2.1f));
            CreatePrimitive(t, PrimitiveType.Cube, "CornerL", roofMaterials[1], new Vector3(-1.3f, 3.4f, 1.3f), Quaternion.identity, new Vector3(0.4f, 6.6f, 0.4f));
            CreatePrimitive(t, PrimitiveType.Cube, "CornerR", roofMaterials[1], new Vector3(1.3f, 3.4f, 1.3f), Quaternion.identity, new Vector3(0.4f, 6.6f, 0.4f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Mast", woodMaterial, new Vector3(0f, 3.6f, 0f), Quaternion.identity, new Vector3(0.5f, 3.5f, 0.5f));

            CreatePrimitive(t, PrimitiveType.Cube, "Door", doorMaterial, new Vector3(0f, 1.15f, 1.24f), Quaternion.identity, new Vector3(1.1f, 2.3f, 0.1f));
            CreatePrimitive(t, PrimitiveType.Cube, "ParapetF", roofMaterials[1], new Vector3(0f, 6.72f, 1.28f), Quaternion.identity, new Vector3(2.9f, 0.24f, 0.18f));
            CreatePrimitive(t, PrimitiveType.Cube, "ParapetB", roofMaterials[1], new Vector3(0f, 6.72f, -1.28f), Quaternion.identity, new Vector3(2.9f, 0.24f, 0.18f));
            CreatePrimitive(t, PrimitiveType.Cube, "ParapetL", roofMaterials[1], new Vector3(-1.28f, 6.72f, 0f), Quaternion.identity, new Vector3(0.18f, 0.24f, 2.9f));
            CreatePrimitive(t, PrimitiveType.Cube, "ParapetR", roofMaterials[1], new Vector3(1.28f, 6.72f, 0f), Quaternion.identity, new Vector3(0.18f, 0.24f, 2.9f));

            for (int i = 0; i < 4; i++)
            {
                float angle = Mathf.Deg2Rad * 90f * i;
                Vector3 local = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Quaternion rot = Quaternion.Euler(0f, 90f * i, 0f);
                Vector3 pos = local * 1.31f + new Vector3(0f, 4.6f, 0f);
                CreatePrimitive(t, PrimitiveType.Cylinder, "ClockFace", markingMaterial, pos, rot * Quaternion.Euler(90f, 0f, 0f), new Vector3(0.9f, 0.06f, 0.9f));
                CreatePrimitive(t, PrimitiveType.Cube, "ClockTrim", roofMaterials[1], pos - local * 0.02f, rot, new Vector3(1.05f, 1.05f, 0.1f));
            }

            CreatePrimitive(t, PrimitiveType.Sphere, "Crest", bulbMaterial, new Vector3(0f, 7.35f, 0f), Quaternion.identity, Vector3.one * 0.36f);
        }

        static void BuildPavilion(Transform parent, Vector3 position, float facingY, System.Random rng)
        {
            GameObject pavilion = new GameObject("Pavilion");
            pavilion.transform.SetParent(parent, false);
            pavilion.transform.position = position;
            pavilion.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
            SetLayer(pavilion);
            Transform t = pavilion.transform;

            CreatePrimitive(t, PrimitiveType.Cube, "Floor", stoneMaterial, new Vector3(0f, 0.06f, 0f), Quaternion.identity, new Vector3(2.6f, 0.12f, 2.6f));
            for (int i = 0; i < 4; i++)
            {
                float x = i % 2 == 0 ? -1.05f : 1.05f;
                float z = i < 2 ? -1.05f : 1.05f;
                CreatePrimitive(t, PrimitiveType.Cube, "Post", woodMaterial, new Vector3(x, 1.27f, z), Quaternion.identity, new Vector3(0.16f, 2.4f, 0.16f));
            }
            CreatePrimitive(t, PrimitiveType.Cube, "Roof", roofMaterials[rng.Next(roofMaterials.Length)], new Vector3(0f, 2.58f, 0f), Quaternion.identity, new Vector3(3.0f, 0.16f, 3.0f));
            CreatePrimitive(t, PrimitiveType.Cube, "RoofRidge", roofMaterials[rng.Next(roofMaterials.Length)], new Vector3(0f, 2.72f, 0f), Quaternion.identity, new Vector3(3.1f, 0.14f, 0.5f));
            CreatePrimitive(t, PrimitiveType.Cube, "Seat", woodMaterial, new Vector3(0f, 0.55f, -0.8f), Quaternion.identity, new Vector3(2.0f, 0.08f, 0.4f));
        }

        static void BuildHedge(Transform parent)
        {
            for (int side = 0; side < 4; side++)
            {
                bool xAxis = side < 2;
                float sign = side % 2 == 0 ? 1f : -1f;
                for (int seg = 0; seg < 2; seg++)
                {
                    float segSign = seg == 0 ? 1f : -1f;
                    float center = segSign * 8.45f;
                    Vector3 pos = xAxis ? new Vector3(center, 0.475f, sign * 15.9f) : new Vector3(sign * 15.9f, 0.475f, center);
                    Vector3 scale = xAxis ? new Vector3(13.1f, 0.95f, 0.18f) : new Vector3(0.18f, 0.95f, 13.1f);
                    CreatePrimitive(parent, PrimitiveType.Cube, "Hedge", foliageMaterials[0], pos, Quaternion.identity, scale);
                }
            }
            for (int i = 0; i < 4; i++)
            {
                float sx = i % 2 == 0 ? 1f : -1f;
                float sz = i < 2 ? 1f : -1f;
                CreatePrimitive(parent, PrimitiveType.Cube, "HedgeCorner", foliageMaterials[2],
                    new Vector3(sx * 15.45f, 0.475f, sz * 15.9f), Quaternion.identity, new Vector3(1.1f, 0.95f, 0.18f));
                CreatePrimitive(parent, PrimitiveType.Cube, "HedgeCornerB", foliageMaterials[2],
                    new Vector3(sx * 15.9f, 0.475f, sz * 15.45f), Quaternion.identity, new Vector3(0.18f, 0.95f, 1.1f));
            }
        }

        static void BuildRingGates(Transform parent, System.Random rng)
        {
            for (int side = 0; side < 4; side++)
            {
                bool xAxis = side < 2;
                float sign = side % 2 == 0 ? 1f : -1f;
                Vector3 pos = xAxis ? new Vector3(sign * 15.9f, 0f, 0f) : new Vector3(0f, 0f, sign * 15.9f);
                float yaw = xAxis ? (sign > 0 ? 90f : -90f) : sign > 0 ? 0f : 180f;
                BuildRingGate(parent, pos, yaw, rng);
            }
        }

        static void BuildRingGate(Transform parent, Vector3 position, float facingY, System.Random rng)
        {
            GameObject gate = new GameObject("RingGate");
            gate.transform.SetParent(parent, false);
            gate.transform.position = position;
            gate.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
            SetLayer(gate);
            Transform t = gate.transform;

            CreatePrimitive(t, PrimitiveType.Cube, "PillarL", stoneMaterial, new Vector3(-1.55f, 1.3f, 0f), Quaternion.identity, new Vector3(0.7f, 2.6f, 0.7f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarR", stoneMaterial, new Vector3(1.55f, 1.3f, 0f), Quaternion.identity, new Vector3(0.7f, 2.6f, 0.7f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarCapL", roofMaterials[1], new Vector3(-1.55f, 2.72f, 0f), Quaternion.identity, new Vector3(0.9f, 0.2f, 0.9f));
            CreatePrimitive(t, PrimitiveType.Cube, "PillarCapR", roofMaterials[1], new Vector3(1.55f, 2.72f, 0f), Quaternion.identity, new Vector3(0.9f, 0.2f, 0.9f));
            CreatePrimitive(t, PrimitiveType.Cube, "Beam", stoneMaterial, new Vector3(0f, 2.95f, 0f), Quaternion.identity, new Vector3(3.8f, 0.4f, 0.6f));
            CreatePrimitive(t, PrimitiveType.Cube, "Roof", roofMaterials[rng.Next(roofMaterials.Length)], new Vector3(0f, 3.24f, 0f), Quaternion.identity, new Vector3(4.2f, 0.16f, 1.0f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "FlagPole", lampMetalMaterial, new Vector3(0f, 3.85f, 0f), Quaternion.identity, new Vector3(0.06f, 0.55f, 0.06f));
            CreatePrimitive(t, PrimitiveType.Cube, "Flag", facadeMaterials[rng.Next(facadeMaterials.Length)], new Vector3(0.28f, 4.02f, 0f), Quaternion.identity, new Vector3(0.5f, 0.3f, 0.04f));
        }

        static void BuildTrees(Transform parent, System.Random rng)
        {
            Vector3[] planterSpots =
            {
                new Vector3(-11f, 0f, 11f), new Vector3(11f, 0f, 11f),
                new Vector3(-11f, 0f, -11f), new Vector3(11f, 0f, -11f),
            };
            foreach (Vector3 spot in planterSpots)
            {
                CreatePrimitive(parent, PrimitiveType.Cube, "TreePlanter", stoneMaterial, spot + new Vector3(0f, 0.25f, 0f), Quaternion.identity, new Vector3(1.4f, 0.5f, 1.4f));
                BuildTree(parent, rng, spot + new Vector3(0f, 0.5f, 0f), Range(rng, 0.8f, 1.0f));
            }

            Vector3[] apronSpots =
            {
                new Vector3(-16f, 0f, 16f), new Vector3(16f, 0f, 16f),
                new Vector3(-16f, 0f, -16f), new Vector3(16f, 0f, -16f),
            };
            foreach (Vector3 spot in apronSpots)
                BuildTree(parent, rng, spot, Range(rng, 0.9f, 1.15f));
        }

        static void BuildTree(Transform parent, System.Random rng, Vector3 position, float scale)
        {
            GameObject tree = new GameObject("Tree");
            tree.transform.SetParent(parent, false);
            tree.transform.position = position;
            tree.transform.rotation = Quaternion.Euler(0f, Range(rng, 0f, 360f), 0f);
            tree.transform.localScale = Vector3.one * scale;
            SetLayer(tree);
            Transform t = tree.transform;

            int mainIndex = rng.Next(foliageMaterials.Length);
            CreatePrimitive(t, PrimitiveType.Cylinder, "Trunk", trunkMaterial, new Vector3(0f, 1.30f, 0f), Quaternion.identity, new Vector3(0.32f, 1.30f, 0.32f));
            CreatePrimitive(t, PrimitiveType.Sphere, "CrownA", foliageMaterials[mainIndex], new Vector3(0f, 2.55f, 0f), Quaternion.identity, Vector3.one * 1.7f);
            CreatePrimitive(t, PrimitiveType.Sphere, "CrownB", foliageMaterials[(mainIndex + 1 + rng.Next(foliageMaterials.Length - 1)) % foliageMaterials.Length],
                new Vector3(0.28f, 3.15f, 0.12f), Quaternion.identity, Vector3.one * 1.1f);
        }

        static void BuildBushes(Transform parent, System.Random rng)
        {
            Vector3[] spots =
            {
                new Vector3(-12f, 0f, 4f), new Vector3(12f, 0f, 4f),
                new Vector3(-12f, 0f, -4f), new Vector3(12f, 0f, -4f),
                new Vector3(-4f, 0f, 12f), new Vector3(4f, 0f, 12f),
                new Vector3(-4f, 0f, -12f), new Vector3(4f, 0f, -12f),
            };
            foreach (Vector3 spot in spots)
                BuildBush(parent, rng, spot);
        }

        static void BuildBush(Transform parent, System.Random rng, Vector3 position)
        {
            GameObject bush = new GameObject("Bush");
            bush.transform.SetParent(parent, false);
            bush.transform.position = position;
            SetLayer(bush);
            Transform t = bush.transform;

            float s = Range(rng, 0.75f, 1.0f);
            CreatePrimitive(t, PrimitiveType.Sphere, "Leaves", foliageMaterials[rng.Next(4)], new Vector3(0f, s * 0.42f, 0f), Quaternion.identity, new Vector3(s, s * 0.7f, s));
            int flowers = 3 + rng.Next(2);
            for (int i = 0; i < flowers; i++)
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                double radius = rng.NextDouble() * s * 0.35f;
                Vector3 offset = new Vector3(Mathf.Cos((float)angle) * (float)radius, s * 0.72f + Range(rng, 0f, 0.12f), Mathf.Sin((float)angle) * (float)radius);
                CreatePrimitive(t, PrimitiveType.Sphere, "Flower", flowerMaterials[rng.Next(flowerMaterials.Length)], offset, Quaternion.identity, Vector3.one * Range(rng, 0.09f, 0.15f));
            }
        }

        static void BuildFlowerPots(Transform parent, System.Random rng)
        {
            Vector3[] spots =
            {
                new Vector3(5.5f, 0f, 7f), new Vector3(5.5f, 0f, 10f),
                new Vector3(4f, 0f, 4f), new Vector3(4f, 0f, -4f),
                new Vector3(-4f, 0f, -4f), new Vector3(-11f, 0f, -4f),
            };
            foreach (Vector3 spot in spots)
                BuildFlowerPot(parent, rng, spot);
        }

        static void BuildFlowerPot(Transform parent, System.Random rng, Vector3 position)
        {
            GameObject pot = new GameObject("FlowerPot");
            pot.transform.SetParent(parent, false);
            pot.transform.position = position;
            SetLayer(pot);
            Transform t = pot.transform;

            CreatePrimitive(t, PrimitiveType.Cube, "Pot", stoneMaterial, new Vector3(0f, 0.28f, 0f), Quaternion.identity, new Vector3(0.85f, 0.56f, 0.85f));
            CreatePrimitive(t, PrimitiveType.Sphere, "Bush", foliageMaterials[rng.Next(4)], new Vector3(0f, 0.78f, 0f), Quaternion.identity, new Vector3(0.95f, 0.66f, 0.95f));
            for (int i = 0; i < 3; i++)
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                double radius = rng.NextDouble() * 0.3f;
                Vector3 offset = new Vector3(Mathf.Cos((float)angle) * (float)radius, 1.08f + Range(rng, 0f, 0.12f), Mathf.Sin((float)angle) * (float)radius);
                CreatePrimitive(t, PrimitiveType.Sphere, "Flower", flowerMaterials[rng.Next(flowerMaterials.Length)], offset, Quaternion.identity, Vector3.one * Range(rng, 0.12f, 0.18f));
            }
        }

        static void BuildStalls(Transform parent, System.Random rng)
        {
            BuildStall(parent, rng, new Vector3(3f, 0f, -12.2f), 0f);
            BuildStall(parent, rng, new Vector3(-3f, 0f, -12.2f), 0f);
        }

        static void BuildStall(Transform parent, System.Random rng, Vector3 position, float facingY)
        {
            GameObject stall = new GameObject("Stall");
            stall.transform.SetParent(parent, false);
            stall.transform.position = position;
            stall.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
            SetLayer(stall);
            Transform t = stall.transform;

            CreatePrimitive(t, PrimitiveType.Cube, "Counter", woodMaterial, new Vector3(0f, 0.48f, 0f), Quaternion.identity, new Vector3(2.2f, 0.95f, 0.9f));
            CreatePrimitive(t, PrimitiveType.Cube, "CounterTop", stoneMaterial, new Vector3(0f, 0.99f, 0f), Quaternion.identity, new Vector3(2.35f, 0.08f, 1.0f));
            for (int i = 0; i < 4; i++)
            {
                float x = i % 2 == 0 ? -1.0f : 1.0f;
                float z = i < 2 ? -0.35f : 0.35f;
                CreatePrimitive(t, PrimitiveType.Cube, "AwningPost", woodMaterial, new Vector3(x, 1.55f, z), Quaternion.identity, new Vector3(0.07f, 1.3f, 0.07f));
            }
            CreatePrimitive(t, PrimitiveType.Cube, "Awning", facadeMaterials[rng.Next(facadeMaterials.Length)], new Vector3(0f, 2.28f, 0.05f), Quaternion.Euler(14f, 0f, 0f), new Vector3(2.6f, 0.08f, 1.4f));

            int goods = 3 + rng.Next(2);
            for (int i = 0; i < goods; i++)
            {
                float x = -0.75f + i * 0.5f;
                if (rng.NextDouble() > 0.5)
                    CreatePrimitive(t, PrimitiveType.Sphere, "GoodsBall", flowerMaterials[rng.Next(flowerMaterials.Length)], new Vector3(x, 1.14f, 0f), Quaternion.identity, Vector3.one * 0.24f);
                else
                    CreatePrimitive(t, PrimitiveType.Cube, "GoodsBox", foliageMaterials[rng.Next(4)], new Vector3(x, 1.16f, 0f), Quaternion.Euler(0f, Range(rng, 0f, 30f), 0f), Vector3.one * 0.26f);
            }
            CreatePrimitive(t, PrimitiveType.Cube, "Crate", woodMaterial, new Vector3(1.55f, 0.3f, 0.2f), Quaternion.Euler(0f, Range(rng, 0f, 20f), 0f), new Vector3(0.6f, 0.6f, 0.6f));
        }

        static void BuildLamps(Transform parent)
        {
            BuildLamp(parent, new Vector3(2.3f, 0f, 12.4f), -90f);
            BuildLamp(parent, new Vector3(-2.3f, 0f, 12.4f), 90f);
            BuildLamp(parent, new Vector3(2.3f, 0f, -12.4f), -90f);
            BuildLamp(parent, new Vector3(-2.3f, 0f, -12.4f), 90f);
            BuildLamp(parent, new Vector3(12.4f, 0f, 2.3f), 180f);
            BuildLamp(parent, new Vector3(12.4f, 0f, -2.3f), 0f);
            BuildLamp(parent, new Vector3(-12.4f, 0f, 2.3f), 180f);
            BuildLamp(parent, new Vector3(-12.4f, 0f, -2.3f), 0f);
            BuildLamp(parent, new Vector3(6.2f, 0f, 6.5f), -90f);
            BuildLamp(parent, new Vector3(6.2f, 0f, 10.5f), -90f);
            BuildLamp(parent, new Vector3(4f, 0f, 0f), -90f);
            BuildLamp(parent, new Vector3(-4f, 0f, 0f), 90f);
            BuildLamp(parent, new Vector3(3.4f, 0f, -3.4f), -135f);
            BuildLamp(parent, new Vector3(-3.4f, 0f, -3.4f), 135f);
        }

        static void BuildLamp(Transform parent, Vector3 position, float facingY)
        {
            GameObject lamp = new GameObject("Lamp");
            lamp.transform.SetParent(parent, false);
            lamp.transform.position = position;
            lamp.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
            SetLayer(lamp);
            Transform t = lamp.transform;

            CreatePrimitive(t, PrimitiveType.Cylinder, "Pole", lampMetalMaterial, new Vector3(0f, 1.80f, 0f), Quaternion.identity, new Vector3(0.10f, 1.80f, 0.10f));
            CreatePrimitive(t, PrimitiveType.Cube, "Arm", lampMetalMaterial, new Vector3(0f, 3.52f, 0.25f), Quaternion.identity, new Vector3(0.08f, 0.08f, 0.60f));
            CreatePrimitive(t, PrimitiveType.Cube, "Head", lampMetalMaterial, new Vector3(0f, 3.44f, 0.50f), Quaternion.identity, new Vector3(0.30f, 0.12f, 0.42f));
            CreatePrimitive(t, PrimitiveType.Sphere, "Bulb", bulbMaterial, new Vector3(0f, 3.32f, 0.50f), Quaternion.identity, Vector3.one * 0.18f);
            CreatePrimitive(t, PrimitiveType.Sphere, "Halo", glowMaterial, new Vector3(0f, 3.32f, 0.50f), Quaternion.identity, Vector3.one * 0.85f);

            Light point = new GameObject("Glow").AddComponent<Light>();
            point.transform.SetParent(t, false);
            point.transform.localPosition = new Vector3(0f, 3.30f, 0.50f);
            point.type = LightType.Point;
            point.color = new Color(1.00f, 0.70f, 0.40f);
            point.intensity = 1.4f;
            point.range = 9f;
            point.shadows = LightShadows.None;
            SetLayer(point.gameObject);
        }

        static void MarkStatic(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = true;
        }

        static void SetLayer(GameObject go)
        {
            go.layer = EnvironmentLayer;
        }

        static float Range(System.Random rng, float min, float max)
        {
            return min + (float)(rng.NextDouble() * (max - min));
        }

        static GameObject CreatePrimitive(Transform parent, PrimitiveType type, string name, Material material, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.layer = EnvironmentLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }
    }
}
