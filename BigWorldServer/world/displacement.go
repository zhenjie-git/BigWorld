package main

import (
	"encoding/json"
	"log"
	"math"
	"os"

	pb "bigworld/common/pb"
)

type displacementState struct {
	Duration    float64                 `json:"duration,omitempty"`
	Curves      map[string][][2]float64 `json:"curves,omitempty"`
	MaxPerFrame float64                 `json:"max_per_frame,omitempty"`
}

type displacementTable struct {
	FixedDelta float64                      `json:"fixed_delta"`
	Margin     float64                      `json:"margin"`
	States     map[string]displacementState `json:"states"`
	caps       map[pb.MoveState]float64
}

func LoadDisplacementTable(path string) (*displacementTable, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var t displacementTable
	if err := json.Unmarshal(data, &t); err != nil {
		return nil, err
	}
	if t.FixedDelta <= 0 {
		t.FixedDelta = 0.02
	}
	if t.Margin <= 0 {
		t.Margin = 1
	}
	t.caps = make(map[pb.MoveState]float64)
	for name, st := range t.States {
		id, ok := pb.MoveState_value[name]
		if !ok {
			continue
		}
		state := pb.MoveState(id)
		if len(st.Curves) > 0 && st.Duration > 0 {
			t.caps[state] = ComputeStateCap(st, t.FixedDelta) * t.Margin
		} else {
			t.caps[state] = st.MaxPerFrame
		}
	}
	return &t, nil
}

// stateEntry returns the displacement state data for a MoveState, used by the
// per-state sim classes to evaluate the exported curves.
func (t *displacementTable) StateEntry(s pb.MoveState) (displacementState, bool) {
	name, ok := pb.MoveState_name[int32(s)]
	if !ok {
		return displacementState{}, false
	}
	st, ok := t.States[name]
	return st, ok
}

func (t *displacementTable) MaxPerFrame(state pb.MoveState) float64 {
	if v, ok := t.caps[state]; ok {
		return v
	}
	return maxMoveDelta
}

func ComputeStateCap(st displacementState, fixedDelta float64) float64 {
	sub := 16
	dt := fixedDelta / st.Duration
	step := dt / float64(sub)
	best := 0.0
	for a := 0.0; a < 1.0; a += step {
		dx := AxisDelta(st.Curves["x"], a, a+dt)
		dz := AxisDelta(st.Curves["z"], a, a+dt)
		if m := math.Hypot(dx, dz); m > best {
			best = m
		}
	}
	return best
}

func AxisDelta(keys [][2]float64, a, b float64) float64 {
	if len(keys) == 0 {
		return 0
	}
	ca := EvalCurve(keys, a)
	if b <= 1 {
		return EvalCurve(keys, b) - ca
	}
	return EvalCurve(keys, 1) - ca + EvalCurve(keys, b-1) - EvalCurve(keys, 0)
}

func EvalCurve(keys [][2]float64, t float64) float64 {
	if t <= keys[0][0] {
		return keys[0][1]
	}
	last := keys[len(keys)-1]
	if t >= last[0] {
		return last[1]
	}
	for i := 1; i < len(keys); i++ {
		if t <= keys[i][0] {
			k0, k1 := keys[i-1], keys[i]
			frac := (t - k0[0]) / (k1[0] - k0[0])
			return k0[1] + frac*(k1[1]-k0[1])
		}
	}
	return last[1]
}

func (t *displacementTable) DebugDump() {
	for name, st := range t.States {
		if len(st.Curves) > 0 {
			log.Printf("[displacement] %s dur=%.4f cap=%.4f", name, st.Duration, t.MaxPerFrame(pb.MoveState(pb.MoveState_value[name])))
		} else {
			log.Printf("[displacement] %s cap=%.4f", name, st.MaxPerFrame)
		}
	}
}
