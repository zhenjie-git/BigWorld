package main

import pb "bigworld/common/pb"

func IsAirborneState(s pb.MoveState) bool {
	return s == pb.MoveState_MOVE_JUMP_UP ||
		s == pb.MoveState_MOVE_JUMP_DOWN ||
		s == pb.MoveState_MOVE_FALL
}

// moveTransitions is the state transition table, loaded at startup from the
// shared GameConfig/StateTransitionTable/state_transition_table.json generated
// from the xlsx in the same folder (see gameconfig.go). It is the
// single source of truth shared with the client's MoveTransitionTable.
// A nil table (config missing/unparseable) falls back to permissive: every
// transition is allowed, mirroring how a missing voxel/displacement file
// degrades to "validation disabled".
var moveTransitions map[pb.MoveState]map[pb.MoveState]bool

func CanTransition(from, to pb.MoveState) bool {
	if from == to {
		return true
	}
	if moveTransitions == nil {
		return true // permissive fallback when the shared table failed to load
	}
	targets, ok := moveTransitions[from]
	if !ok {
		return false
	}
	return targets[to]
}
