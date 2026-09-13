package main

import (
	"bigworld/common"
	pb "bigworld/common/pb"
)

type playerEntity struct {
	PlayerId        uint64
	Account         string
	GatewayId       string
	SessionId       string
	GatewayConn     *common.ConnWrapper
	X, Z, Y         float64
	VoxelK          int
	Airborne        bool
	State           pb.MoveState
	Scene           *scene
	LastMoveTimeMs  int64
	Transferring    bool
	TransferUntilMs int64

	MoveDirX, MoveDirZ float64
	CurveNorm          float64
	StateStartMs       int64
	FallVelY           float64

	SimTick   int64
	Snapshots []entitySnapshot
	Inputs    []moveInput
	InputSeq  uint64
}
