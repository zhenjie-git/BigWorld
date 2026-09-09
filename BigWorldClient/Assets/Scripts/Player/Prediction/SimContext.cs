using System;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class SimContext
    {
        public const double MaxMoveDelta = 0.6;
        private const double AirborneMargin = 1.0;
        private const double AirborneCeiling = 3.0;

        public VoxelGridData VoxelGrid;
        public double MaxStep;
        public double SprintToRunTime;
        public double FallGravityMps2;
        public double FallSpeedLimitMps;
        public double PlayerHeight;
        public double PlayerCenterY;
        public StateConfigTable Displacement;

        public static SimContext Build(GameScene scene, PlayerConfigTable cfg)
        {
            var ctx = new SimContext
            {
                VoxelGrid = scene?.VoxelGridData,
                MaxStep = scene != null ? scene.MaxStepHeight : 0.5,
                Displacement = StateConfigTable.Instance,
                FallGravityMps2 = 10.0,
                FallSpeedLimitMps = 15.0,
                PlayerHeight = 1.8,
                PlayerCenterY = 0.9,
            };

            if (cfg == null) return ctx;

            ctx.SprintToRunTime = cfg.SprintToRunTime;
            ctx.FallGravityMps2 = cfg.Gravity;
            ctx.FallSpeedLimitMps = cfg.FallSpeedLimit;
            ctx.PlayerHeight = cfg.ColliderHeight;
            ctx.PlayerCenterY = cfg.ColliderCenterY;
            return ctx;
        }

        public void InitSpawnState(double x, double z, out int voxelK, out double y)
        {
            voxelK = -1;
            y = 0;
            if (VoxelGrid == null) return;

            if (WorldToColumn(x, z, out int cx, out int cz))
            {
                int top = TopLayerAt(cx, cz);
                if (top >= 0)
                {
                    voxelK = top;
                    y = SurfaceHeight(x, z, top);
                }
            }
        }

        public int TopLayerAt(int x, int z)
        {
            if (VoxelGrid == null || x < 0 || z < 0 || x >= VoxelGrid.gridDimX || z >= VoxelGrid.gridDimZ)
                return -1;
            int idx = GridIndex(x, z);
            return VoxelGrid.voxelCounts[idx] - 1;
        }

        public int GridIndex(int x, int z) => x + z * VoxelGrid.gridDimX;

        public bool WorldToColumn(double wx, double wz, out int x, out int z)
        {
            x = 0;
            z = 0;
            if (VoxelGrid == null) return false;
            x = (int)Math.Floor((wx - VoxelGrid.originOffset.x) / VoxelGrid.voxelSize.x);
            z = (int)Math.Floor((wz - VoxelGrid.originOffset.z) / VoxelGrid.voxelSize.z);
            return x >= 0 && x < VoxelGrid.gridDimX && z >= 0 && z < VoxelGrid.gridDimZ;
        }

        public double SurfaceHeight(double x, double z, int k)
        {
            if (VoxelGrid == null) return 0;
            if (k < 0) return VoxelGrid.originOffset.y;
            if (!WorldToColumn(x, z, out int cx, out int cz)) return VoxelGrid.originOffset.y;
            int idx = GridIndex(cx, cz);
            if (k >= VoxelGrid.voxelCounts[idx]) return VoxelGrid.originOffset.y;
            return VoxelGrid.originOffset.y + VoxelGrid.voxels[VoxelGrid.startIndices[idx] + k].maxY;
        }

        public int ResolveLayerNearY(double x, double z, double refY)
        {
            if (VoxelGrid == null || !WorldToColumn(x, z, out int cx, out int cz)) return -1;
            int idx = GridIndex(cx, cz);
            int count = VoxelGrid.voxelCounts[idx];
            if (count == 0) return -1;

            int best = 0;
            double bestDist = double.MaxValue;
            int start = VoxelGrid.startIndices[idx];
            for (int i = 0; i < count; i++)
            {
                double top = VoxelGrid.originOffset.y + VoxelGrid.voxels[start + i].maxY;
                double d = Math.Abs(top - refY);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        private static int DirectionIndex(int dx, int dz)
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

        public int ResolveTargetLayer(int curX, int curZ, int curK, int targetX, int targetZ)
        {
            if (VoxelGrid == null) return -1;
            int curIdx = GridIndex(curX, curZ);
            int targetIdx = GridIndex(targetX, targetZ);
            int targetCount = VoxelGrid.voxelCounts[targetIdx];
            int targetStart = VoxelGrid.startIndices[targetIdx];
            if (curK < 0 || curK >= VoxelGrid.voxelCounts[curIdx]) return -1;

            VoxelData curVoxel = VoxelGrid.voxels[VoxelGrid.startIndices[curIdx] + curK];
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
                        double d = Math.Abs(VoxelGrid.voxels[targetStart + i].maxY - curVoxel.maxY);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = i;
                        }
                    }
                    return best;
            }
        }

        public bool GroundDropAt(double fromX, double fromZ, int curK, double dx, double dz)
        {
            if (VoxelGrid == null || curK < 0) return false;
            if (!WorldToColumn(fromX, fromZ, out int curX, out int curZ)) return false;
            if (!WorldToColumn(fromX + dx, fromZ + dz, out int targetX, out int targetZ)) return true;
            if (targetX == curX && targetZ == curZ) return false;

            int targetIdx = GridIndex(targetX, targetZ);
            if (VoxelGrid.voxelCounts[targetIdx] == 0) return true;
            int resolvedK = ResolveTargetLayer(curX, curZ, curK, targetX, targetZ);
            if (resolvedK < 0) return true;

            VoxelData curVoxel = VoxelGrid.voxels[VoxelGrid.startIndices[GridIndex(curX, curZ)] + curK];
            VoxelData targetVoxel = VoxelGrid.voxels[VoxelGrid.startIndices[targetIdx] + resolvedK];
            return curVoxel.maxY - targetVoxel.maxY > MaxStep;
        }

        public bool ValidateGroundMove(double fromX, double fromZ, int curK, double dx, double dz,
            double maxDelta, out int newK, out double newY)
        {
            newK = curK;
            newY = VoxelGrid != null ? VoxelGrid.originOffset.y : 0;
            if (Math.Sqrt(dx * dx + dz * dz) > maxDelta) return false;
            if (VoxelGrid == null)
            {
                newY = 0;
                return true;
            }

            double dist = Math.Sqrt(dx * dx + dz * dz);
            int steps = (int)Math.Ceiling(dist / (VoxelGrid.voxelSize.x * 0.5));
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
            newY = SurfaceHeight(px, pz, k);
            return true;
        }

        private bool ValidateMove(double fromX, double fromZ, int curK, double dx, double dz, out int resolvedK)
        {
            resolvedK = curK;
            if (!WorldToColumn(fromX, fromZ, out int curX, out int curZ)) return false;
            if (!WorldToColumn(fromX + dx, fromZ + dz, out int targetX, out int targetZ)) return false;
            if (Math.Abs(targetX - curX) > 1 || Math.Abs(targetZ - curZ) > 1) return false;

            int targetIdx = GridIndex(targetX, targetZ);
            int targetCount = VoxelGrid.voxelCounts[targetIdx];
            if (targetCount == 0) return false;

            int curIdx = GridIndex(curX, curZ);
            int curCount = VoxelGrid.voxelCounts[curIdx];
            if (curK < 0 || curK >= curCount) return false;
            VoxelData curVoxel = VoxelGrid.voxels[VoxelGrid.startIndices[curIdx] + curK];

            int resolved = curK;
            if (targetX != curX || targetZ != curZ)
            {
                resolved = ResolveTargetLayer(curX, curZ, curK, targetX, targetZ);
                if (resolved < 0 || resolved >= targetCount) return false;
            }

            VoxelData targetVoxel = VoxelGrid.voxels[VoxelGrid.startIndices[targetIdx] + resolved];
            double heightDiff = targetVoxel.maxY - curVoxel.maxY;
            if (heightDiff > MaxStep) return false;
            resolvedK = resolved;
            return true;
        }

        public bool CeilingHit(double x, double z, double feetY, double headTopY, out double ceilingY)
        {
            ceilingY = 0;
            if (VoxelGrid == null || !WorldToColumn(x, z, out int cx, out int cz)) return false;
            int idx = GridIndex(cx, cz);
            int count = VoxelGrid.voxelCounts[idx];
            if (count == 0) return false;

            int start = VoxelGrid.startIndices[idx];
            double originY = VoxelGrid.originOffset.y;
            double ceiling = double.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                VoxelData v = VoxelGrid.voxels[start + i];
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
