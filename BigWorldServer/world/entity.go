package main

import pb "bigworld/common/pb"

type playerEntity struct {
	PlayerId uint64
	Account  string
	X, Z, Y  float64
	VoxelK   int
	Airborne bool
	State    pb.MoveState
	Scene    *scene
	LastMoveTimeMs int64

	// Server-authoritative move simulation state (see world/move_state.go).
	MoveDirX, MoveDirZ float64 // current world-space horizontal direction
	CurveNorm          float64 // current displacement-curve progress [0,1)
	StateStartMs       int64   // state start time, from the client's server_time_ms
	FallVelY           float64 // fallState downward velocity
}
