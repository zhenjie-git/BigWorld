using BigWorldClient.Network.Protocol;

namespace BigWorldClient
{

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
