using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BigWorldClient
{
    public static class WorldPortalVisuals
    {
        struct PortalPoint
        {
            public string SceneId;
            public Vector3 Position;
            public float Yaw;
            public string Label;
        }

        static readonly PortalPoint[] Points =
        {
            new PortalPoint
            {
                SceneId = "MainCity",
                Position = new Vector3(8.5f, 0f, 8.5f),
                Yaw = 45f,
                Label = "TO COLORFUL TOWN",
            },
            new PortalPoint
            {
                SceneId = "ColorfulTown",
                Position = new Vector3(0f, 0f, -30f),
                Yaw = 0f,
                Label = "TO MAIN CITY",
            },
        };

        static Material _baseMaterial;
        static Material _beamMaterial;
        static Material _ringMaterial;
        static Material _arcMaterial;
        static Material _orbMaterial;
        static Mesh _ringMesh;
        static Mesh _arcMesh;

        public static void EnsureForScene(string sceneId)
        {
            if (string.IsNullOrEmpty(sceneId)) return;

            for (int i = 0; i < Points.Length; i++)
            {
                if (Points[i].SceneId != sceneId) continue;
                string rootName = "Portal_" + sceneId;
                if (GameObject.Find(rootName) != null) return;
                Create(Points[i], rootName);
                return;
            }
        }

        static void Create(PortalPoint point, string rootName)
        {
            GameObject root = new GameObject(rootName);
            root.transform.position = point.Position;
            root.transform.rotation = Quaternion.Euler(0f, point.Yaw, 0f);

            CreatePrimitive(root.transform, PrimitiveType.Cylinder, "BaseDisc", new Vector3(0f, 0.02f, 0f), new Vector3(3.2f, 0.02f, 3.2f), BaseMaterial());
            CreatePrimitive(root.transform, PrimitiveType.Cylinder, "Beam", new Vector3(0f, 2.3f, 0f), new Vector3(2.1f, 2.3f, 2.1f), BeamMaterial());

            Quaternion vertical = Quaternion.Euler(90f, 0f, 0f);
            CreateMeshObject(root.transform, "PortalRing", GetRingMesh(), RingMaterial(), new Vector3(0f, 1.75f, 0f), vertical);
            GameObject arc = CreateMeshObject(root.transform, "PortalArc", GetArcMesh(), ArcMaterial(), new Vector3(0f, 1.75f, 0f), vertical);
            GameObject orb = CreatePrimitive(root.transform, PrimitiveType.Sphere, "Orb", new Vector3(0f, 1.75f, 0f), Vector3.one * 0.32f, OrbMaterial());

            GameObject runes = new GameObject("Runes");
            runes.transform.SetParent(root.transform, false);
            runes.transform.localPosition = new Vector3(0f, 0.22f, 0f);
            for (int i = 0; i < 4; i++)
            {
                float angle = 45f + i * 90f;
                float rad = angle * Mathf.Deg2Rad;
                Vector3 pos = new Vector3(Mathf.Cos(rad) * 1.65f, 0f, Mathf.Sin(rad) * 1.65f);
                CreatePrimitive(runes.transform, PrimitiveType.Sphere, "Rune", pos, Vector3.one * 0.16f, OrbMaterial());
            }

            GameObject lightObject = new GameObject("GlowLight");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.localPosition = new Vector3(0f, 1.75f, 0f);
            Light glowLight = lightObject.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = new Color(0.3f, 0.92f, 1f);
            glowLight.intensity = 2.6f;
            glowLight.range = 9f;
            glowLight.shadows = LightShadows.None;

            Transform label = CreateLabel(root.transform, point.Label);

            PortalVisualFx fx = root.AddComponent<PortalVisualFx>();
            fx.Ring = root.transform.Find("PortalRing");
            fx.Arc = arc.transform;
            fx.Orb = orb.transform;
            fx.Runes = runes.transform;
            fx.Beam = root.transform.Find("Beam");
            fx.Label = label;
            fx.GlowLight = glowLight;
        }

        static GameObject CreatePrimitive(Transform parent, PrimitiveType type, string name, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return go;
        }

        static GameObject CreateMeshObject(Transform parent, string name, Mesh mesh, Material material, Vector3 localPosition, Quaternion localRotation)
        {
            GameObject go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;

            MeshFilter filter = go.GetComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        static Transform CreateLabel(Transform parent, string text)
        {
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", 48);
            if (font == null) return null;

            GameObject go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 3.55f, 0f);

            TextMesh mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = font;
            mesh.fontSize = 40;
            mesh.characterSize = 0.38f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.fontStyle = FontStyle.Bold;
            mesh.color = new Color(0.78f, 0.97f, 1f);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = font.material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return go.transform;
        }

        static Mesh GetRingMesh()
        {
            if (_ringMesh == null) _ringMesh = BuildRingMesh(1.62f, 1.92f, 72, 360f);
            return _ringMesh;
        }

        static Mesh GetArcMesh()
        {
            if (_arcMesh == null) _arcMesh = BuildRingMesh(1.28f, 1.52f, 64, 286f);
            return _arcMesh;
        }

        static Mesh BuildRingMesh(float innerRadius, float outerRadius, int segments, float arcDegrees)
        {
            int steps = Mathf.Max(8, segments);
            bool closed = arcDegrees >= 359.9f;
            int count = closed ? steps : steps + 1;
            var vertices = new List<Vector3>(count * 2);
            var triangles = new List<int>(steps * 6);
            float arc = arcDegrees * Mathf.Deg2Rad;

            for (int i = 0; i < count; i++)
            {
                float t = (float)i / steps;
                float angle = arc * t;
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);
                vertices.Add(new Vector3(sin * innerRadius, 0f, cos * innerRadius));
                vertices.Add(new Vector3(sin * outerRadius, 0f, cos * outerRadius));
            }

            for (int i = 0; i < steps; i++)
            {
                int current = i * 2;
                int next = closed ? ((i + 1) % steps) * 2 : current + 2;
                triangles.Add(current);
                triangles.Add(current + 1);
                triangles.Add(next);
                triangles.Add(current + 1);
                triangles.Add(next + 1);
                triangles.Add(next);
            }

            Mesh mesh = new Mesh();
            mesh.name = "PortalRing";
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material BaseMaterial()
        {
            if (_baseMaterial == null) _baseMaterial = CreateGlowMaterial(new Color(0.25f, 0.92f, 1f, 0.16f));
            return _baseMaterial;
        }

        static Material BeamMaterial()
        {
            if (_beamMaterial == null) _beamMaterial = CreateGlowMaterial(new Color(0.25f, 0.82f, 1f, 0.08f));
            return _beamMaterial;
        }

        static Material RingMaterial()
        {
            if (_ringMaterial == null) _ringMaterial = CreateGlowMaterial(new Color(0.35f, 0.95f, 1f, 0.95f));
            return _ringMaterial;
        }

        static Material ArcMaterial()
        {
            if (_arcMaterial == null) _arcMaterial = CreateGlowMaterial(new Color(0.7f, 0.48f, 1f, 0.95f));
            return _arcMaterial;
        }

        static Material OrbMaterial()
        {
            if (_orbMaterial == null) _orbMaterial = CreateGlowMaterial(new Color(1f, 0.78f, 0.45f, 0.95f));
            return _orbMaterial;
        }

        static Material CreateGlowMaterial(Color color)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Standard");

            Material material = new Material(shader);
            material.color = color;
            material.renderQueue = 3000;
            return material;
        }
    }

    public sealed class PortalVisualFx : MonoBehaviour
    {
        public Transform Ring;
        public Transform Arc;
        public Transform Orb;
        public Transform Runes;
        public Transform Beam;
        public Transform Label;
        public Light GlowLight;

        float _phase;

        void Awake()
        {
            _phase = Random.value * 10f;
        }

        void Update()
        {
            float time = Time.time + _phase;
            float dt = Time.deltaTime;

            if (Ring != null)
            {
                float pulse = 1f + Mathf.Sin(time * 2.2f) * 0.025f;
                Ring.localScale = new Vector3(pulse, pulse, pulse);
            }
            if (Arc != null) Arc.Rotate(0f, 70f * dt, 0f, Space.Self);
            if (Runes != null) Runes.Rotate(0f, -35f * dt, 0f, Space.Self);
            if (Orb != null)
            {
                Orb.localPosition = new Vector3(0f, 1.75f + Mathf.Sin(time * 2.4f) * 0.12f, 0f);
                Orb.localRotation = Quaternion.Euler(0f, time * 90f, 0f);
            }
            if (Beam != null)
            {
                float pulse = 1f + Mathf.Sin(time * 1.7f) * 0.04f;
                Beam.localScale = new Vector3(2.1f * pulse, 2.3f, 2.1f * pulse);
            }
            if (GlowLight != null)
                GlowLight.intensity = 2.6f + Mathf.Sin(time * 3f) * 0.7f;
            if (Label != null && Camera.main != null)
                Label.rotation = Quaternion.LookRotation(Label.position - Camera.main.transform.position, Vector3.up);
        }
    }
}
