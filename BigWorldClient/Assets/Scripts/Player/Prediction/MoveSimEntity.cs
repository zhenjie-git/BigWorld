using BigWorldClient.Network.Protocol;

namespace BigWorldClient
{
    /// <summary>
    /// 可快照的移动模拟实体。字段与服务器 PlayerEntity 一一对应；
    /// 一个快照即可完全恢复客户端预测状态，也是回滚重放的载体。
    /// </summary>
    public struct MoveSimEntity
    {
        public double X, Y, Z;
        public int VoxelK;
        public bool Airborne;
        public MoveState State;
        public double MoveDirX, MoveDirZ;
        public double CurveNorm;
        public long StateStartMs;
        public double FallVelY;
    }
}
