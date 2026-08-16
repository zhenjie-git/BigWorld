package main

import pb "bigworld/common/pb"

type playerEntity struct {
	PlayerId       uint64
	Account        string
	X, Z, Y        float64
	VoxelK         int
	Airborne       bool
	State          pb.MoveState
	Scene          *scene
	LastMoveTimeMs int64

	// Server-authoritative move simulation state (see world/MoveState.go).
	MoveDirX, MoveDirZ float64 // current world-space horizontal direction
	CurveNorm          float64 // current displacement-curve progress [0,1)
	StateStartMs       int64   // state start time, on the fixed tick grid
	FallVelY           float64 // fallState downward velocity

	// Fixed-tick timeline and rollback support (see world/Rollback.go).
	SimTick   int64            // last fully simulated tick (snapshot tick)
	Snapshots []entitySnapshot // per-tick snapshots, newest last
	Inputs    []moveInput      // recent accepted player inputs, sorted by (Tick, Seq)
	InputSeq  uint64           // monotonic input sequence for stable ordering
}
