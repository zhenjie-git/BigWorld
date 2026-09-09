package main

import pb "bigworld/common/pb"

func StateNameToMoveState(name string) (pb.MoveState, bool) {
	switch name {
	case "Idling":
		return pb.MoveState_MOVE_IDLE, true
	case "Walking":
		return pb.MoveState_MOVE_WALK, true
	case "Running":
		return pb.MoveState_MOVE_RUN, true
	case "Sprinting":
		return pb.MoveState_MOVE_SPRINT, true
	case "LightStopping":
		return pb.MoveState_MOVE_STOP_LIGHT, true
	case "MediumStopping":
		return pb.MoveState_MOVE_STOP_MED, true
	case "HardStopping":
		return pb.MoveState_MOVE_STOP_HARD, true
	case "LightLanding":
		return pb.MoveState_MOVE_LAND_LIGHT, true
	case "Rolling":
		return pb.MoveState_MOVE_ROLL, true
	case "Dashing":
		return pb.MoveState_MOVE_DASH, true
	case "JumpUp":
		return pb.MoveState_MOVE_JUMP_UP, true
	case "Falling":
		return pb.MoveState_MOVE_FALL, true
	case "JumpDown":
		return pb.MoveState_MOVE_JUMP_DOWN, true
	}
	return 0, false
}
