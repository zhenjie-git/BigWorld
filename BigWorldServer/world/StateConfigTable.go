package main

import (
	"fmt"
	"log"
	"math"
	"os"
	"path/filepath"

	pb "bigworld/common/pb"
)

type StateConfigState struct {
	Duration    float64
	Curves      map[string][][2]float64
	MaxPerFrame float64
}

type StateConfigTable struct {
	FixedDelta float64
	Margin     float64
	States     map[string]StateConfigState
	caps       map[pb.MoveState]float64
}

var stateConfigTable = &StateConfigTable{
	FixedDelta: 0.02,
	Margin:     2.0,
	States:     map[string]StateConfigState{},
	caps:       map[pb.MoveState]float64{},
}

func StateConfig() *StateConfigTable { return stateConfigTable }

func (t *StateConfigTable) LoadCurveKeys(root, relativePath string) ([][2]float64, error) {
	data, err := os.ReadFile(filepath.Join(root, relativePath+".bytes"))
	if err != nil {
		return nil, err
	}
	msg := pb.GetRootAsCurveMsg(data, 0)
	n := msg.TLength()
	if n == 0 || msg.VLength() != n {
		return nil, fmt.Errorf("curve file %s has no usable keys", relativePath)
	}
	keys := make([][2]float64, n)
	for i := 0; i < n; i++ {
		keys[i] = [2]float64{msg.T(i), msg.V(i)}
	}
	return keys, nil
}

func (t *StateConfigTable) Load(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsStateConfigMsg(data, 0)

	root := filepath.Dir(filepath.Dir(path))
	t.FixedDelta = 0.02
	t.Margin = 2.0
	t.States = make(map[string]StateConfigState)
	t.caps = make(map[pb.MoveState]float64)

	entry := &pb.StateConfigEntry{}
	for i := 0; i < msg.EntriesLength(); i++ {
		if !msg.Entries(entry, i) {
			continue
		}
		stateName := string(entry.State())
		state, ok := StateNameToMoveState(stateName)
		if !ok {
			return fmt.Errorf("state config: unknown state %q", stateName)
		}
		name, ok := pb.MoveState_name[int32(state)]
		if !ok {
			return fmt.Errorf("state config: state %q has no proto name", stateName)
		}

		ds := StateConfigState{
			Duration:    float64(entry.DurationSeconds()),
			MaxPerFrame: float64(entry.MaxPerFrame()),
		}
		if len(entry.CurveX()) > 0 {
			keys, err := t.LoadCurveKeys(root, string(entry.CurveX()))
			if err != nil {
				return fmt.Errorf("state %q curve_x: %w", stateName, err)
			}
			ds.SetCurve("x", keys)
		}
		if len(entry.CurveY()) > 0 {
			keys, err := t.LoadCurveKeys(root, string(entry.CurveY()))
			if err != nil {
				return fmt.Errorf("state %q curve_y: %w", stateName, err)
			}
			ds.SetCurve("y", keys)
		}
		if len(entry.CurveZ()) > 0 {
			keys, err := t.LoadCurveKeys(root, string(entry.CurveZ()))
			if err != nil {
				return fmt.Errorf("state %q curve_z: %w", stateName, err)
			}
			ds.SetCurve("z", keys)
		}
		t.States[name] = ds
	}

	t.BuildCaps()
	return nil
}

func (st *StateConfigState) SetCurve(axis string, keys [][2]float64) {
	if st.Curves == nil {
		st.Curves = make(map[string][][2]float64)
	}
	st.Curves[axis] = keys
}

func (t *StateConfigTable) BuildCaps() {
	for name, st := range t.States {
		id, ok := pb.MoveState_value[name]
		if !ok {
			continue
		}
		state := pb.MoveState(id)
		if len(st.Curves) > 0 && st.Duration > 0 {
			t.caps[state] = t.ComputeStateCap(st) * t.Margin
		} else {
			t.caps[state] = st.MaxPerFrame
		}
	}
}

func (t *StateConfigTable) StateEntry(s pb.MoveState) (StateConfigState, bool) {
	name, ok := pb.MoveState_name[int32(s)]
	if !ok {
		return StateConfigState{}, false
	}
	st, ok := t.States[name]
	return st, ok
}

func (t *StateConfigTable) MaxPerFrame(state pb.MoveState) float64 {
	if v, ok := t.caps[state]; ok {
		return v
	}
	return maxMoveDelta
}

func (t *StateConfigTable) ComputeStateCap(st StateConfigState) float64 {
	sub := 16
	dt := t.FixedDelta / st.Duration
	step := dt / float64(sub)
	best := 0.0
	for a := 0.0; a < 1.0; a += step {
		dx := t.AxisDelta(st.Curves["x"], a, a+dt)
		dz := t.AxisDelta(st.Curves["z"], a, a+dt)
		if m := math.Hypot(dx, dz); m > best {
			best = m
		}
	}
	return best
}

func (t *StateConfigTable) AxisDelta(keys [][2]float64, a, b float64) float64 {
	if len(keys) == 0 {
		return 0
	}
	ca := t.EvalCurve(keys, a)
	if b <= 1 {
		return t.EvalCurve(keys, b) - ca
	}
	return t.EvalCurve(keys, 1) - ca + t.EvalCurve(keys, b-1) - t.EvalCurve(keys, 0)
}

func (t *StateConfigTable) EvalCurve(keys [][2]float64, x float64) float64 {
	if x <= keys[0][0] {
		return keys[0][1]
	}
	last := keys[len(keys)-1]
	if x >= last[0] {
		return last[1]
	}
	for i := 1; i < len(keys); i++ {
		if x <= keys[i][0] {
			k0, k1 := keys[i-1], keys[i]
			frac := (x - k0[0]) / (k1[0] - k0[0])
			return k0[1] + frac*(k1[1]-k0[1])
		}
	}
	return last[1]
}

func (t *StateConfigTable) DebugDump() {
	for name, st := range t.States {
		if len(st.Curves) > 0 {
			log.Printf("[displacement] %s dur=%.4f cap=%.4f", name, st.Duration, t.MaxPerFrame(pb.MoveState(pb.MoveState_value[name])))
		} else {
			log.Printf("[displacement] %s cap=%.4f", name, st.MaxPerFrame)
		}
	}
}
