package main

import (
	"fmt"
	"os"

	pb "bigworld/common/pb"
)

type MoveTransitionTable struct {
	allowed map[pb.MoveState]map[pb.MoveState]bool
}

var moveTransitionTable = &MoveTransitionTable{}

func MoveTransitions() *MoveTransitionTable { return moveTransitionTable }

func (t *MoveTransitionTable) Load(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsTransitionTableMsg(data, 0)
	t.allowed = make(map[pb.MoveState]map[pb.MoveState]bool)
	e := &pb.TransitionEntry{}
	for i := 0; i < msg.EntriesLength(); i++ {
		if !msg.Entries(e, i) {
			continue
		}
		from, ok := StateNameToMoveState(string(e.Source()))
		if !ok {
			return fmt.Errorf("state transition table: unknown source state %q", e.Source())
		}
		targets := make(map[pb.MoveState]bool)
		for j := 0; j < e.AllowedTargetsLength(); j++ {
			to, ok := StateNameToMoveState(string(e.AllowedTargets(j)))
			if !ok {
				return fmt.Errorf("state transition table: unknown target state %q for source %q", e.AllowedTargets(j), e.Source())
			}
			targets[to] = true
		}
		t.allowed[from] = targets
	}
	if len(t.allowed) == 0 {
		return fmt.Errorf("state transition table has no usable entries")
	}
	return nil
}

func (t *MoveTransitionTable) CanTransition(from, to pb.MoveState) bool {
	if from == to {
		return true
	}
	if len(t.allowed) == 0 {
		return true
	}
	targets, ok := t.allowed[from]
	if !ok {
		return false
	}
	return targets[to]
}
