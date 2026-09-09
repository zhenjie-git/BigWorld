using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    public static class ColorfulTownSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/ColorfulTown.unity";
        const string MaterialFolder = "Assets/Materials/ColorfulTown";
        const string MeshFolder = "Assets/Meshes/ColorfulTown";
        const int Seed = 20260905;

        static readonly Color[] FacadePalette =
        {
            new Color(0.90f, 0.55f, 0.45f),
            new Color(0.55f, 0.78f, 0.82f),
            new Color(0.95f, 0.85f, 0.55f),
            new Color(0.60f, 0.80f, 0.60f),
            new Color(0.85f, 0.65f, 0.78f),
            new Color(0.95f, 0.72f, 0.50f),
            new Color(0.70f, 0.72f, 0.88f),
            new Color(0.93f, 0.90f, 0.82f)
        };

        static readonly Color[] RoofPalette =
        {
            new Color(0.42f, 0.25f, 0.22f),
            new Color(0.30f, 0.36f, 0.45f),
            new Color(0.55f, 0.30f, 0.25f),
            new Color(0.28f, 0.40f, 0.36f),
            new Color(0.45f, 0.30f, 0.42f)
        };

        static readonly Color[] FoliagePalette =
        {
            new Color(0.25f, 0.55f, 0.28f),
            new Color(0.35f, 0.65f, 0.30f),
            new Color(0.20f, 0.48f, 0.30f),
            new Color(0.55f, 0.75f, 0.35f),
            new Color(0.90f, 0.55f, 0.60f),
            new Color(0.95f, 0.65f, 0.35f)
        };

        static readonly Color[] FlowerPalette =
        {
            new Color(0.95f, 0.30f, 0.35f),
            new Color(0.98f, 0.80f, 0.25f),
            new Color(0.75f, 0.40f, 0.90f),
            new Color(1.00f, 0.55f, 0.75f),
            new Color(1.00f, 0.98f, 0.90f)
        };

        static Material[] facadeMaterials;
        static Material[] roofMaterials;
        static Material[] foliageMaterials;
        static Material[] flowerMaterials;
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
        static Mesh roofPrismMesh;

        [MenuItem("BigWorld/生成缤纷小镇场景")]
        public static void Build()
        {
            System.Random rng = new System.Random(Seed);
            EnsureFolder(MaterialFolder);
            EnsureFolder(MeshFolder);

            CreateSharedAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupEnvironment();

            GameObject root = new GameObject("ColorfulTown");
            BuildGround(root.transform);
            BuildFountain(root.transform);
            List<Vector3> occupied = new List<Vector3>();
            BuildHouses(root.transform, rng, occupied);
            BuildLamps(root.transform);
            BuildTreesAndBushes(root.transform, rng, occupied);
            BuildParticles(root.transform);
            MarkStatic(root);

            EditorSceneManager.SaveScene(scene, ScenePath);

            CaptureShots();
            AssetDatabase.Refresh();

            int count = root.GetComponentsInChildren<Transform>(true).Length;
            Debug.Log($"[ColorfulTown] scene saved: {ScenePath}, {count} objects");
        }

        static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static void CreateSharedAssets()
        {
            grassMaterial = SaveMaterial(CreateStandardMaterial("Town_Grass", new Color(0.30f, 0.50f, 0.26f), 0f, 0.05f));
            asphaltMaterial = SaveMaterial(CreateStandardMaterial("Town_Asphalt", new Color(0.22f, 0.22f, 0.26f), 0f, 0.25f));
            sidewalkMaterial = SaveMaterial(CreateStandardMaterial("Town_Sidewalk", new Color(0.72f, 0.69f, 0.62f), 0f, 0.10f));
            stoneMaterial = SaveMaterial(CreateStandardMaterial("Town_Stone", new Color(0.82f, 0.76f, 0.66f), 0f, 0.15f));
            markingMaterial = SaveMaterial(CreateStandardMaterial("Town_Marking", new Color(0.90f, 0.88f, 0.80f), 0f, 0.10f));
            trunkMaterial = SaveMaterial(CreateStandardMaterial("Town_Trunk", new Color(0.38f, 0.27f, 0.18f), 0f, 0.05f));
            windowLitMaterial = SaveMaterial(CreateEmissiveMaterial("Town_WindowLit", new Color(1.00f, 0.82f, 0.50f), 2.2f));
            windowDarkMaterial = SaveMaterial(CreateStandardMaterial("Town_WindowDark", new Color(0.10f, 0.12f, 0.18f), 0.4f, 0.60f));
            doorMaterial = SaveMaterial(CreateStandardMaterial("Town_Door", new Color(0.40f, 0.25f, 0.15f), 0f, 0.15f));
            chimneyMaterial = SaveMaterial(CreateStandardMaterial("Town_Chimney", new Color(0.55f, 0.30f, 0.25f), 0f, 0.15f));
            lampMetalMaterial = SaveMaterial(CreateStandardMaterial("Town_LampMetal", new Color(0.16f, 0.20f, 0.18f), 0.6f, 0.50f));
            bulbMaterial = SaveMaterial(CreateEmissiveMaterial("Town_Bulb", new Color(1.00f, 0.85f, 0.55f), 2.6f));
            glowMaterial = SaveMaterial(CreateGlowMaterial("Town_Glow", new Color(1.00f, 0.78f, 0.45f, 0.10f)));
            waterMaterial = SaveMaterial(CreateWaterMaterial());
            woodMaterial = SaveMaterial(CreateStandardMaterial("Town_Wood", new Color(0.45f, 0.30f, 0.20f), 0f, 0.15f));

            facadeMaterials = new Material[FacadePalette.Length];
            string[] facadeNames = { "Coral", "SkyBlue", "Cream", "Mint", "Pink", "Apricot", "Lavender", "Ivory" };
            for (int i = 0; i < FacadePalette.Length; i++)
                facadeMaterials[i] = SaveMaterial(CreateStandardMaterial($"Town_Facade_{facadeNames[i]}", FacadePalette[i], 0f, 0.20f));

            roofMaterials = new Material[RoofPalette.Length];
            for (int i = 0; i < RoofPalette.Length; i++)
                roofMaterials[i] = SaveMaterial(CreateStandardMaterial($"Town_Roof_{i}", RoofPalette[i], 0f, 0.15f));

            foliageMaterials = new Material[FoliagePalette.Length];
            for (int i = 0; i < FoliagePalette.Length; i++)
                foliageMaterials[i] = SaveMaterial(CreateStandardMaterial($"Town_Foliage_{i}", FoliagePalette[i], 0f, 0.08f));

            flowerMaterials = new Material[FlowerPalette.Length];
            for (int i = 0; i < FlowerPalette.Length; i++)
                flowerMaterials[i] = SaveMaterial(CreateEmissiveMaterial($"Town_Flower_{i}", FlowerPalette[i], 0.6f));

            roofPrismMesh = CreateRoofPrismMesh(1f, 1f, 1f);
            AssetDatabase.CreateAsset(roofPrismMesh, $"{MeshFolder}/Town_RoofPrism.asset");
        }

        static Material SaveMaterial(Material mat)
        {
            AssetDatabase.CreateAsset(mat, $"{MaterialFolder}/{mat.name}.mat");
            return mat;
        }

        static Material CreateStandardMaterial(string name, Color color, float metallic, float smoothness)
        {
            Material mat = new Material(Shader.Find("Standard"));
            mat.name = name;
            mat.color = color;
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Glossiness", smoothness);
            return mat;
        }

        static Material CreateEmissiveMaterial(string name, Color color, float intensity)
        {
            Material mat = CreateStandardMaterial(name, color, 0f, 0.30f);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * intensity);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return mat;
        }

        static Material CreateGlowMaterial(string name, Color color)
        {
            Material mat = new Material(Shader.Find("Sprites/Default"));
            mat.name = name;
            mat.color = color;
            return mat;
        }

        static Material CreateWaterMaterial()
        {
            Material mat = CreateStandardMaterial("Town_Water", new Color(0.12f, 0.50f, 0.62f), 0.3f, 0.85f);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", new Color(0.00f, 0.25f, 0.30f) * 0.5f);
            return mat;
        }

        static Mesh CreateRoofPrismMesh(float width, float depth, float height)
        {
            float hw = width * 0.5f;
            float hd = depth * 0.5f;
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();

            void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int s = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
                tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
            }

            void AddTri(Vector3 a, Vector3 b, Vector3 c)
            {
                int s = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c);
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
            }

            Vector3 a = new Vector3(-hw, 0f, -hd);
            Vector3 b = new Vector3(hw, 0f, -hd);
            Vector3 c = new Vector3(hw, 0f, hd);
            Vector3 d = new Vector3(-hw, 0f, hd);
            Vector3 p = new Vector3(0f, height, -hd);
            Vector3 q = new Vector3(0f, height, hd);

            AddQuad(b, p, q, c);
            AddQuad(d, q, p, a);
            AddTri(a, p, b);
            AddTri(c, q, d);

            Mesh mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.name = "Town_RoofPrism";
            return mesh;
        }

        static void SetupEnvironment()
        {
            Light sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(9f, 205f, 0f);
            sun.color = new Color(1.00f, 0.60f, 0.36f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;

            Material sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunSize", 0.045f);
            sky.SetFloat("_AtmosphereThickness", 0.42f);
            sky.SetColor("_SkyTint", new Color(0.68f, 0.60f, 0.72f));
            sky.SetFloat("_Exposure", 1.42f);
            sky.SetFloat("_SunDisk", 2f);
            AssetDatabase.CreateAsset(sky, $"{MaterialFolder}/Town_Skybox.mat");

            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.0055f;
            RenderSettings.fogColor = new Color(0.96f, 0.66f, 0.52f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.52f, 0.66f);
            RenderSettings.ambientEquatorColor = new Color(0.85f, 0.58f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.25f, 0.32f);
        }

        static void BuildGround(Transform parent)
        {
            CreatePrimitive(parent, PrimitiveType.Plane, "Ground", grassMaterial, Vector3.zero, Quaternion.identity, new Vector3(60f, 1f, 60f));

            CreatePrimitive(parent, PrimitiveType.Plane, "Road_NS", asphaltMaterial, new Vector3(0f, 0.018f, 0f), Quaternion.identity, new Vector3(0.6f, 1f, 20f));
            CreatePrimitive(parent, PrimitiveType.Plane, "Road_EW", asphaltMaterial, new Vector3(0f, 0.022f, 0f), Quaternion.identity, new Vector3(20f, 1f, 0.6f));

            CreatePrimitive(parent, PrimitiveType.Plane, "Sidewalk_NS_E", sidewalkMaterial, new Vector3(3.9f, 0.012f, 0f), Quaternion.identity, new Vector3(0.18f, 1f, 20f));
            CreatePrimitive(parent, PrimitiveType.Plane, "Sidewalk_NS_W", sidewalkMaterial, new Vector3(-3.9f, 0.012f, 0f), Quaternion.identity, new Vector3(0.18f, 1f, 20f));
            CreatePrimitive(parent, PrimitiveType.Plane, "Sidewalk_EW_N", sidewalkMaterial, new Vector3(0f, 0.014f, 3.9f), Quaternion.identity, new Vector3(20f, 1f, 0.18f));
            CreatePrimitive(parent, PrimitiveType.Plane, "Sidewalk_EW_S", sidewalkMaterial, new Vector3(0f, 0.014f, -3.9f), Quaternion.identity, new Vector3(20f, 1f, 0.18f));

            for (float z = -96f; z <= 96f; z += 6f)
            {
                if (Mathf.Abs(z) < 4f) continue;
                CreatePrimitive(parent, PrimitiveType.Cube, "Dash", markingMaterial, new Vector3(0f, 0.035f, z), Quaternion.identity, new Vector3(0.18f, 0.01f, 1.6f));
            }
            for (float x = -96f; x <= 96f; x += 6f)
            {
                if (Mathf.Abs(x) < 4f) continue;
                CreatePrimitive(parent, PrimitiveType.Cube, "Dash", markingMaterial, new Vector3(x, 0.037f, 0f), Quaternion.identity, new Vector3(1.6f, 0.01f, 0.18f));
            }

            CreatePrimitive(parent, PrimitiveType.Cylinder, "Plaza", stoneMaterial, new Vector3(0f, 0.03f, 0f), Quaternion.identity, new Vector3(22f, 0.03f, 22f));
        }

        static void BuildFountain(Transform parent)
        {
            GameObject fountain = new GameObject("Fountain");
            fountain.transform.SetParent(parent, false);
            Transform t = fountain.transform;

            CreatePrimitive(t, PrimitiveType.Cylinder, "Pool", stoneMaterial, new Vector3(0f, 0.15f, 0f), Quaternion.identity, new Vector3(4.4f, 0.15f, 4.4f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Water", waterMaterial, new Vector3(0f, 0.20f, 0f), Quaternion.identity, new Vector3(4.0f, 0.10f, 4.0f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Pedestal", stoneMaterial, new Vector3(0f, 0.75f, 0f), Quaternion.identity, new Vector3(0.9f, 0.90f, 0.9f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Bowl", stoneMaterial, new Vector3(0f, 1.70f, 0f), Quaternion.identity, new Vector3(1.8f, 0.15f, 1.8f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "BowlWater", waterMaterial, new Vector3(0f, 1.74f, 0f), Quaternion.identity, new Vector3(1.55f, 0.06f, 1.55f));
            CreatePrimitive(t, PrimitiveType.Cylinder, "Spire", stoneMaterial, new Vector3(0f, 2.30f, 0f), Quaternion.identity, new Vector3(0.5f, 0.60f, 0.5f));
            CreatePrimitive(t, PrimitiveType.Sphere, "Orb", waterMaterial, new Vector3(0f, 2.75f, 0f), Quaternion.identity, new Vector3(0.5f, 0.5f, 0.5f));

            for (int i = 0; i < 4; i++)
            {
                float angle = Mathf.Deg2Rad * (45f + 90f * i);
                Vector3 pos = new Vector3(Mathf.Cos(angle) * 7f, 0f, Mathf.Sin(angle) * 7f);
                GameObject bench = BuildBench();
                bench.transform.SetParent(parent, false);
                bench.transform.position = pos;
                bench.transform.rotation = Quaternion.LookRotation((-pos).normalized);
            }

            for (int i = 0; i < 4; i++)
            {
                float angle = Mathf.Deg2Rad * (90f * i);
                Vector3 pos = new Vector3(Mathf.Cos(angle) * 9.5f, 0f, Mathf.Sin(angle) * 9.5f);
                GameObject pot = BuildFlowerPot();
                pot.transform.SetParent(parent, false);
                pot.transform.position = pos;
            }
        }

        static GameObject BuildBench()
        {
            GameObject bench = new GameObject("Bench");
            Transform t = bench.transform;
            CreatePrimitive(t, PrimitiveType.Cube, "Seat", woodMaterial, new Vector3(0f, 0.50f, 0f), Quaternion.identity, new Vector3(1.7f, 0.08f, 0.45f));
            CreatePrimitive(t, PrimitiveType.Cube, "Back", woodMaterial, new Vector3(0f, 0.85f, -0.19f), Quaternion.identity, new Vector3(1.7f, 0.45f, 0.07f));
            CreatePrimitive(t, PrimitiveType.Cube, "LegL", lampMetalMaterial, new Vector3(-0.70f, 0.25f, 0f), Quaternion.identity, new Vector3(0.08f, 0.50f, 0.40f));
            CreatePrimitive(t, PrimitiveType.Cube, "LegR", lampMetalMaterial, new Vector3(0.70f, 0.25f, 0f), Quaternion.identity, new Vector3(0.08f, 0.50f, 0.40f));
            return bench;
        }

        static GameObject BuildFlowerPot()
        {
            GameObject pot = new GameObject("FlowerPot");
            Transform t = pot.transform;
            CreatePrimitive(t, PrimitiveType.Cube, "Pot", stoneMaterial, new Vector3(0f, 0.30f, 0f), Quaternion.identity, new Vector3(0.9f, 0.60f, 0.9f));
            CreatePrimitive(t, PrimitiveType.Sphere, "Bush", foliageMaterials[Random.Range(0, 4)], new Vector3(0f, 0.85f, 0f), Quaternion.identity, new Vector3(1.0f, 0.7f, 1.0f));
            for (int i = 0; i < 4; i++)
            {
                Vector2 r = Random.insideUnitCircle * 0.35f;
                CreatePrimitive(t, PrimitiveType.Sphere, "Flower", flowerMaterials[Random.Range(0, flowerMaterials.Length)],
                    new Vector3(r.x, 1.15f + Random.Range(0f, 0.15f), r.y), Quaternion.identity, Vector3.one * Random.Range(0.14f, 0.22f));
            }
            return pot;
        }

        static void BuildHouses(Transform parent, System.Random rng, List<Vector3> occupied)
        {
            int index = 0;
            float[] mainZ = { -58f, -44f, -30f, 30f, 44f, 58f };
            float[] crossX = { -58f, -44f, -30f, 30f, 44f, 58f };

            foreach (float z in mainZ)
            {
                BuildHouse(parent, rng, occupied, new Vector3(8.2f, 0f, z), -90f, ref index);
                BuildHouse(parent, rng, occupied, new Vector3(-8.2f, 0f, z), 90f, ref index);
            }
            foreach (float x in crossX)
            {
                BuildHouse(parent, rng, occupied, new Vector3(x, 0f, 8.2f), 180f, ref index);
                BuildHouse(parent, rng, occupied, new Vector3(x, 0f, -8.2f), 0f, ref index);
            }
        }

        static void BuildHouse(Transform parent, System.Random rng, List<Vector3> occupied, Vector3 position, float facingY, ref int index)
        {
            GameObject house = new GameObject($"House_{index:00}");
            house.transform.SetParent(parent, false);
            house.transform.position = position;
            house.transform.rotation = Quaternion.Euler(0f, facingY + ((float)rng.NextDouble() - 0.5f) * 5f, 0f);
            float scale = 0.94f + (float)rng.NextDouble() * 0.16f;
            house.transform.localScale = Vector3.one * scale;

            float w = 3.6f + (float)rng.NextDouble() * 1.8f;
            float d = 3.2f + (float)rng.NextDouble() * 1.4f;
            bool twoFloors = rng.NextDouble() > 0.45f;
            float h = twoFloors ? 5.6f : 3.1f;

            Material facade = facadeMaterials[rng.Next(facadeMaterials.Length)];
            Material roofMat = roofMaterials[rng.Next(roofMaterials.Length)];
            float roofH = 1.5f + (float)rng.NextDouble() * 0.6f;

            CreatePrimitive(house.transform, PrimitiveType.Cube, "Body", facade, new Vector3(0f, h * 0.5f, 0f), Quaternion.identity, new Vector3(w, h, d));

            GameObject roof = new GameObject("Roof");
            roof.transform.SetParent(house.transform, false);
            roof.transform.localPosition = new Vector3(0f, h, 0f);
            if (w > d) roof.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            MeshFilter mf = roof.AddComponent<MeshFilter>();
            mf.sharedMesh = roofPrismMesh;
            MeshRenderer mr = roof.AddComponent<MeshRenderer>();
            mr.sharedMaterial = roofMat;
            roof.transform.localScale = new Vector3(w + 0.5f, roofH, d + 0.5f);

            CreatePrimitive(house.transform, PrimitiveType.Cube, "Door", doorMaterial, new Vector3(0f, 1.02f, d * 0.5f + 0.05f), Quaternion.identity, new Vector3(0.95f, 2.05f, 0.10f));

            float[] windowXs = w >= 4.6f ? new[] { -w * 0.30f, w * 0.30f } : new[] { -w * 0.22f, w * 0.22f };
            float[] floorYs = twoFloors ? new[] { 1.75f, 4.35f } : new[] { 1.75f };
            foreach (float y in floorYs)
            {
                foreach (float wx in windowXs)
                {
                    Material win = rng.NextDouble() > 0.38f ? windowLitMaterial : windowDarkMaterial;
                    CreatePrimitive(house.transform, PrimitiveType.Cube, "Window", win, new Vector3(wx, y, d * 0.5f + 0.04f), Quaternion.identity, new Vector3(0.75f, 1.05f, 0.08f));
                }
                if (twoFloors || y > 2f)
                {
                    if (rng.NextDouble() > 0.5f)
                    {
                        Material win = rng.NextDouble() > 0.38f ? windowLitMaterial : windowDarkMaterial;
                        CreatePrimitive(house.transform, PrimitiveType.Cube, "SideWindow", win, new Vector3(w * 0.5f + 0.04f, y, 0f), Quaternion.identity, new Vector3(0.08f, 1.05f, 0.75f));
                    }
                    if (rng.NextDouble() > 0.5f)
                    {
                        Material win = rng.NextDouble() > 0.38f ? windowLitMaterial : windowDarkMaterial;
                        CreatePrimitive(house.transform, PrimitiveType.Cube, "SideWindow", win, new Vector3(-w * 0.5f - 0.04f, y, 0f), Quaternion.identity, new Vector3(0.08f, 1.05f, 0.75f));
                    }
                }
            }

            if (rng.NextDouble() > 0.45f)
                CreatePrimitive(house.transform, PrimitiveType.Cube, "Chimney", chimneyMaterial, new Vector3(w * 0.22f, h + roofH * 0.55f, -d * 0.15f), Quaternion.identity, new Vector3(0.55f, 1.10f, 0.55f));

            occupied.Add(position);
            index++;
        }

        static void BuildLamps(Transform parent)
        {
            float[] mainZ = { -46f, -32f, 32f, 46f };
            float[] crossX = { -46f, -32f, 32f, 46f };
            foreach (float z in mainZ)
            {
                BuildLamp(parent, new Vector3(4.3f, 0f, z), -90f);
                BuildLamp(parent, new Vector3(-4.3f, 0f, z), 90f);
            }
            foreach (float x in crossX)
            {
                BuildLamp(parent, new Vector3(x, 0f, 4.3f), 180f);
                BuildLamp(parent, new Vector3(x, 0f, -4.3f), 0f);
            }
            for (int i = 0; i < 4; i++)
            {
                float angle = Mathf.Deg2Rad * (45f + 90f * i);
                Vector3 pos = new Vector3(Mathf.Cos(angle) * 11.5f, 0f, Mathf.Sin(angle) * 11.5f);
                BuildLamp(parent, pos, Mathf.Rad2Deg * Mathf.Atan2(-pos.x, -pos.z));
            }
        }

        static void BuildLamp(Transform parent, Vector3 position, float facingY)
        {
            GameObject lamp = new GameObject("Lamp");
            lamp.transform.SetParent(parent, false);
            lamp.transform.position = position;
            lamp.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
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
        }

        static void BuildTreesAndBushes(Transform parent, System.Random rng, List<Vector3> occupied)
        {
            List<Vector3> treePositions = new List<Vector3>();
            int trees = 0;
            for (int attempt = 0; attempt < 600 && trees < 45; attempt++)
            {
                Vector3 pos = new Vector3(Random.Range(-88f, 88f), 0f, Random.Range(-88f, 88f));
                if (!IsFreeSpot(pos, occupied, treePositions, 4.2f, 3.4f)) continue;
                BuildTree(parent, rng, pos, 0.8f + (float)rng.NextDouble() * 0.6f);
                treePositions.Add(pos);
                trees++;
            }

            List<Vector3> bushPositions = new List<Vector3>();
            int bushes = 0;
            for (int attempt = 0; attempt < 500 && bushes < 26; attempt++)
            {
                Vector3 pos = new Vector3(Random.Range(-85f, 85f), 0f, Random.Range(-85f, 85f));
                if (!IsFreeSpot(pos, occupied, treePositions, 2.6f, 3.0f)) continue;
                if (!IsFreeSpot(pos, occupied, bushPositions, 1.2f, 1.2f)) continue;
                BuildBush(parent, rng, pos);
                bushPositions.Add(pos);
                bushes++;
            }

            int patches = 0;
            for (int attempt = 0; attempt < 400 && patches < 30; attempt++)
            {
                Vector3 pos = new Vector3(Random.Range(-80f, 80f), 0f, Random.Range(-80f, 80f));
                if (!IsFreeSpot(pos, occupied, treePositions, 1.6f, 3.0f)) continue;
                if (!IsFreeSpot(pos, occupied, bushPositions, 1.0f, 1.0f)) continue;
                BuildFlowerPatch(parent, rng, pos);
                patches++;
            }
        }

        static bool IsFreeSpot(Vector3 pos, List<Vector3> occupied, List<Vector3> group, float occupiedClearance, float groupClearance)
        {
            if (Mathf.Abs(pos.x) < 6.5f || Mathf.Abs(pos.z) < 6.5f) return false;
            if (pos.magnitude < 14f) return false;
            foreach (Vector3 o in occupied)
                if ((o - pos).sqrMagnitude < occupiedClearance * occupiedClearance) return false;
            foreach (Vector3 g in group)
                if ((g - pos).sqrMagnitude < groupClearance * groupClearance) return false;
            return true;
        }

        static void BuildTree(Transform parent, System.Random rng, Vector3 position, float scale)
        {
            GameObject tree = new GameObject("Tree");
            tree.transform.SetParent(parent, false);
            tree.transform.position = position;
            tree.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            tree.transform.localScale = Vector3.one * scale;

            int mainIndex = rng.NextDouble() > 0.78f ? rng.Next(4, 6) : rng.Next(0, 4);
            float s1 = 2.0f + (float)rng.NextDouble() * 1.0f;
            CreatePrimitive(tree.transform, PrimitiveType.Cylinder, "Trunk", trunkMaterial, new Vector3(0f, 1.40f, 0f), Quaternion.identity, new Vector3(0.35f, 1.40f, 0.35f));
            CreatePrimitive(tree.transform, PrimitiveType.Sphere, "CrownA", foliageMaterials[mainIndex], new Vector3(0f, 2.6f + s1 * 0.35f, 0f), Quaternion.identity, Vector3.one * s1);

            float s2 = s1 * 0.7f;
            Vector2 r = Random.insideUnitCircle * 0.45f;
            int secondIndex = (mainIndex + 1 + rng.Next(FoliagePalette.Length - 1)) % FoliagePalette.Length;
            CreatePrimitive(tree.transform, PrimitiveType.Sphere, "CrownB", foliageMaterials[secondIndex],
                new Vector3(r.x, 2.6f + s1 * 0.35f + s2 * 0.5f, r.y), Quaternion.identity, Vector3.one * s2);

            if (rng.NextDouble() > 0.6f)
            {
                Vector2 r2 = Random.insideUnitCircle * 0.6f;
                CreatePrimitive(tree.transform, PrimitiveType.Sphere, "CrownC", foliageMaterials[mainIndex],
                    new Vector3(r2.x, 2.8f + s1 * 0.3f, r2.y), Quaternion.identity, Vector3.one * s1 * 0.55f);
            }
        }

        static void BuildBush(Transform parent, System.Random rng, Vector3 position)
        {
            GameObject bush = new GameObject("Bush");
            bush.transform.SetParent(parent, false);
            bush.transform.position = position;
            float s = 0.8f + (float)rng.NextDouble() * 0.5f;
            CreatePrimitive(bush.transform, PrimitiveType.Sphere, "Leaves", foliageMaterials[rng.Next(0, 4)], new Vector3(0f, s * 0.45f, 0f), Quaternion.identity, new Vector3(s, s * 0.7f, s));
            int flowers = 3 + rng.Next(3);
            for (int i = 0; i < flowers; i++)
            {
                Vector2 r = Random.insideUnitCircle * s * 0.4f;
                CreatePrimitive(bush.transform, PrimitiveType.Sphere, "Flower", flowerMaterials[rng.Next(flowerMaterials.Length)],
                    new Vector3(r.x, s * 0.75f + Random.Range(0f, 0.2f), r.y), Quaternion.identity, Vector3.one * Random.Range(0.10f, 0.18f));
            }
        }

        static void BuildFlowerPatch(Transform parent, System.Random rng, Vector3 position)
        {
            GameObject patch = new GameObject("FlowerPatch");
            patch.transform.SetParent(parent, false);
            patch.transform.position = position;
            int count = 4 + rng.Next(5);
            Material mat = flowerMaterials[rng.Next(flowerMaterials.Length)];
            for (int i = 0; i < count; i++)
            {
                Vector2 r = Random.insideUnitCircle * 0.7f;
                CreatePrimitive(patch.transform, PrimitiveType.Sphere, "Flower", mat,
                    new Vector3(r.x, 0.08f, r.y), Quaternion.identity, Vector3.one * Random.Range(0.09f, 0.16f));
            }
        }

        static void BuildParticles(Transform parent)
        {
            Material particleMat = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Particle.mat");
            if (particleMat == null) particleMat = new Material(Shader.Find("Particles/Standard Unlit"));

            GameObject fireflies = new GameObject("Fireflies");
            fireflies.transform.SetParent(parent, false);
            fireflies.transform.position = new Vector3(0f, 1.6f, 0f);
            ParticleSystem ff = fireflies.AddComponent<ParticleSystem>();
            ff.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var ffMain = ff.main;
            ffMain.duration = 12f;
            ffMain.loop = true;
            ffMain.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
            ffMain.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.10f);
            ffMain.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            ffMain.startColor = new ParticleSystem.MinMaxGradient(new Color(1.00f, 0.85f, 0.40f), new Color(0.95f, 0.95f, 0.70f));
            ffMain.simulationSpace = ParticleSystemSimulationSpace.World;
            ffMain.maxParticles = 120;
            var ffEmission = ff.emission;
            ffEmission.rateOverTime = new ParticleSystem.MinMaxCurve(16f);
            var ffShape = ff.shape;
            ffShape.shapeType = ParticleSystemShapeType.Box;
            ffShape.scale = new Vector3(60f, 3f, 60f);
            var ffVelocity = ff.velocityOverLifetime;
            ffVelocity.enabled = true;
            ffVelocity.x = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
            ffVelocity.y = new ParticleSystem.MinMaxCurve(-0.10f, 0.15f);
            ffVelocity.z = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
            Gradient ffGradient = new Gradient();
            ffGradient.SetKeys(
                new[] { new GradientColorKey(new Color(1.00f, 0.88f, 0.50f), 0f), new GradientColorKey(new Color(1.00f, 0.88f, 0.50f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            var ffColor = ff.colorOverLifetime;
            ffColor.enabled = true;
            ffColor.color = new ParticleSystem.MinMaxGradient(ffGradient);
            SetupParticleRenderer(ff, particleMat);

            GameObject petals = new GameObject("Petals");
            petals.transform.SetParent(parent, false);
            petals.transform.position = new Vector3(0f, 8f, 0f);
            ParticleSystem pt = petals.AddComponent<ParticleSystem>();
            pt.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var ptMain = pt.main;
            ptMain.duration = 12f;
            ptMain.loop = true;
            ptMain.startLifetime = new ParticleSystem.MinMaxCurve(9f, 14f);
            ptMain.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.4f);
            ptMain.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.20f);
            ptMain.gravityModifier = new ParticleSystem.MinMaxCurve(0.045f);
            ptMain.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            ptMain.startColor = new ParticleSystem.MinMaxGradient(new Color(0.98f, 0.62f, 0.75f), new Color(0.97f, 0.93f, 0.85f));
            ptMain.simulationSpace = ParticleSystemSimulationSpace.World;
            ptMain.maxParticles = 400;
            var ptEmission = pt.emission;
            ptEmission.rateOverTime = new ParticleSystem.MinMaxCurve(20f);
            var ptShape = pt.shape;
            ptShape.shapeType = ParticleSystemShapeType.Box;
            ptShape.scale = new Vector3(150f, 2f, 150f);
            Gradient ptGradient = new Gradient();
            ptGradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.15f), new GradientAlphaKey(0.9f, 0.85f), new GradientAlphaKey(0f, 1f) });
            var ptColor = pt.colorOverLifetime;
            ptColor.enabled = true;
            ptColor.color = new ParticleSystem.MinMaxGradient(ptGradient);
            SetupParticleRenderer(pt, particleMat);
        }

        static void SetupParticleRenderer(ParticleSystem ps, Material mat)
        {
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = mat;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static void CaptureShots()
        {
            GameObject camGo = new GameObject("ShotCamera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 900f;
            cam.fieldOfView = 58f;
            camGo.transform.position = new Vector3(2.4f, 2.3f, -42f);
            camGo.transform.LookAt(new Vector3(0f, 2.0f, 12f));
            Capture(cam, "Assets/Screenshots/ColorfulTown_Street.png", 1600, 900);

            cam.fieldOfView = 48f;
            camGo.transform.position = new Vector3(36f, 26f, -38f);
            camGo.transform.LookAt(new Vector3(0f, 0f, 6f));
            Capture(cam, "Assets/Screenshots/ColorfulTown_Overview.png", 1600, 900);

            Object.DestroyImmediate(camGo);
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
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }

        static void MarkStatic(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = true;
        }

        static GameObject CreatePrimitive(Transform parent, PrimitiveType type, string name, Material material, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }
    }
}
