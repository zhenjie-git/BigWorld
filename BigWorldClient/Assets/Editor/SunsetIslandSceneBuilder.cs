using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace BigWorldClient
{
    public static class SunsetIslandSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/SunsetIsland.unity";
        const string MaterialFolder = "Assets/Materials/SunsetIsland";
        const string MeshFolder = "Assets/Meshes/SunsetIsland";
        const int Seed = 20260905;
        const int GridCells = 128;
        const float Extent = 130f;

        static Material terrainMaterial;
        static Material waterMaterial;
        static Material trunkMaterial;
        static Material frondAMaterial;
        static Material frondBMaterial;
        static Material coconutMaterial;
        static Material rockMaterial;
        static Material woodMaterial;
        static Material darkWoodMaterial;
        static Material canopyCoralMaterial;
        static Material canopyWhiteMaterial;
        static Material hullMaterial;
        static Material sailMaterial;
        static Material flagMaterial;
        static Material cloudMaterial;
        static Material glowMaterial;
        static Material towelCoralMaterial;
        static Material towelYellowMaterial;
        static Mesh umbrellaCanopyMesh;
        static Mesh sailMesh;
        static Mesh flagMesh;

        [MenuItem("BigWorld/生成日落海岛场景")]
        public static void Build()
        {
            System.Random rng = new System.Random(Seed);
            EnsureFolder(MaterialFolder);
            EnsureFolder(MeshFolder);

            CreateSharedAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupEnvironment();

            GameObject root = new GameObject("SunsetIsland");
            BuildTerrain(root.transform);
            BuildWater(root.transform);
            BuildPalms(root.transform, rng);
            BuildRocks(root.transform, rng);
            BuildDock(root.transform);
            BuildBeachCamp(root.transform);
            BuildBoat(root.transform);
            BuildBuoy(root.transform);
            BuildClouds(root.transform, rng);
            MarkStatic(root);

            EditorSceneManager.SaveScene(scene, ScenePath);
            CaptureShots();
            AssetDatabase.Refresh();

            int count = root.GetComponentsInChildren<Transform>(true).Length;
            Debug.Log($"[SunsetIsland] scene saved: {ScenePath}, {count} objects");
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
            terrainMaterial = SaveMaterial(new Material(Shader.Find("Legacy Shaders/Diffuse")) { name = "Island_Terrain" });
            waterMaterial = SaveMaterial(CreateWaterMaterial());
            trunkMaterial = SaveMaterial(CreateStandardMaterial("Island_Trunk", new Color(0.42f, 0.32f, 0.20f), 0f, 0.05f));
            frondAMaterial = SaveMaterial(CreateStandardMaterial("Island_FrondA", new Color(0.22f, 0.50f, 0.25f), 0f, 0.08f));
            frondBMaterial = SaveMaterial(CreateStandardMaterial("Island_FrondB", new Color(0.32f, 0.62f, 0.28f), 0f, 0.08f));
            coconutMaterial = SaveMaterial(CreateStandardMaterial("Island_Coconut", new Color(0.42f, 0.30f, 0.16f), 0f, 0.10f));
            rockMaterial = SaveMaterial(CreateStandardMaterial("Island_Rock", new Color(0.46f, 0.43f, 0.42f), 0f, 0.10f));
            woodMaterial = SaveMaterial(CreateStandardMaterial("Island_Wood", new Color(0.55f, 0.40f, 0.26f), 0f, 0.10f));
            darkWoodMaterial = SaveMaterial(CreateStandardMaterial("Island_DarkWood", new Color(0.36f, 0.25f, 0.15f), 0f, 0.10f));
            canopyCoralMaterial = SaveMaterial(CreateStandardMaterial("Island_CanopyCoral", new Color(0.90f, 0.36f, 0.30f), 0f, 0.20f));
            canopyWhiteMaterial = SaveMaterial(CreateStandardMaterial("Island_CanopyWhite", new Color(0.95f, 0.90f, 0.82f), 0f, 0.20f));
            hullMaterial = SaveMaterial(CreateStandardMaterial("Island_Hull", new Color(0.92f, 0.90f, 0.86f), 0f, 0.25f));
            sailMaterial = SaveMaterial(CreateStandardMaterial("Island_Sail", new Color(0.96f, 0.93f, 0.86f), 0f, 0.30f));
            flagMaterial = SaveMaterial(CreateStandardMaterial("Island_Flag", new Color(0.90f, 0.25f, 0.25f), 0f, 0.20f));
            cloudMaterial = SaveMaterial(CreateCloudMaterial());
            glowMaterial = SaveMaterial(CreateGlowMaterial("Island_Glow", new Color(1.00f, 0.60f, 0.30f, 0.14f)));
            towelCoralMaterial = SaveMaterial(CreateStandardMaterial("Island_TowelCoral", new Color(0.92f, 0.45f, 0.40f), 0f, 0.15f));
            towelYellowMaterial = SaveMaterial(CreateStandardMaterial("Island_TowelYellow", new Color(0.95f, 0.80f, 0.35f), 0f, 0.15f));

            umbrellaCanopyMesh = CreateConeMesh(8, 1f, 0.55f);
            umbrellaCanopyMesh.name = "Island_UmbrellaCanopy";
            AssetDatabase.CreateAsset(umbrellaCanopyMesh, $"{MeshFolder}/Island_UmbrellaCanopy.asset");

            sailMesh = CreateTriangleMesh(2.0f, 2.5f);
            sailMesh.name = "Island_Sail";
            AssetDatabase.CreateAsset(sailMesh, $"{MeshFolder}/Island_Sail.asset");

            flagMesh = CreateTriangleMesh(0.45f, 0.28f);
            flagMesh.name = "Island_Flag";
            AssetDatabase.CreateAsset(flagMesh, $"{MeshFolder}/Island_Flag.asset");
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

        static Material CreateWaterMaterial()
        {
            Material mat = CreateStandardMaterial("Island_Water", new Color(0.08f, 0.30f, 0.42f), 0.35f, 0.92f);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", new Color(0.00f, 0.08f, 0.12f));
            return mat;
        }

        static Material CreateCloudMaterial()
        {
            Material mat = CreateStandardMaterial("Island_Cloud", new Color(0.96f, 0.94f, 0.95f), 0f, 0.00f);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", new Color(0.35f, 0.28f, 0.34f) * 0.4f);
            return mat;
        }

        static Material CreateGlowMaterial(string name, Color color)
        {
            Material mat = new Material(Shader.Find("Sprites/Default"));
            mat.name = name;
            mat.color = color;
            return mat;
        }

        static Mesh CreateConeMesh(int segments, float radius, float height)
        {
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2f * i / segments;
                float a1 = Mathf.PI * 2f * (i + 1) / segments;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);
                Vector3 apex = new Vector3(0f, height, 0f);
                Vector3 center = new Vector3(0f, -0.02f, 0f);

                int s = verts.Count;
                verts.Add(p0); verts.Add(apex); verts.Add(p1);
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);

                int u = verts.Count;
                verts.Add(p0); verts.Add(p1); verts.Add(center);
                tris.Add(u); tris.Add(u + 1); tris.Add(u + 2);
            }

            Mesh mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh CreateTriangleMesh(float width, float height)
        {
            Vector3 a = new Vector3(0f, 0f, 0f);
            Vector3 b = new Vector3(0f, height, 0f);
            Vector3 c = new Vector3(width, height * 0.15f, 0f);

            Mesh mesh = new Mesh();
            mesh.vertices = new[] { a, b, c, a, c, b };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static float IslandNoise01(float x, float z)
        {
            float v = Mathf.Sin(x * 0.043f + 1.7f) * Mathf.Cos(z * 0.037f + 4.2f) * 0.5f
                    + Mathf.Sin(x * 0.091f + 3.1f) * Mathf.Cos(z * 0.083f + 0.8f) * 0.30f
                    + Mathf.Sin(x * 0.190f + 5.4f) * Mathf.Cos(z * 0.173f + 2.6f) * 0.14f;
            return Mathf.Clamp01(v * 0.5f + 0.5f);
        }

        static float Gaussian(float x, float z, float cx, float cz, float sigma)
        {
            float dx = x - cx;
            float dz = z - cz;
            return Mathf.Exp(-(dx * dx + dz * dz) / (2f * sigma * sigma));
        }

        static float IslandHeight(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + z * z);
            float falloff = 1f - Mathf.SmoothStep(58f, 96f, r);
            float h = 1.7f + 2.6f * IslandNoise01(x, z);
            h += Gaussian(x, z, -25f, 20f, 30f) * 13f;
            h += Gaussian(x, z, 22f, -16f, 22f) * 9f;
            h -= Gaussian(x, z, 32f, 26f, 10f) * 4.0f;
            return Mathf.Lerp(-3.5f, h * falloff, Mathf.Clamp01(falloff * 3f));
        }

        static Color TerrainColor(float h, float x, float z)
        {
            float n = IslandNoise01(x * 3.1f + 40f, z * 2.7f - 15f);
            Color deep = new Color(0.24f, 0.40f, 0.47f);
            Color shallowSand = new Color(0.62f, 0.58f, 0.44f);
            Color sand = new Color(0.93f, 0.85f, 0.60f);
            Color grassA = new Color(0.30f, 0.54f, 0.28f);
            Color grassB = new Color(0.44f, 0.62f, 0.30f);
            Color rock = new Color(0.47f, 0.43f, 0.42f);
            Color grass = Color.Lerp(grassA, grassB, n);
            Color c;
            if (h < -1.2f) c = Color.Lerp(deep, shallowSand, Mathf.InverseLerp(-3.5f, -1.2f, h));
            else if (h < 0.35f) c = Color.Lerp(shallowSand, sand, Mathf.InverseLerp(-1.2f, 0.35f, h));
            else if (h < 1.2f) c = Color.Lerp(sand, grass, Mathf.InverseLerp(0.35f, 1.2f, h));
            else if (h < 8f) c = grass;
            else c = Color.Lerp(grass, rock, Mathf.InverseLerp(8f, 13f, h));
            return c * (0.94f + 0.12f * IslandNoise01(x * 7.7f, z * 7.7f));
        }

        static void SetupEnvironment()
        {
            Light sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(10f, 67f, 0f);
            sun.color = new Color(1.00f, 0.55f, 0.32f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;

            Material sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunSize", 0.05f);
            sky.SetFloat("_AtmosphereThickness", 0.45f);
            sky.SetColor("_SkyTint", new Color(0.65f, 0.55f, 0.70f));
            sky.SetFloat("_Exposure", 1.4f);
            sky.SetFloat("_SunDisk", 2f);
            AssetDatabase.CreateAsset(sky, $"{MaterialFolder}/Island_Skybox.mat");

            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.0045f;
            RenderSettings.fogColor = new Color(0.96f, 0.64f, 0.52f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.60f, 0.50f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.85f, 0.56f, 0.48f);
            RenderSettings.ambientGroundColor = new Color(0.32f, 0.28f, 0.32f);
        }

        static void BuildTerrain(Transform parent)
        {
            int size = GridCells + 1;
            Vector3[] verts = new Vector3[size * size];
            Color[] colors = new Color[size * size];
            Vector2[] uvs = new Vector2[size * size];
            int[] tris = new int[GridCells * GridCells * 6];

            for (int iz = 0; iz < size; iz++)
            {
                for (int ix = 0; ix < size; ix++)
                {
                    float x = -Extent + 2f * Extent * ix / GridCells;
                    float z = -Extent + 2f * Extent * iz / GridCells;
                    float h = IslandHeight(x, z);
                    int i = iz * size + ix;
                    verts[i] = new Vector3(x, h, z);
                    colors[i] = TerrainColor(h, x, z);
                    uvs[i] = new Vector2((float)ix / GridCells, (float)iz / GridCells);
                }
            }

            int t = 0;
            for (int iz = 0; iz < GridCells; iz++)
            {
                for (int ix = 0; ix < GridCells; ix++)
                {
                    int bl = iz * size + ix;
                    int br = bl + 1;
                    int tl = bl + size;
                    int tr = tl + 1;
                    tris[t++] = bl; tris[t++] = tl; tris[t++] = br;
                    tris[t++] = br; tris[t++] = tl; tris[t++] = tr;
                }
            }

            Mesh mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(new List<Vector3>(verts));
            mesh.SetColors(new List<Color>(colors));
            mesh.SetUVs(0, new List<Vector2>(uvs));
            mesh.SetTriangles(new List<int>(tris), 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.name = "Island_Terrain";
            AssetDatabase.CreateAsset(mesh, $"{MeshFolder}/Island_Terrain.asset");

            GameObject terrain = new GameObject("Terrain");
            terrain.transform.SetParent(parent, false);
            MeshFilter mf = terrain.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            MeshRenderer mr = terrain.AddComponent<MeshRenderer>();
            mr.sharedMaterial = terrainMaterial;
            MeshCollider mc = terrain.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }

        static void BuildWater(Transform parent)
        {
            CreatePrimitive(parent, PrimitiveType.Plane, "Sea", waterMaterial, new Vector3(0f, 0f, 0f), Quaternion.identity, new Vector3(90f, 1f, 90f));
        }

        static Vector3 GroundPoint(float x, float z, float sink)
        {
            return new Vector3(x, IslandHeight(x, z) + sink, z);
        }

        static void BuildPalms(Transform parent, System.Random rng)
        {
            List<Vector3> props = new List<Vector3>
            {
                new Vector3(14f, 0f, 74f), new Vector3(18f, 0f, 78f), new Vector3(8f, 0f, 70f)
            };

            int placed = 0;
            for (int attempt = 0; attempt < 300 && placed < 16; attempt++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 58f + (float)rng.NextDouble() * 22f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                Vector3 flat = new Vector3(x, 0f, z);
                if (x < -60f && Mathf.Abs(z - 8f) < 8f) continue;
                bool free = true;
                foreach (Vector3 p in props)
                    if ((p - flat).sqrMagnitude < 20f) free = false;
                if (!free) continue;
                BuildPalm(parent, rng, GroundPoint(x, z, -0.08f), 0.85f + (float)rng.NextDouble() * 0.5f);
                props.Add(flat);
                placed++;
            }

            BuildPalm(parent, rng, GroundPoint(24f, 76f, -0.08f), 1.15f);
            BuildPalm(parent, rng, GroundPoint(17f, 85f, -0.08f), 0.95f);
            BuildPalm(parent, rng, GroundPoint(29f, 68f, -0.08f), 1.05f);
        }

        static void BuildPalm(Transform parent, System.Random rng, Vector3 position, float scale)
        {
            GameObject palm = new GameObject("Palm");
            palm.transform.SetParent(parent, false);
            palm.transform.position = position;
            palm.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            palm.transform.localScale = Vector3.one * scale;

            float leanAngle = (float)rng.NextDouble() * 360f;
            Vector3 leanDir = new Vector3(Mathf.Sin(leanAngle * Mathf.Deg2Rad), 0f, Mathf.Cos(leanAngle * Mathf.Deg2Rad));
            Vector3 pos = Vector3.zero;
            int segments = 5;
            for (int i = 0; i < segments; i++)
            {
                float t = (float)i / (segments - 1);
                float radius = Mathf.Lerp(0.30f, 0.16f, t);
                CreatePrimitive(palm.transform, PrimitiveType.Cylinder, "Trunk", trunkMaterial,
                    pos + new Vector3(0f, 0.5f, 0f), Quaternion.identity, new Vector3(radius * 2f, 0.5f, radius * 2f));
                pos += leanDir * (0.09f * i) + Vector3.up;
            }

            for (int i = 0; i < 8; i++)
            {
                float yaw = i * 45f + (float)(rng.NextDouble() - 0.5) * 16f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                CreatePrimitive(palm.transform, PrimitiveType.Cube, "Frond", frondAMaterial,
                    pos + dir * 1.05f + Vector3.up * 0.05f, Quaternion.Euler(-16f, yaw, 0f), new Vector3(0.50f, 0.06f, 2.30f));
            }
            for (int i = 0; i < 6; i++)
            {
                float yaw = i * 60f + 30f + (float)(rng.NextDouble() - 0.5) * 16f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                CreatePrimitive(palm.transform, PrimitiveType.Cube, "Frond", frondBMaterial,
                    pos + dir * 0.72f + Vector3.up * 0.16f, Quaternion.Euler(-34f, yaw, 0f), new Vector3(0.40f, 0.05f, 1.60f));
            }

            for (int i = 0; i < 3; i++)
            {
                Vector2 r = Random.insideUnitCircle * 0.18f;
                CreatePrimitive(palm.transform, PrimitiveType.Sphere, "Coconut", coconutMaterial,
                    pos + new Vector3(r.x, -0.10f, r.y), Quaternion.identity, Vector3.one * 0.16f);
            }
        }

        static void BuildRocks(Transform parent, System.Random rng)
        {
            int placed = 0;
            for (int attempt = 0; attempt < 200 && placed < 10; attempt++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 40f + (float)rng.NextDouble() * 42f;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                if (x < -60f && Mathf.Abs(z - 8f) < 8f) continue;
                if (IslandHeight(x, z) < 0.4f) continue;
                float s = 0.6f + (float)rng.NextDouble() * 1.6f;
                CreatePrimitive(parent, PrimitiveType.Sphere, "Rock", rockMaterial,
                    GroundPoint(x, z, -s * 0.15f), Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f),
                    new Vector3(s, s * (0.5f + (float)rng.NextDouble() * 0.4f), s * (0.7f + (float)rng.NextDouble() * 0.5f)));
                placed++;
            }
        }

        static void BuildDock(Transform parent)
        {
            GameObject dock = new GameObject("Dock");
            dock.transform.SetParent(parent, false);

            for (int i = 0; i < 11; i++)
            {
                float x = -86f - 2.2f * i;
                CreatePrimitive(dock.transform, PrimitiveType.Cube, "Plank", woodMaterial,
                    new Vector3(x, 0.50f, 8f + (i % 2 == 0 ? 0.04f : -0.04f)),
                    Quaternion.Euler(0f, (i % 2 == 0 ? 1.5f : -1.5f), 0f), new Vector3(2.0f, 0.08f, 0.55f));
                if (i % 3 == 0)
                {
                    CreatePrimitive(dock.transform, PrimitiveType.Cylinder, "Leg", darkWoodMaterial,
                        new Vector3(x, -0.35f, 7.65f), Quaternion.identity, new Vector3(0.16f, 0.85f, 0.16f));
                    CreatePrimitive(dock.transform, PrimitiveType.Cylinder, "Leg", darkWoodMaterial,
                        new Vector3(x, -0.35f, 8.35f), Quaternion.identity, new Vector3(0.16f, 0.85f, 0.16f));
                }
            }

            BuildTorch(dock.transform, new Vector3(-90f, 0.50f, 6.8f));
            BuildTorch(dock.transform, new Vector3(-105f, 0.50f, 9.2f));
        }

        static void BuildTorch(Transform parent, Vector3 position)
        {
            GameObject torch = new GameObject("Torch");
            torch.transform.SetParent(parent, false);
            torch.transform.position = position;

            CreatePrimitive(torch.transform, PrimitiveType.Cylinder, "Pole", darkWoodMaterial,
                new Vector3(0f, 0.80f, 0f), Quaternion.identity, new Vector3(0.13f, 0.80f, 0.13f));
            CreatePrimitive(torch.transform, PrimitiveType.Cylinder, "Brazier", rockMaterial,
                new Vector3(0f, 1.68f, 0f), Quaternion.identity, new Vector3(0.26f, 0.16f, 0.26f));

            Material particleMat = LoadParticleMaterial();
            GameObject flameGo = new GameObject("Flame");
            flameGo.transform.SetParent(torch.transform, false);
            flameGo.transform.localPosition = new Vector3(0f, 1.85f, 0f);
            ParticleSystem flame = flameGo.AddComponent<ParticleSystem>();
            flame.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = flame.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.20f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(2.6f, 1.6f, 0.5f), new Color(2.2f, 0.8f, 0.2f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;
            var emission = flame.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(26f);
            var shape = flame.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.07f;
            var velocity = flame.velocityOverLifetime;
            velocity.enabled = true;
            velocity.y = new ParticleSystem.MinMaxCurve(0.7f, 1.1f);
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(2.6f, 1.7f, 0.5f), 0f), new GradientColorKey(new Color(2.2f, 0.5f, 0.12f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.5f), new GradientAlphaKey(0f, 1f) });
            var colorOverLifetime = flame.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);
            var sizeOverLifetime = flame.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve shrink = new AnimationCurve();
            shrink.AddKey(0f, 1f);
            shrink.AddKey(1f, 0.25f);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, shrink);
            SetupParticleRenderer(flame, particleMat);

            Light light = flameGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1.00f, 0.60f, 0.30f);
            light.intensity = 1.6f;
            light.range = 6f;
            light.shadows = LightShadows.None;
        }

        static void BuildBeachCamp(Transform parent)
        {
            Vector3 firePos = GroundPoint(14f, 74f, 0f);
            GameObject camp = new GameObject("Campfire");
            camp.transform.SetParent(parent, false);
            camp.transform.position = firePos;

            for (int i = 0; i < 7; i++)
            {
                float a = Mathf.PI * 2f * i / 7f;
                CreatePrimitive(camp.transform, PrimitiveType.Sphere, "Stone", rockMaterial,
                    new Vector3(Mathf.Cos(a) * 0.85f, 0.08f, Mathf.Sin(a) * 0.85f), Quaternion.identity, new Vector3(0.30f, 0.20f, 0.30f));
            }
            for (int i = 0; i < 3; i++)
            {
                float a = Mathf.PI * 2f * i / 3f;
                CreatePrimitive(camp.transform, PrimitiveType.Cylinder, "Log", darkWoodMaterial,
                    new Vector3(Mathf.Cos(a) * 0.18f, 0.22f, Mathf.Sin(a) * 0.18f),
                    Quaternion.Euler(Mathf.Cos(a) * 68f, 0f, Mathf.Sin(a) * 68f), new Vector3(0.16f, 0.55f, 0.16f));
            }

            Material particleMat = LoadParticleMaterial();
            GameObject flameGo = new GameObject("Flame");
            flameGo.transform.SetParent(camp.transform, false);
            flameGo.transform.localPosition = new Vector3(0f, 0.30f, 0f);
            ParticleSystem flame = flameGo.AddComponent<ParticleSystem>();
            flame.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = flame.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.32f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(2.6f, 1.6f, 0.5f), new Color(2.2f, 0.8f, 0.2f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 60;
            var emission = flame.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(40f);
            var shape = flame.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.10f;
            var velocity = flame.velocityOverLifetime;
            velocity.enabled = true;
            velocity.y = new ParticleSystem.MinMaxCurve(0.8f, 1.2f);
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(2.6f, 1.7f, 0.5f), 0f), new GradientColorKey(new Color(2.2f, 0.5f, 0.12f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.5f), new GradientAlphaKey(0f, 1f) });
            var colorOverLifetime = flame.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);
            var sizeOverLifetime = flame.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve shrink = new AnimationCurve();
            shrink.AddKey(0f, 1f);
            shrink.AddKey(1f, 0.25f);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, shrink);
            SetupParticleRenderer(flame, particleMat);

            Light light = flameGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1.00f, 0.55f, 0.25f);
            light.intensity = 2.2f;
            light.range = 8f;
            light.shadows = LightShadows.None;

            CreatePrimitive(camp.transform, PrimitiveType.Sphere, "Glow", glowMaterial,
                new Vector3(0f, 0.40f, 0f), Quaternion.identity, Vector3.one * 0.6f);

            GameObject smokeGo = new GameObject("Smoke");
            smokeGo.transform.SetParent(camp.transform, false);
            smokeGo.transform.localPosition = new Vector3(0f, 1.00f, 0f);
            ParticleSystem smoke = smokeGo.AddComponent<ParticleSystem>();
            smoke.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var sMain = smoke.main;
            sMain.loop = true;
            sMain.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 2.8f);
            sMain.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
            sMain.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
            sMain.startColor = new ParticleSystem.MinMaxGradient(new Color(0.45f, 0.42f, 0.45f, 0.35f));
            sMain.simulationSpace = ParticleSystemSimulationSpace.World;
            sMain.maxParticles = 30;
            var sEmission = smoke.emission;
            sEmission.rateOverTime = new ParticleSystem.MinMaxCurve(9f);
            var sShape = smoke.shape;
            sShape.shapeType = ParticleSystemShapeType.Sphere;
            sShape.radius = 0.12f;
            var sVelocity = smoke.velocityOverLifetime;
            sVelocity.enabled = true;
            sVelocity.y = new ParticleSystem.MinMaxCurve(0.4f, 0.6f);
            Gradient sGradient = new Gradient();
            sGradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.3f, 0f), new GradientAlphaKey(0f, 1f) });
            var sColor = smoke.colorOverLifetime;
            sColor.enabled = true;
            sColor.color = new ParticleSystem.MinMaxGradient(sGradient);
            SetupParticleRenderer(smoke, particleMat);

            BuildUmbrella(parent, GroundPoint(18f, 78f, 0f), canopyCoralMaterial, 12f);
            BuildUmbrella(parent, GroundPoint(8f, 70f, 0f), canopyWhiteMaterial, -9f);

            CreatePrimitive(parent, PrimitiveType.Cube, "Towel", towelCoralMaterial,
                GroundPoint(21f, 74f, 0.04f), Quaternion.Euler(0f, 28f, 0f), new Vector3(0.75f, 0.03f, 1.60f));
            CreatePrimitive(parent, PrimitiveType.Cube, "Towel", towelYellowMaterial,
                GroundPoint(4.5f, 66.5f, 0.04f), Quaternion.Euler(0f, -14f, 0f), new Vector3(0.75f, 0.03f, 1.60f));
        }

        static void BuildUmbrella(Transform parent, Vector3 position, Material canopy, float tiltDeg)
        {
            GameObject umbrella = new GameObject("Umbrella");
            umbrella.transform.SetParent(parent, false);
            umbrella.transform.position = position;

            CreatePrimitive(umbrella.transform, PrimitiveType.Cylinder, "Pole", darkWoodMaterial,
                new Vector3(0f, 1.15f, 0f), Quaternion.identity, new Vector3(0.09f, 1.15f, 0.09f));

            GameObject top = new GameObject("Canopy");
            top.transform.SetParent(umbrella.transform, false);
            top.transform.localPosition = new Vector3(0f, 2.28f, 0f);
            top.transform.localRotation = Quaternion.Euler(0f, 0f, tiltDeg);
            MeshFilter mf = top.AddComponent<MeshFilter>();
            mf.sharedMesh = umbrellaCanopyMesh;
            MeshRenderer mr = top.AddComponent<MeshRenderer>();
            mr.sharedMaterial = canopy;
            top.transform.localScale = new Vector3(1.5f, 0.9f, 1.5f);
        }

        static void BuildBoat(Transform parent)
        {
            GameObject boat = new GameObject("Sailboat");
            boat.transform.SetParent(parent, false);
            boat.transform.position = new Vector3(-70f, 0f, 35f);
            boat.transform.rotation = Quaternion.Euler(0f, 35f, 0f);

            CreatePrimitive(boat.transform, PrimitiveType.Cube, "Hull", hullMaterial,
                new Vector3(0f, 0.12f, 0f), Quaternion.identity, new Vector3(3.2f, 0.7f, 1.3f));
            CreatePrimitive(boat.transform, PrimitiveType.Cube, "Stripe", flagMaterial,
                new Vector3(0f, 0.30f, 0f), Quaternion.identity, new Vector3(3.24f, 0.14f, 1.34f));
            CreatePrimitive(boat.transform, PrimitiveType.Cube, "Cabin", woodMaterial,
                new Vector3(-0.6f, 0.62f, 0f), Quaternion.identity, new Vector3(0.9f, 0.45f, 0.8f));
            CreatePrimitive(boat.transform, PrimitiveType.Cylinder, "Mast", darkWoodMaterial,
                new Vector3(0.3f, 2.2f, 0f), Quaternion.identity, new Vector3(0.10f, 1.60f, 0.10f));

            GameObject sail = new GameObject("Sail");
            sail.transform.SetParent(boat.transform, false);
            sail.transform.localPosition = new Vector3(0.26f, 0.90f, 0f);
            sail.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            MeshFilter mf = sail.AddComponent<MeshFilter>();
            mf.sharedMesh = sailMesh;
            MeshRenderer mr = sail.AddComponent<MeshRenderer>();
            mr.sharedMaterial = sailMaterial;
            sail.transform.localScale = new Vector3(1.05f, 1.0f, 1f);

            GameObject flag = new GameObject("Flag");
            flag.transform.SetParent(boat.transform, false);
            flag.transform.localPosition = new Vector3(0.3f, 3.62f, 0f);
            MeshFilter fm = flag.AddComponent<MeshFilter>();
            fm.sharedMesh = flagMesh;
            MeshRenderer fr = flag.AddComponent<MeshRenderer>();
            fr.sharedMaterial = flagMaterial;
        }

        static void BuildBuoy(Transform parent)
        {
            GameObject buoy = new GameObject("Buoy");
            buoy.transform.SetParent(parent, false);
            buoy.transform.position = new Vector3(-55f, 0f, 45f);
            CreatePrimitive(buoy.transform, PrimitiveType.Sphere, "Bottom", hullMaterial,
                new Vector3(0f, -0.05f, 0f), Quaternion.identity, new Vector3(0.5f, 0.42f, 0.5f));
            CreatePrimitive(buoy.transform, PrimitiveType.Sphere, "Top", flagMaterial,
                new Vector3(0f, 0.14f, 0f), Quaternion.identity, new Vector3(0.46f, 0.34f, 0.46f));
        }

        static void BuildClouds(Transform parent, System.Random rng)
        {
            for (int i = 0; i < 9; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 130f + (float)rng.NextDouble() * 110f;
                Vector3 center = new Vector3(Mathf.Cos(angle) * radius, 30f + (float)rng.NextDouble() * 16f, Mathf.Sin(angle) * radius);
                GameObject cloud = new GameObject("Cloud");
                cloud.transform.SetParent(parent, false);
                cloud.transform.position = center;
                int puffs = 3 + rng.Next(3);
                for (int p = 0; p < puffs; p++)
                {
                    float sx = 4f + (float)rng.NextDouble() * 6f;
                    CreatePrimitive(cloud.transform, PrimitiveType.Sphere, "Puff", cloudMaterial,
                        new Vector3((float)rng.NextDouble() * 8f - 4f, (float)rng.NextDouble() * 1.6f, (float)rng.NextDouble() * 5f - 2.5f),
                        Quaternion.identity, new Vector3(sx, sx * 0.38f, sx * 0.7f));
                }
            }
        }

        static void MarkStatic(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = true;
        }

        static void CaptureShots()
        {
            GameObject camGo = new GameObject("ShotCamera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1500f;

            float heroGroundY = IslandHeight(20f, 82f);
            cam.fieldOfView = 60f;
            camGo.transform.position = new Vector3(20f, heroGroundY + 1.7f, 82f);
            camGo.transform.LookAt(new Vector3(-41f, 5f, 47f));
            Capture(cam, "Assets/Screenshots/SunsetIsland_Hero.png", 1600, 900);

            cam.fieldOfView = 45f;
            camGo.transform.position = new Vector3(110f, 70f, 110f);
            camGo.transform.LookAt(Vector3.zero);
            Capture(cam, "Assets/Screenshots/SunsetIsland_Overview.png", 1600, 900);

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

        static Material LoadParticleMaterial()
        {
            Material mat = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Particle.mat");
            if (mat != null) return mat;
            return new Material(Shader.Find("Particles/Standard Unlit"));
        }

        static void SetupParticleRenderer(ParticleSystem ps, Material mat)
        {
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = mat;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
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
