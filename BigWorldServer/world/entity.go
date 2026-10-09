package main

import (
	"time"

	"bigworld/common"
	pb "bigworld/common/pb"
)

type playerEntity struct {
	PlayerID         uint64
	Account          string
	GatewayID        string
	SessionID        string
	GatewayConn      *common.ConnWrapper
	X, Z, Y          float64
	VoxelK           int
	Airborne         bool
	State            pb.MoveState
	Scene            *scene
	LastMoveTimeMs   int64
	Phase            entityPhase
	PhaseUntilMs     int64
	PhaseStartTick   int64
	TransferID       string
	TransferCooldown time.Time

	MoveDirX, MoveDirZ float64
	CurveNorm          float64
	StateStartMs       int64
	FallVelY           float64

	SimTick   int64
	Snapshots []entitySnapshot
	Inputs    []moveInput
	InputSeq  uint64

	KnownEntities map[uint64]bool
}
