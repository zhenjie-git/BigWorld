package main

import pb "bigworld/common/pb"

type playerEntity struct {
	PlayerId       uint64
	Account        string
	GatewayId      string
	SessionId      string
	X, Z, Y        float64
	VoxelK         int
	Airborne       bool
	State          pb.MoveState
	Scene          *scene
	LastMoveTimeMs int64

	MoveDirX, MoveDirZ float64
	CurveNorm          float64
	StateStartMs       int64
	FallVelY           float64

	SimTick   int64
	Snapshots []entitySnapshot
	Inputs    []moveInput
	InputSeq  uint64
}
