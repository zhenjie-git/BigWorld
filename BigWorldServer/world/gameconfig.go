package main

import (
	"encoding/json"
	"fmt"
	"os"

	pb "bigworld/common/pb"
)

// playerConfigJSON mirrors the fields of GameConfig/Player/player_config.json
// that the world simulation needs. The nine scalar values it reads are authored
// in GameConfig/Player/player_config.xlsx; the rest of the JSON is Unity-owned.
// Only the fields required for derivation are declared; unknown extra fields in
// the shared file are ignored.
type playerConfigJSON struct {
	Grounded struct {
		BaseSpeed float64 `json:"base_speed"`
		Sprint    struct {
			SpeedModifier   float64 `json:"speed_modifier"`
			SprintToRunTime float64 `json:"sprint_to_run_time"`
		} `json:"sprint"`
		Roll struct {
			SpeedModifier float64 `json:"speed_modifier"`
		} `json:"roll"`
	} `json:"grounded"`
	Airborne struct {
		Fall struct {
			FallSpeedLimit float64 `json:"fall_speed_limit"`
			Gravity        float64 `json:"gravity"`
		} `json:"fall"`
	} `json:"airborne"`
	Collider struct {
		Height  float64 `json:"height"`
		CenterY float64 `json:"center_y"`
	} `json:"collider"`
	VoxelMaxStepHeight float64 `json:"voxel_max_step_height"`
}

// gameConfig holds the movement parameters the world sim uses, derived from the
// shared GameConfig/Player/player_config.json (derivation table in README).
type gameConfig struct {
	maxStep           float64
	sprintSpeedMps    float64
	sprintToRunTime   float64
	rollSpeedMps      float64
	fallGravityMps2   float64
	fallSpeedLimitMps float64
	playerHeight      float64
	playerCenterY     float64
}

// LoadPlayerConfig reads GameConfig/Player/player_config.json and derives the
// movement parameters the world process simulates with.
func LoadPlayerConfig(path string) (*gameConfig, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var pc playerConfigJSON
	if err := json.Unmarshal(data, &pc); err != nil {
		return nil, err
	}
	base := pc.Grounded.BaseSpeed
	return &gameConfig{
		maxStep:           pc.VoxelMaxStepHeight,
		sprintSpeedMps:    base * pc.Grounded.Sprint.SpeedModifier,
		sprintToRunTime:   pc.Grounded.Sprint.SprintToRunTime,
		rollSpeedMps:      base * pc.Grounded.Roll.SpeedModifier,
		fallGravityMps2:   pc.Airborne.Fall.Gravity,
		fallSpeedLimitMps: pc.Airborne.Fall.FallSpeedLimit,
		playerHeight:      pc.Collider.Height,
		playerCenterY:     pc.Collider.CenterY,
	}, nil
}

// StateNameToMoveState maps the shared state names to server MoveState.
// The single source of truth is
// GameConfig/StateTransitionTable/state_transition_table.xlsx
// (validated by the Unity exporter against MoveTransitionTable.StateMappings).
// Keep this switch in sync with the client mapping in MoveStateMachine.cs.
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

type stateTransitionTableJSON struct {
	Entries []struct {
		Source         string   `json:"source"`
		AllowedTargets []string `json:"allowed_targets"`
	} `json:"entries"`
}

// LoadStateTransitionTable reads GameConfig/StateTransitionTable/state_transition_table.json
// (the file generated from the xlsx next to it) and builds the
// CanTransition lookup. Unknown state names are a hard error so a drifted
// client/server mapping can never be silently ignored.
func LoadStateTransitionTable(path string) (map[pb.MoveState]map[pb.MoveState]bool, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var t stateTransitionTableJSON
	if err := json.Unmarshal(data, &t); err != nil {
		return nil, err
	}
	table := make(map[pb.MoveState]map[pb.MoveState]bool)
	for _, e := range t.Entries {
		from, ok := StateNameToMoveState(e.Source)
		if !ok {
			return nil, fmt.Errorf("state transition table: unknown source state %q", e.Source)
		}
		targets := make(map[pb.MoveState]bool)
		for _, name := range e.AllowedTargets {
			to, ok := StateNameToMoveState(name)
			if !ok {
				return nil, fmt.Errorf("state transition table: unknown target state %q for source %q", name, e.Source)
			}
			targets[to] = true
		}
		table[from] = targets
	}
	if len(table) == 0 {
		return nil, fmt.Errorf("state transition table has no usable entries")
	}
	return table, nil
}
