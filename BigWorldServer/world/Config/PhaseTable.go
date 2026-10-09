package config

import (
	"fmt"
	"os"

	pb "bigworld/common/pb"
)

type PhaseDef struct {
	Phase            string
	AcceptsMoveInput bool
	Simulates        bool
	JoinsAoi         bool
	SendsMoveRsp     bool
	CanStartTravel   bool
	SavesToDb        bool
	ForcesMoveIdle   bool
}

type PhaseTable struct {
	defs        map[string]PhaseDef
	transitions map[string]map[string]bool
}

var phaseTableCfg = &PhaseTable{}

func Phases() *PhaseTable { return phaseTableCfg }

func (t *PhaseTable) Load(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsPhaseTableMsg(data, 0)
	t.defs = make(map[string]PhaseDef)
	t.transitions = make(map[string]map[string]bool)
	e := &pb.PhaseEntry{}
	for i := 0; i < msg.EntriesLength(); i++ {
		if !msg.Entries(e, i) {
			continue
		}
		def := PhaseDef{
			Phase:            string(e.Phase()),
			AcceptsMoveInput: e.AcceptsMoveInput(),
			Simulates:        e.Simulates(),
			JoinsAoi:         e.JoinsAoi(),
			SendsMoveRsp:     e.SendsMoveRsp(),
			CanStartTravel:   e.CanStartTravel(),
			SavesToDb:        e.SavesToDb(),
			ForcesMoveIdle:   e.ForcesMoveIdle(),
		}
		if def.Phase == "" {
			return fmt.Errorf("phase table: empty phase name at entry %d", i)
		}
		t.defs[def.Phase] = def
		t.transitions[def.Phase] = map[string]bool{
			"Active":         e.Active(),
			"TeleportFrozen": e.TeleportFrozen(),
			"ZoneHandoff":    e.ZoneHandoff(),
			"WorldHandoff":   e.WorldHandoff(),
			"Arriving":       e.Arriving(),
			"ArrivingFrozen": e.ArrivingFrozen(),
		}
	}
	if len(t.defs) == 0 {
		return fmt.Errorf("phase table has no usable entries")
	}
	return nil
}

func (t *PhaseTable) Loaded() bool {
	return len(t.defs) > 0
}

func (t *PhaseTable) Def(name string) (PhaseDef, bool) {
	def, ok := t.defs[name]
	return def, ok
}

func (t *PhaseTable) Names() []string {
	names := make([]string, 0, len(t.defs))
	for name := range t.defs {
		names = append(names, name)
	}
	return names
}

func (t *PhaseTable) Allows(from, to string) bool {
	row, ok := t.transitions[from]
	if !ok {
		return false
	}
	return row[to]
}
