using UnityEngine;

namespace BigWorldClient
{
    /// <summary>
    /// 基于分离轴定理 (SAT) 的三角形-AABB 相交测试
    /// </summary>
    public static class SATIntersection
    {
        /// <summary>
        /// 测试三角形与轴对齐包围盒(AABB)是否相交
        /// </summary>
        /// <param name="v0">三角形顶点0（世界空间）</param>
        /// <param name="v1">三角形顶点1（世界空间）</param>
        /// <param name="v2">三角形顶点2（世界空间）</param>
        /// <param name="boxCenter">AABB中心（世界空间）</param>
        /// <param name="boxHalfExtents">AABB半边长</param>
        /// <returns>true=相交</returns>
        public static bool TriangleAABBIntersect(
            Vector3 v0, Vector3 v1, Vector3 v2,
            Vector3 boxCenter, Vector3 boxHalfExtents)
        {
            // 快速剔除：三角形AABB与体素AABB是否重叠
            if (!AABBOverlap(v0, v1, v2, boxCenter, boxHalfExtents))
                return false;

            // 计算三角形边
            Vector3 e0 = v1 - v0;
            Vector3 e1 = v2 - v1;
            Vector3 e2 = v0 - v2;

            // 将三角形平移到AABB局部空间（简化后续投影计算）
            Vector3 t0 = v0 - boxCenter;
            Vector3 t1 = v1 - boxCenter;
            Vector3 t2 = v2 - boxCenter;

            Vector3 h = boxHalfExtents;

            // ---- 测试1: AABB三个面法线 (x, y, z) ----
            // X轴
            if (TestAxisX(t0, t1, t2, h.x)) return false;
            // Y轴
            if (TestAxisY(t0, t1, t2, h.y)) return false;
            // Z轴
            if (TestAxisZ(t0, t1, t2, h.z)) return false;

            // ---- 测试2: 三角形面法线 ----
            Vector3 triNormal = Vector3.Cross(e0, e1);
            if (triNormal.sqrMagnitude > 1e-12f)
            {
                // 投影三角形到面法线
                float triProj = Vector3.Dot(t0, triNormal);
                // AABB投影到面法线
                float r = h.x * Mathf.Abs(triNormal.x)
                        + h.y * Mathf.Abs(triNormal.y)
                        + h.z * Mathf.Abs(triNormal.z);
                if (Mathf.Abs(triProj) > r)
                    return false;
            }

            // ---- 测试3: 边 × AABB轴 的9个叉积轴 ----
            // e0 × (1,0,0), e0 × (0,1,0), e0 × (0,0,1)
            if (TestEdgeAxis(t0, t1, t2, e0, h)) return false;
            // e1 × (1,0,0), e1 × (0,1,0), e1 × (0,0,1)
            if (TestEdgeAxis(t0, t1, t2, e1, h)) return false;
            // e2 × (1,0,0), e2 × (0,1,0), e2 × (0,0,1)
            if (TestEdgeAxis(t0, t1, t2, e2, h)) return false;

            return true;
        }

        /// <summary>
        /// 测试三角形AABB与体素AABB是否重叠（快速剔除）
        /// </summary>
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

        // ---- 优化: 轴对齐测试直接比较分量 ----

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

        /// <summary>
        /// 测试边e与三个AABB轴叉积产生的3个轴
        /// </summary>
        private static bool TestEdgeAxis(Vector3 t0, Vector3 t1, Vector3 t2,
            Vector3 edge, Vector3 h)
        {
            // axis = cross(edge, (1,0,0)) = (0, edge.z, -edge.y)
            float ax0 = 0f, ay0 = edge.z, az0 = -edge.y;
            if (TestCrossAxis(t0, t1, t2, ax0, ay0, az0, h))
                return true;

            // axis = cross(edge, (0,1,0)) = (-edge.z, 0, edge.x)
            float ax1 = -edge.z, ay1 = 0f, az1 = edge.x;
            if (TestCrossAxis(t0, t1, t2, ax1, ay1, az1, h))
                return true;

            // axis = cross(edge, (0,0,1)) = (edge.y, -edge.x, 0)
            float ax2 = edge.y, ay2 = -edge.x, az2 = 0f;
            if (TestCrossAxis(t0, t1, t2, ax2, ay2, az2, h))
                return true;

            return false;
        }

        /// <summary>
        /// 测试给定叉积轴上的投影是否分离
        /// </summary>
        /// <returns>true=在该轴上分离（不相交）</returns>
        private static bool TestCrossAxis(Vector3 t0, Vector3 t1, Vector3 t2,
            float ax, float ay, float az, Vector3 h)
        {
            // 跳过退化轴
            float sqrLen = ax * ax + ay * ay + az * az;
            if (sqrLen < 1e-12f)
                return false;

            // 三角形投影
            float p0 = t0.x * ax + t0.y * ay + t0.z * az;
            float p1 = t1.x * ax + t1.y * ay + t1.z * az;
            float p2 = t2.x * ax + t2.y * ay + t2.z * az;

            float triMin = Mathf.Min(p0, Mathf.Min(p1, p2));
            float triMax = Mathf.Max(p0, Mathf.Max(p1, p2));

            // AABB投影: r = hx·|ax| + hy·|ay| + hz·|az|
            float r = h.x * Mathf.Abs(ax)
                    + h.y * Mathf.Abs(ay)
                    + h.z * Mathf.Abs(az);

            // 分离判定
            return triMin > r || triMax < -r;
        }
    }
}
