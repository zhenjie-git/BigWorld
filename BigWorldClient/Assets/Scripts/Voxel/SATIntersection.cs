using UnityEngine;

namespace BigWorldClient
{

    public static class SATIntersection
    {

        public static bool TriangleAABBIntersect(
            Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 boxCenter, Vector3 boxHalfExtents)
        {

            if (!AABBOverlap(v0, v1, v2, boxCenter, boxHalfExtents))
                return false;

            Vector3 e0 = v1 - v0;
            Vector3 e1 = v2 - v1;
            Vector3 e2 = v0 - v2;

            Vector3 t0 = v0 - boxCenter;
            Vector3 t1 = v1 - boxCenter;
            Vector3 t2 = v2 - boxCenter;

            Vector3 h = boxHalfExtents;

            if (TestAxisX(t0, t1, t2, h.x)) return false;

            if (TestAxisY(t0, t1, t2, h.y)) return false;

            if (TestAxisZ(t0, t1, t2, h.z)) return false;

            Vector3 triNormal = Vector3.Cross(e0, e1);
            if (triNormal.sqrMagnitude > 1e-12f)
            {

                float triProj = Vector3.Dot(t0, triNormal);

                float r = h.x * Mathf.Abs(triNormal.x)
                        + h.y * Mathf.Abs(triNormal.y)
                        + h.z * Mathf.Abs(triNormal.z);
                if (Mathf.Abs(triProj) > r)
                    return false;
            }

            if (TestEdgeAxis(t0, t1, t2, e0, h)) return false;

            if (TestEdgeAxis(t0, t1, t2, e1, h)) return false;

            if (TestEdgeAxis(t0, t1, t2, e2, h)) return false;

            return true;
        }

        private static bool AABBOverlap(Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 boxCenter, Vector3 boxHalfExtents)
        {
            Vector3 triMin = Vector3.Min(Vector3.Min(v0, v1), v2);
            Vector3 triMax = Vector3.Max(Vector3.Max(v0, v1), v2);
            Vector3 boxMin = boxCenter - boxHalfExtents;
            Vector3 boxMax = boxCenter + boxHalfExtents;

            return triMin.x <= boxMax.x && triMax.x >= boxMin.x
                && triMin.y <= boxMax.y && triMax.y >= boxMin.y
                && triMin.z <= boxMax.z && triMax.z >= boxMin.z;
        }

        private static bool TestAxisX(Vector3 t0, Vector3 t1, Vector3 t2, float hx)
        {
            float min = Mathf.Min(t0.x, Mathf.Min(t1.x, t2.x));
            float max = Mathf.Max(t0.x, Mathf.Max(t1.x, t2.x));
            return min > hx || max < -hx;
        }

        private static bool TestAxisY(Vector3 t0, Vector3 t1, Vector3 t2, float hy)
        {
            float min = Mathf.Min(t0.y, Mathf.Min(t1.y, t2.y));
            float max = Mathf.Max(t0.y, Mathf.Max(t1.y, t2.y));
            return min > hy || max < -hy;
        }

        private static bool TestAxisZ(Vector3 t0, Vector3 t1, Vector3 t2, float hz)
        {
            float min = Mathf.Min(t0.z, Mathf.Min(t1.z, t2.z));
            float max = Mathf.Max(t0.z, Mathf.Max(t1.z, t2.z));
            return min > hz || max < -hz;
        }

        private static bool TestEdgeAxis(Vector3 t0, Vector3 t1, Vector3 t2,
            Vector3 edge, Vector3 h)
        {

            float ax0 = 0f, ay0 = edge.z, az0 = -edge.y;
            if (TestCrossAxis(t0, t1, t2, ax0, ay0, az0, h))
                return true;

            float ax1 = -edge.z, ay1 = 0f, az1 = edge.x;
            if (TestCrossAxis(t0, t1, t2, ax1, ay1, az1, h))
                return true;

            float ax2 = edge.y, ay2 = -edge.x, az2 = 0f;
            if (TestCrossAxis(t0, t1, t2, ax2, ay2, az2, h))
                return true;

            return false;
        }

        private static bool TestCrossAxis(Vector3 t0, Vector3 t1, Vector3 t2,
            float ax, float ay, float az, Vector3 h)
        {

            float sqrLen = ax * ax + ay * ay + az * az;
            if (sqrLen < 1e-12f)
                return false;

            float p0 = t0.x * ax + t0.y * ay + t0.z * az;
            float p1 = t1.x * ax + t1.y * ay + t1.z * az;
            float p2 = t2.x * ax + t2.y * ay + t2.z * az;

            float triMin = Mathf.Min(p0, Mathf.Min(p1, p2));
            float triMax = Mathf.Max(p0, Mathf.Max(p1, p2));

            float r = h.x * Mathf.Abs(ax)
                    + h.y * Mathf.Abs(ay)
                    + h.z * Mathf.Abs(az);

            return triMin > r || triMax < -r;
        }
    }
}
