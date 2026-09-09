using BigWorldClient.Network;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient
{

    public static class MovementSim
    {
        private const int MoveApplied = 0;
        private const int MoveBlocked = 1;
        private const int MoveLedged = 2;

        public static long TickToMs(long tick) => tick * ServerClock.TickMs;

        public static void SetMoveState(ref MoveSimEntity e, MoveState s, long nowMs)
        {
            e.State = s;
            e.StateStartMs = nowMs;
            e.CurveNorm = 0;
            if (s == MoveState.MoveFall)
                e.FallVelY = 0;
        }

        public static void SetMoveDir(ref MoveSimEntity e, double x, double z)
        {
            double l = System.Math.Sqrt(x * x + z * z);
            if (l < 1e-4) return;
            e.MoveDirX = (double)(float)(x / l);
            e.MoveDirZ = (double)(float)(z / l);
        }

        public static void WorldDelta(in MoveSimEntity e, double localX, double localZ, out double wx, out double wz)
        {
            wx = e.MoveDirX * localZ + e.MoveDirZ * localX;
            wz = e.MoveDirZ * localZ - e.MoveDirX * localX;
        }

        public static double AllowedDelta(long dtMs)
        {
            double d = SimContext.MaxMoveDelta * (double)dtMs / 20.0;
            return d < SimContext.MaxMoveDelta ? SimContext.MaxMoveDelta : d;
        }

        public static MoveTransitionRequest CyclicUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs, MoveState state)
        {
            if (!ctx.Displacement.TryGetState(state, out SimMoveState entry) || entry.DurationSeconds <= 0)
                return MoveTransitionRequest.None;

            double elapsed = (double)(nowMs - e.StateStartMs) / 1000.0;
            if (elapsed < 0) elapsed = 0;
            double norm = elapsed / entry.DurationSeconds % 1.0;
            double a = e.CurveNorm;
            double b = norm;
            if (b < a) b += 1;

            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            double dx = curveX?.AxisDelta(a, b) ?? 0;
            double dz = curveZ?.AxisDelta(a, b) ?? 0;
            e.CurveNorm = norm;
            if (dx == 0 && dz == 0) return MoveTransitionRequest.None;

            WorldDelta(in e, dx, dz, out double wx, out double wz);
            int res = ApplyGroundedDelta(ref e, ctx, wx, wz, AllowedDelta(dtMs));
            return res == MoveLedged
                ? new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.Ledged)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest ConstSpeedUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs, double speed)
        {
            if (speed <= 0) return MoveTransitionRequest.None;
            double dist = speed * (double)dtMs / 1000.0;
            int res = ApplyGroundedDelta(ref e, ctx, e.MoveDirX * dist, e.MoveDirZ * dist, AllowedDelta(dtMs));
            return res == MoveLedged
                ? new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.Ledged)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest SprintUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            double elapsed = (double)(nowMs - e.StateStartMs) / 1000.0;
            if (elapsed < 0) elapsed = 0;
            if (ctx.SprintToRunTime > 0 && elapsed >= ctx.SprintToRunTime)
                return new MoveTransitionRequest(MoveState.MoveRun, MoveTransitionReason.SprintTimeout);

            if (!ctx.Displacement.TryGetState(MoveState.MoveSprint, out SimMoveState entry) || entry.DurationSeconds <= 0)
                return MoveTransitionRequest.None;

            double norm = elapsed / entry.DurationSeconds % 1.0;
            double a = e.CurveNorm;
            double b = norm;
            if (b < a) b += 1;

            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            double dx = curveX?.AxisDelta(a, b) ?? 0;
            double dz = curveZ?.AxisDelta(a, b) ?? 0;
            e.CurveNorm = norm;
            if (dx == 0 && dz == 0) return MoveTransitionRequest.None;

            WorldDelta(in e, dx, dz, out double wx, out double wz);
            int res = ApplyGroundedDelta(ref e, ctx, wx, wz, AllowedDelta(dtMs));
            return res == MoveLedged
                ? new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.Ledged)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest RollUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            if (!ctx.Displacement.TryGetState(MoveState.MoveRoll, out SimMoveState entry) || entry.DurationSeconds <= 0)
                return new MoveTransitionRequest(MoveState.MoveIdle, MoveTransitionReason.RollCompleted);

            double norm = OneShotNorm(e, entry.DurationSeconds, nowMs);
            double a = e.CurveNorm;
            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            double dx = curveX?.AxisDelta(a, norm) ?? 0;
            double dz = curveZ?.AxisDelta(a, norm) ?? 0;
            e.CurveNorm = norm;
            if (dx != 0 || dz != 0)
            {
                WorldDelta(in e, dx, dz, out double wx, out double wz);
                int res = ApplyGroundedDelta(ref e, ctx, wx, wz, AllowedDelta(dtMs));
                if (res == MoveLedged)
                    return new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.Ledged);
            }

            return norm >= 1
                ? new MoveTransitionRequest(MoveState.MoveIdle, MoveTransitionReason.RollCompleted)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest DashUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            if (!ctx.Displacement.TryGetState(MoveState.MoveDash, out SimMoveState entry) || entry.DurationSeconds <= 0)
                return new MoveTransitionRequest(MoveState.MoveIdle, MoveTransitionReason.DashCompleted);

            double norm = OneShotNorm(e, entry.DurationSeconds, nowMs);
            double a = e.CurveNorm;
            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            double dx = curveX?.AxisDelta(a, norm) ?? 0;
            double dz = curveZ?.AxisDelta(a, norm) ?? 0;
            e.CurveNorm = norm;
            if (dx != 0 || dz != 0)
            {
                WorldDelta(in e, dx, dz, out double wx, out double wz);
                int res = ApplyGroundedDelta(ref e, ctx, wx, wz, AllowedDelta(dtMs));
                if (res == MoveLedged)
                    return new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.Ledged);
            }

            return norm >= 1
                ? new MoveTransitionRequest(MoveState.MoveIdle, MoveTransitionReason.DashCompleted)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest StopUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            if (!ctx.Displacement.TryGetState(e.State, out SimMoveState entry) || entry.Curves.Count == 0)
                return MoveTransitionRequest.None;

            double norm = OneShotNorm(e, entry.DurationSeconds, nowMs);
            double a = e.CurveNorm;
            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            double dx = curveX?.AxisDelta(a, norm) ?? 0;
            double dz = curveZ?.AxisDelta(a, norm) ?? 0;
            e.CurveNorm = norm;
            if (dx != 0 || dz != 0)
            {
                WorldDelta(in e, dx, dz, out double wx, out double wz);
                int res = ApplyGroundedDelta(ref e, ctx, wx, wz, AllowedDelta(dtMs));
                if (res == MoveLedged)
                    return new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.Ledged);
            }

            return norm >= 1
                ? new MoveTransitionRequest(MoveState.MoveIdle,
                    e.State == MoveState.MoveStopHard ? MoveTransitionReason.HardStopCompleted : MoveTransitionReason.StateCompleted)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest JumpUpUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            if (!ctx.Displacement.TryGetState(MoveState.MoveJumpUp, out SimMoveState entry) || entry.DurationSeconds <= 0)
            {
                return new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.StateCompleted);
            }

            double norm = OneShotNorm(e, entry.DurationSeconds, nowMs);
            double a = e.CurveNorm;
            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            SimCurve curveY = GetCurve(entry, "y");
            double dx = curveX?.AxisDelta(a, norm) ?? 0;
            double dz = curveZ?.AxisDelta(a, norm) ?? 0;
            double dy = (curveY?.Eval(norm) ?? 0) - (curveY?.Eval(a) ?? 0);
            e.CurveNorm = norm;

            if (dx != 0 || dz != 0)
            {
                WorldDelta(in e, dx, dz, out double wx, out double wz);
                ApplyJumpHorizontal(ref e, ctx, wx, wz, AllowedDelta(dtMs));
            }

            double newY = e.Y + dy;
            if (ctx.VoxelGrid != null)
            {
                double half = ctx.PlayerHeight / 2;
                double headTop = newY + ctx.PlayerCenterY + half;
                double feet = newY + ctx.PlayerCenterY - half;
                if (ctx.CeilingHit(e.X, e.Z, feet, headTop, out double ceilingY))
                {
                    e.Y = ceilingY - ctx.PlayerCenterY - half;
                    e.Airborne = true;
                    return new MoveTransitionRequest(MoveState.MoveJumpDown, MoveTransitionReason.StateCompleted);
                }
            }

            e.Y = newY;
            e.Airborne = true;
            return norm >= 1
                ? new MoveTransitionRequest(MoveState.MoveJumpDown, MoveTransitionReason.StateCompleted)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest JumpDownUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            if (!ctx.Displacement.TryGetState(MoveState.MoveJumpDown, out SimMoveState entry) || entry.DurationSeconds <= 0)
            {
                return new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.StateCompleted);
            }

            double norm = OneShotNorm(e, entry.DurationSeconds, nowMs);
            double a = e.CurveNorm;
            if (norm < a)
                return new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.StateCompleted);

            SimCurve curveX = GetCurve(entry, "x");
            SimCurve curveZ = GetCurve(entry, "z");
            SimCurve curveY = GetCurve(entry, "y");
            double dx = curveX?.AxisDelta(a, norm) ?? 0;
            double dz = curveZ?.AxisDelta(a, norm) ?? 0;
            double dy = (curveY?.Eval(norm) ?? 0) - (curveY?.Eval(a) ?? 0);
            if (dy > 0) dy = 0;
            e.CurveNorm = norm;

            if (dx != 0 || dz != 0)
            {
                WorldDelta(in e, dx, dz, out double wx, out double wz);
                ApplyJumpHorizontal(ref e, ctx, wx, wz, AllowedDelta(dtMs));
            }

            double newY = e.Y + dy;
            if (TryLandOnVoxel(ref e, ctx, newY))
            {
                return new MoveTransitionRequest(MoveState.MoveIdle, MoveTransitionReason.AirborneLanded);
            }

            e.Y = newY;
            e.Airborne = true;
            return norm >= 1
                ? new MoveTransitionRequest(MoveState.MoveFall, MoveTransitionReason.StateCompleted)
                : MoveTransitionRequest.None;
        }

        public static MoveTransitionRequest FallUpdate(ref MoveSimEntity e, SimContext ctx, long dtMs, long nowMs)
        {
            double dt = (double)dtMs / 1000.0;
            if (dt > 0.05) dt = 0.05;

            e.FallVelY -= ctx.FallGravityMps2 * dt;
            if (e.FallVelY < -ctx.FallSpeedLimitMps)
                e.FallVelY = -ctx.FallSpeedLimitMps;

            double newY = e.Y + e.FallVelY * dt;
            if (TryLandOnVoxel(ref e, ctx, newY))
                return new MoveTransitionRequest(MoveState.MoveIdle, MoveTransitionReason.AirborneLanded);

            e.Y = newY;
            e.Airborne = true;
            return MoveTransitionRequest.None;
        }

        private static double OneShotNorm(in MoveSimEntity e, double duration, long nowMs)
        {
            if (duration <= 0) return 1;
            double elapsed = (double)(nowMs - e.StateStartMs) / 1000.0;
            double norm = elapsed / duration;
            if (norm < 0) return 0;
            if (norm > 1) return 1;
            return norm;
        }

        private static int ApplyGroundedDelta(ref MoveSimEntity e, SimContext ctx, double dx, double dz, double maxDelta)
        {
            if (ctx.VoxelGrid == null)
            {
                e.X += dx;
                e.Z += dz;
                return MoveApplied;
            }

            int k = e.VoxelK;
            if (e.Airborne || k < 0)
                k = ctx.ResolveLayerNearY(e.X, e.Z, e.Y);
            if (k < 0)
            {
                e.X += dx;
                e.Z += dz;
                return MoveApplied;
            }

            if (ctx.GroundDropAt(e.X, e.Z, k, dx, dz))
            {
                e.X += dx;
                e.Z += dz;
                return MoveLedged;
            }

            if (!ctx.ValidateGroundMove(e.X, e.Z, k, dx, dz, maxDelta, out int newK, out double newY) || newK < 0)
                return MoveBlocked;

            e.X += dx;
            e.Z += dz;
            e.Y = newY;
            e.VoxelK = newK;
            e.Airborne = false;
            return MoveApplied;
        }

        private static void ApplyJumpHorizontal(ref MoveSimEntity e, SimContext ctx, double dx, double dz, double maxDelta)
        {
            if (ctx.VoxelGrid == null)
            {
                e.X += dx;
                e.Z += dz;
                return;
            }

            int k = e.VoxelK;
            if (e.Airborne || k < 0)
                k = ctx.ResolveLayerNearY(e.X, e.Z, e.Y);
            if (k < 0)
            {
                e.X += dx;
                e.Z += dz;
                return;
            }

            if (ctx.ValidateGroundMove(e.X, e.Z, k, dx, dz, maxDelta, out _, out _))
            {
                e.X += dx;
                e.Z += dz;
            }
        }

        private static bool TryLandOnVoxel(ref MoveSimEntity e, SimContext ctx, double newY)
        {
            if (ctx.VoxelGrid == null) return false;
            int k = ctx.ResolveLayerNearY(e.X, e.Z, newY);
            double top = ctx.SurfaceHeight(e.X, e.Z, k);
            if (k >= 0 && newY <= top)
            {
                e.Y = top;
                e.Airborne = false;
                e.VoxelK = k;
                return true;
            }
            return false;
        }

        private static SimCurve GetCurve(SimMoveState entry, string axis)
            => entry.TryGetCurve(axis, out SimCurve curve) ? curve : null;
    }
}
