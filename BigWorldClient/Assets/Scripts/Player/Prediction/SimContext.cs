using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class SimContext
    {
        public const double MaxMoveDelta = 0.6;
        private const double AirborneMargin = 1.0;
        private const double AirborneCeiling = 3.0;

        public List<VoxelGridData> Grids = new();
        public double MaxStep;
        public double SprintToRunTime;
        public double FallGravityMps2;
        public double FallSpeedLimitMps;
        public double PlayerHeight;
        public double PlayerCenterY;
        public StateConfigTable Displacement;

        public VoxelGridData VoxelGrid => Grids != null && Grids.Count > 0 ? Grids[0] : null;

        public void AddGrid(VoxelGridData grid)
        {
            if (grid == null || Grids.Contains(grid))
                return;
            Grids.Add(grid);
        }

        public static SimContext Build(GameScene scene, PlayerConfigTable cfg)
        {
            var ctx = new SimContext
            {
                MaxStep = scene != null ? scene.MaxStepHeight : 0.5,
                Displacement = StateConfigTable.Instance,
                FallGravityMps2 = 10.0,
                FallSpeedLimitMps = 15.0,
                PlayerHeight = 1.8,
                PlayerCenterY = 0.9,
            };
            if (scene != null)
            {
                ctx.Grids = scene.VoxelGrids;
            }

            if (cfg == null) return ctx;

            ctx.SprintToRunTime = cfg.SprintToRunTime;
            ctx.FallGravityMps2 = cfg.Gravity;
            ctx.FallSpeedLimitMps = cfg.FallSpeedLimit;
            ctx.PlayerHeight = cfg.ColliderHeight;
            ctx.PlayerCenterY = cfg.ColliderCenterY;
            return ctx;
        }

        bool ColumnInGrid(VoxelGridData g, double wx, double wz, out int x, out int z)
        {
            x = 0;
            z = 0;
            if (g == null) return false;
            x = (int)Math.Floor((wx - g.originOffset.x) / g.voxelSize.x);
            z = (int)Math.Floor((wz - g.originOffset.z) / g.voxelSize.z);
            return x >= 0 && x < g.gridDimX && z >= 0 && z < g.gridDimZ;
        }

        bool TryGetGrid(double wx, double wz, out VoxelGridData grid, out int cx, out int cz)
        {
            grid = null;
            cx = 0;
            cz = 0;
            if (Grids == null) return false;
            foreach (VoxelGridData g in Grids)
            {
                if (ColumnInGrid(g, wx, wz, out cx, out cz))
                {
                    grid = g;
                    return true;
                }
            }
            return false;
        }

        VoxelGridData GridContainingBoth(double ax, double az, double bx, double bz, out int acx, out int acz, out int bcx, out int bcz)
        {
            acx = acz = bcx = bcz = 0;
            if (Grids == null) return null;
            foreach (VoxelGridData g in Grids)
            {
                if (!ColumnInGrid(g, ax, az, out acx, out acz))
                    continue;
                if (!ColumnInGrid(g, bx, bz, out bcx, out bcz))
                    continue;
                return g;
            }
            return null;
        }

        public void InitSpawnState(double x, double z, out int voxelK, out double y)
        {
            voxelK = -1;
            y = 0;
            if (!TryGetGrid(x, z, out VoxelGridData g, out int cx, out int cz))
                return;
            int top = TopLayerIn(g, cx, cz);
            if (top >= 0)
            {
                voxelK = top;
                y = SurfaceHeightIn(g, x, z, top);
            }
        }

        static int TopLayerIn(VoxelGridData g, int x, int z)
        {
            return g.voxelCounts[GridIndexOf(g, x, z)] - 1;
        }

        static int GridIndexOf(VoxelGridData g, int x, int z) => x + z * g.gridDimX;

        public int GridIndex(int x, int z) => GridIndexOf(VoxelGrid, x, z);

        public int TopLayerAt(int x, int z)
        {
            VoxelGridData g = VoxelGrid;
            if (g == null || x < 0 || z < 0 || x >= g.gridDimX || z >= g.gridDimZ)
                return -1;
            return TopLayerIn(g, x, z);
        }

        public bool WorldToColumn(double wx, double wz, out int x, out int z)
        {
            TryGetGrid(wx, wz, out _, out x, out z);
            return VoxelGrid != null && x >= 0 && x < VoxelGrid.gridDimX && z >= 0 && z < VoxelGrid.gridDimZ;
        }

        static double SurfaceHeightIn(VoxelGridData g, double x, double z, int k)
        {
            if (k < 0) return g.originOffset.y;
            int cx = (int)Math.Floor((x - g.originOffset.x) / g.voxelSize.x);
            int cz = (int)Math.Floor((z - g.originOffset.z) / g.voxelSize.z);
            if (cx < 0 || cx >= g.gridDimX || cz < 0 || cz >= g.gridDimZ) return g.originOffset.y;
            int idx = GridIndexOf(g, cx, cz);
            if (k >= g.voxelCounts[idx]) return g.originOffset.y;
            return g.originOffset.y + g.voxels[g.startIndices[idx] + k].maxY;
        }

        public double SurfaceHeight(double x, double z, int k)
        {
            if (!TryGetGrid(x, z, out VoxelGridData g, out _, out _))
                return VoxelGrid != null ? VoxelGrid.originOffset.y : 0;
            return SurfaceHeightIn(g, x, z, k);
        }

        public double SmoothGroundHeight(double x, double z, double refY)
        {
            if (!TryGetGrid(x, z, out VoxelGridData g, out _, out _))
                return refY;

            double fx = (x - g.originOffset.x) / g.voxelSize.x - 0.5;
            double fz = (z - g.originOffset.z) / g.voxelSize.z - 0.5;
            int x0 = (int)Math.Floor(fx);
            int z0 = (int)Math.Floor(fz);
            double tx = fx - x0;
            double tz = fz - z0;

            double h00 = CornerTop(g, x0, z0, refY);
            double h10 = CornerTop(g, x0 + 1, z0, refY);
            double h01 = CornerTop(g, x0, z0 + 1, refY);
            double h11 = CornerTop(g, x0 + 1, z0 + 1, refY);

            double wx0 = (1 - tx) * (1 - tz);
            double wx1 = tx * (1 - tz);
            double wz0 = (1 - tx) * tz;
            double wz1 = tx * tz;
            return h00 * wx0 + h10 * wx1 + h01 * wz0 + h11 * wz1;
        }

        double CornerTop(VoxelGridData g, int cx, int cz, double refY)
        {
            if (cx < 0 || cx >= g.gridDimX || cz < 0 || cz >= g.gridDimZ)
                return refY;
            int idx = GridIndexOf(g, cx, cz);
            int count = g.voxelCounts[idx];
            if (count == 0)
                return refY;
            int start = g.startIndices[idx];
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                double d = Math.Abs(g.originOffset.y + g.voxels[start + i].maxY - refY);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            double top = g.originOffset.y + g.voxels[start + best].maxY;
            if (top > refY + MaxStep || top < refY - MaxStep)
                return refY;
            return top;
        }

        public int ResolveLayerNearY(double x, double z, double refY)
        {
            if (!TryGetGrid(x, z, out VoxelGridData g, out int cx, out int cz))
                return -1;
            int idx = GridIndexOf(g, cx, cz);
            int count = g.voxelCounts[idx];
            if (count == 0) return -1;

            int best = 0;
            double bestDist = double.MaxValue;
            int start = g.startIndices[idx];
            for (int i = 0; i < count; i++)
            {
                double top = g.originOffset.y + g.voxels[start + i].maxY;
                double d = Math.Abs(top - refY);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        static int DirectionIndex(int dx, int dz)
        {
            dx = Math.Max(-1, Math.Min(1, dx));
            dz = Math.Max(-1, Math.Min(1, dz));
            if (dx == 0 && dz == 1) return VoxelConnectivity.DirUp;
            if (dx == 0 && dz == -1) return VoxelConnectivity.DirDown;
            if (dx == -1 && dz == 0) return VoxelConnectivity.DirLeft;
            if (dx == 1 && dz == 0) return VoxelConnectivity.DirRight;
            if (dx == -1 && dz == 1) return VoxelConnectivity.DirUpperLeft;
            if (dx == -1 && dz == -1) return VoxelConnectivity.DirLowerLeft;
            if (dx == 1 && dz == 1) return VoxelConnectivity.DirUpperRight;
            if (dx == 1 && dz == -1) return VoxelConnectivity.DirLowerRight;
            return -1;
        }

        static int ResolveTargetLayerIn(VoxelGridData g, int curX, int curZ, int curK, int targetX, int targetZ)
        {
            int curIdx = GridIndexOf(g, curX, curZ);
            int targetIdx = GridIndexOf(g, targetX, targetZ);
            int targetCount = g.voxelCounts[targetIdx];
            int targetStart = g.startIndices[targetIdx];
            if (curK < 0 || curK >= g.voxelCounts[curIdx]) return -1;

            VoxelData curVoxel = g.voxels[g.startIndices[curIdx] + curK];
            int dir = DirectionIndex(targetX - curX, targetZ - curZ);
            if (dir < 0) return -1;
            byte flag = VoxelConnectivity.GetFlag(curVoxel.connectivity, dir);

            switch (flag)
            {
                case VoxelConnectivity.FlagSameLayer:
                    return curK < targetCount ? curK : -1;
                case VoxelConnectivity.FlagLayerAbove:
                    return curK + 1 < targetCount ? curK + 1 : -1;
                case VoxelConnectivity.FlagLayerBelow:
                    return curK - 1 >= 0 ? curK - 1 : -1;
                default:
                    int best = -1;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < targetCount; i++)
                    {
                        double d = Math.Abs(g.voxels[targetStart + i].maxY - curVoxel.maxY);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = i;
                        }
                    }
                    return best;
            }
        }

        public int ResolveTargetLayer(int curX, int curZ, int curK, int targetX, int targetZ)
        {
            VoxelGridData g = VoxelGrid;
            if (g == null) return -1;
            return ResolveTargetLayerIn(g, curX, curZ, curK, targetX, targetZ);
        }

        public bool GroundDropAt(double fromX, double fromZ, int curK, double dx, double dz)
        {
            if (curK < 0) return false;
            VoxelGridData g = GridContainingBoth(fromX, fromZ, fromX + dx, fromZ + dz, out int curX, out int curZ, out int targetX, out int targetZ);
            if (g == null) return true;
            if (targetX == curX && targetZ == curZ) return false;

            int targetIdx = GridIndexOf(g, targetX, targetZ);
            if (g.voxelCounts[targetIdx] == 0) return true;
            int resolvedK = ResolveTargetLayerIn(g, curX, curZ, curK, targetX, targetZ);
            if (resolvedK < 0) return true;

            VoxelData curVoxel = g.voxels[g.startIndices[GridIndexOf(g, curX, curZ)] + curK];
            VoxelData targetVoxel = g.voxels[g.startIndices[targetIdx] + resolvedK];
            return curVoxel.maxY - targetVoxel.maxY > MaxStep;
        }

        public bool ValidateGroundMove(double fromX, double fromZ, int curK, double dx, double dz,
            double maxDelta, double refY, out int newK, out double newY)
        {
            newK = curK;
            newY = 0;
            if (Math.Sqrt(dx * dx + dz * dz) > maxDelta) return false;
            if (VoxelGrid == null)
            {
                newY = 0;
                return true;
            }

            VoxelGridData any = VoxelGrid;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            int steps = (int)Math.Ceiling(dist / (any.voxelSize.x * 0.5));
            if (steps < 1) steps = 1;

            double px = fromX;
            double pz = fromZ;
            int k = curK;
            for (int i = 1; i <= steps; i++)
            {
                double nx = fromX + dx * i / steps;
                double nz = fromZ + dz * i / steps;
                if (!ValidateMove(px, pz, k, nx - px, nz - pz, out k))
                    return false;
                px = nx;
                pz = nz;
            }
            newK = k;
            newY = SmoothGroundHeight(px, pz, refY);
            return true;
        }

        bool ValidateMove(double fromX, double fromZ, int curK, double dx, double dz, out int resolvedK)
        {
            resolvedK = curK;
            VoxelGridData g = GridContainingBoth(fromX, fromZ, fromX + dx, fromZ + dz, out int curX, out int curZ, out int targetX, out int targetZ);
            if (g == null) return false;
            if (Math.Abs(targetX - curX) > 1 || Math.Abs(targetZ - curZ) > 1) return false;

            int targetIdx = GridIndexOf(g, targetX, targetZ);
            int targetCount = g.voxelCounts[targetIdx];
            if (targetCount == 0) return false;

            int curIdx = GridIndexOf(g, curX, curZ);
            int curCount = g.voxelCounts[curIdx];
            if (curK < 0 || curK >= curCount) return false;
            VoxelData curVoxel = g.voxels[g.startIndices[curIdx] + curK];

            int resolved = curK;
            if (targetX != curX || targetZ != curZ)
            {
                resolved = ResolveTargetLayerIn(g, curX, curZ, curK, targetX, targetZ);
                if (resolved < 0 || resolved >= targetCount) return false;
            }

            VoxelData targetVoxel = g.voxels[g.startIndices[targetIdx] + resolved];
            double heightDiff = targetVoxel.maxY - curVoxel.maxY;
            if (heightDiff > MaxStep) return false;
            resolvedK = resolved;
            return true;
        }

        public bool CeilingHit(double x, double z, double feetY, double headTopY, out double ceilingY)
        {
            ceilingY = 0;
            if (!TryGetGrid(x, z, out VoxelGridData g, out int cx, out int cz)) return false;
            int idx = GridIndexOf(g, cx, cz);
            int count = g.voxelCounts[idx];
            if (count == 0) return false;

            int start = g.startIndices[idx];
            double originY = g.originOffset.y;
            double ceiling = double.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                VoxelData v = g.voxels[start + i];
                double minY = originY + v.minY;
                double maxY = originY + v.maxY;

                if (minY > feetY + 0.01
                    && minY < headTopY
                    && headTopY > minY
                    && headTopY < maxY)
                {
                    ceiling = minY;
                    found = true;
                }
            }
            ceilingY = ceiling;
            return found;
        }
    }
}
