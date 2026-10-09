package config

import (
	"fmt"
	"os"

	pb "bigworld/common/pb"
)

var moveEventFields = []string{
	"Walk", "Run", "Sprint", "Jump", "Dash", "Roll",
	"StopLight", "StopMed", "StopHard", "Halt", "DirChange",
}

type MoveTransitionTable struct {
	policies     map[pb.MoveState]map[string]pb.EventPolicy
	blocksTravel map[pb.MoveState]bool
}

var moveTransitionTable = &MoveTransitionTable{}

func MoveTransitions() *MoveTransitionTable { return moveTransitionTable }

func (t *MoveTransitionTable) Load(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsTransitionTableMsg(data, 0)
	t.policies = make(map[pb.MoveState]map[string]pb.EventPolicy)
	t.blocksTravel = make(map[pb.MoveState]bool)
	e := &pb.TransitionEntry{}
	for i := 0; i < msg.EntriesLength(); i++ {
		if !msg.Entries(e, i) {
			continue
		}
		from, ok := StateNameToMoveState(string(e.Source()))
		if !ok {
			return fmt.Errorf("state transition table: unknown source state %q", e.Source())
		}
		values := []pb.EventPolicy{
			e.Walk(), e.Run(), e.Sprint(), e.Jump(), e.Dash(), e.Roll(),
			e.StopLight(), e.StopMed(), e.StopHard(), e.Halt(), e.DirChange(),
		}
		row := make(map[string]pb.EventPolicy, len(moveEventFields))
		for j, name := range moveEventFields {
			row[name] = values[j]
		}
		t.policies[from] = row
		t.blocksTravel[from] = e.BlocksTravel()
	}
	if len(t.policies) == 0 {
		return fmt.Errorf("state transition table has no usable entries")
	}
	return nil
}

func (t *MoveTransitionTable) Loaded() bool {
	return len(t.policies) > 0
}

func (t *MoveTransitionTable) PolicyOf(from pb.MoveState, event string) (pb.EventPolicy, bool) {
	row, ok := t.policies[from]
	if !ok {
		return 0, false
	}
	p, ok := row[event]
	return p, ok
}

func (t *MoveTransitionTable) BlocksTravel(from pb.MoveState) bool {
	return t.blocksTravel[from]
}
