package main

import (
	"time"

	pb "bigworld/common/pb"
)

// entitySnapshot is a complete copy of the per-entity movement simulation state
// at the end of one fixed tick. It is the restore point for frame-based rollback.
type entitySnapshot struct {
	Tick         int64
	X, Y, Z      float64
	VoxelK       int
	Airborne     bool
	State        pb.MoveState
	MoveDirX     float64
	MoveDirZ     float64
	CurveNorm    float64
	StateStartMs int64
	FallVelY     float64
}

// moveInputKind enumerates the recent player operations recorded for replay.
type moveInputKind uint8

const (
	moveInputStart moveInputKind = iota
	moveInputStop
	moveInputDirChange
)

// moveInput is one accepted player movement operation stamped onto the fixed
// tick grid. Inputs with the same tick are ordered by Seq (arrival order).
type moveInput struct {
	Seq   uint64
	Tick  int64
	Kind  moveInputKind
	State pb.MoveState
	DirX  float64
	DirZ  float64
}

// --- fixed tick timeline ---

func (ss *worldServer) TickToMs(tick int64) int64 { return tick * ss.moveTickMs }

func (ss *worldServer) MsToTick(ms int64) int64 {
	if ms < 0 {
		return 0
	}
	return ms / ss.moveTickMs
}

func (ss *worldServer) NowTick() int64 { return ss.MsToTick(time.Now().UnixMilli()) }

// NormalizeInputTick maps a client server_time_ms onto the server tick grid:
// <=0 falls back to the current tick, future timestamps beyond the accept
// window are rejected, and timestamps older than the rollback window are
// clamped to the oldest replayable tick.
func (ss *worldServer) NormalizeInputTick(ts, nowMs int64) (int64, bool) {
	nowTick := ss.MsToTick(nowMs)
	if ts <= 0 {
		return nowTick, true
	}
	if ts > nowMs+maxMoveWindowMs {
		return 0, false
	}
	oldestMs := nowMs - maxMoveWindowMs
	if ts < oldestMs {
		ts = oldestMs
	}
	tick := ss.MsToTick(ts)
	if tick > nowTick {
		tick = nowTick
	}
	return tick, true
}

// InitEntityTimeline puts a freshly created entity onto the tick grid and
// records its first authoritative snapshot.
func (ss *worldServer) InitEntityTimeline(e *playerEntity) {
	tick := ss.NowTick()
	e.SimTick = tick
	e.StateStartMs = ss.TickToMs(tick)
	e.LastMoveTimeMs = e.StateStartMs
	e.Snapshots = []entitySnapshot{CaptureEntitySnapshot(e)}
}

// --- snapshots ---

func CaptureEntitySnapshot(e *playerEntity) entitySnapshot {
	return entitySnapshot{
		Tick:         e.SimTick,
		X:            e.X,
		Y:            e.Y,
		Z:            e.Z,
		VoxelK:       e.VoxelK,
		Airborne:     e.Airborne,
		State:        e.State,
		MoveDirX:     e.MoveDirX,
		MoveDirZ:     e.MoveDirZ,
		CurveNorm:    e.CurveNorm,
		StateStartMs: e.StateStartMs,
		FallVelY:     e.FallVelY,
	}
}

func RestoreEntitySnapshot(e *playerEntity, s entitySnapshot) {
	e.SimTick = s.Tick
	e.X = s.X
	e.Y = s.Y
	e.Z = s.Z
	e.VoxelK = s.VoxelK
	e.Airborne = s.Airborne
	e.State = s.State
	e.MoveDirX = s.MoveDirX
	e.MoveDirZ = s.MoveDirZ
	e.CurveNorm = s.CurveNorm
	e.StateStartMs = s.StateStartMs
	e.FallVelY = s.FallVelY
}

// RecordEntitySnapshot appends the end-of-tick snapshot and prunes snapshots
// and inputs older than the rollback window.
func (ss *worldServer) RecordEntitySnapshot(e *playerEntity) {
	e.Snapshots = append(e.Snapshots, CaptureEntitySnapshot(e))
	cutoff := e.SimTick - ss.rollbackTicks

	start := 0
	for start < len(e.Snapshots) && e.Snapshots[start].Tick < cutoff {
		start++
	}
	if start > 0 {
		e.Snapshots = append(e.Snapshots[:0], e.Snapshots[start:]...)
	}

	kept := e.Inputs[:0]
	for _, in := range e.Inputs {
		if in.Tick >= cutoff {
			kept = append(kept, in)
		}
	}
	e.Inputs = kept
}

// AdvanceEntityTo simulates one fixed tick at a time until toTick is reached.
// Every tick boundary gets a snapshot, so a late input can rewind exactly to
// its own tick and replay frame by frame.
func (ss *worldServer) AdvanceEntityTo(e *playerEntity, toTick int64) {
	if toTick <= e.SimTick {
		return
	}
	for e.SimTick < toTick {
		next := e.SimTick + 1
		if cl := MoveStateClassFor(e.State); cl != nil {
			cl.Update(e, e.Scene, ss.moveTickMs, ss.TickToMs(next))
		}
		e.SimTick = next
		ss.RecordEntitySnapshot(e)
	}
}

// LatestSnapshotAtOrBefore returns the newest snapshot whose tick is <= tick.
func LatestSnapshotAtOrBefore(e *playerEntity, tick int64) (entitySnapshot, bool) {
	for i := len(e.Snapshots) - 1; i >= 0; i-- {
		if e.Snapshots[i].Tick <= tick {
			return e.Snapshots[i], true
		}
	}
	return entitySnapshot{}, false
}

// SnapshotAfterEventsAt reconstructs the authoritative state at the end of tick
// after every input stamped on that tick has been applied. Snapshot history
// stores the pre-input state of a tick, so MoveRsp needs this helper to report
// a snapshot that already contains the acknowledged input.
func (ss *worldServer) SnapshotAfterEventsAt(e *playerEntity, tick int64) (entitySnapshot, bool) {
	base, ok := LatestSnapshotAtOrBefore(e, tick)
	if !ok {
		return entitySnapshot{}, false
	}
	tmp := &playerEntity{Scene: e.Scene}
	RestoreEntitySnapshot(tmp, base)
	tmp.SimTick = base.Tick
	ss.AdvanceEntityTo(tmp, tick)
	for _, ev := range e.Inputs {
		if ev.Tick != tick {
			continue
		}
		if ok, _ := ss.ApplyMoveInputToEntity(tmp, ev); !ok {
			continue
		}
	}
	return CaptureEntitySnapshot(tmp), true
}

// --- input history ---

func InsertMoveInput(inputs []moveInput, in moveInput) []moveInput {
	idx := len(inputs)
	for i, cur := range inputs {
		if in.Tick < cur.Tick || (in.Tick == cur.Tick && in.Seq < cur.Seq) {
			idx = i
			break
		}
	}
	inputs = append(inputs, moveInput{})
	copy(inputs[idx+1:], inputs[idx:])
	inputs[idx] = in
	return inputs
}

// ApplyMoveInputToEntity applies one input at the state reconstructed for its
// tick. It mirrors the original handlers: same-state starts only update the
// direction, stops only reset non-idle states, and invalid transitions are
// rejected.
func (ss *worldServer) ApplyMoveInputToEntity(e *playerEntity, in moveInput) (bool, string) {
	stateMs := ss.TickToMs(in.Tick)
	switch in.Kind {
	case moveInputStart:
		if !CanTransition(e.State, in.State) {
			return false, "非法状态转换"
		}
		if in.DirX != 0 || in.DirZ != 0 {
			SetMoveDir(e, in.DirX, in.DirZ)
		}
		if e.State != in.State {
			SetMoveState(e, in.State, stateMs)
		}
	case moveInputStop:
		if e.State != pb.MoveState_MOVE_IDLE {
			SetMoveState(e, pb.MoveState_MOVE_IDLE, stateMs)
		}
	case moveInputDirChange:
		SetMoveDir(e, in.DirX, in.DirZ)
	}
	return true, ""
}

// ProcessMoveInput accepts one player movement input and makes the entity
// authoritative for that input:
//   - tick > sim tick: fast-forward to the input tick, apply, then catch up to now;
//   - tick <= sim tick: rollback to the snapshot at that tick and replay the
//     whole recent input history frame by frame, which is what fixes a late
//     MoveStop arriving after the server already simulated extra frames.
//
// On success it also returns the authoritative snapshot at the input tick
// (after that tick's inputs were applied) for the MoveRsp payload.
func (ss *worldServer) ProcessMoveInput(e *playerEntity, in moveInput) (bool, string, entitySnapshot) {
	nowTick := ss.NowTick()

	if in.Tick > e.SimTick {
		ss.AdvanceEntityTo(e, in.Tick)
		ok, msg := ss.ApplyMoveInputToEntity(e, in)
		if !ok {
			return false, msg, entitySnapshot{}
		}
		e.Inputs = InsertMoveInput(e.Inputs, in)
		e.InputSeq++
		snap, _ := ss.SnapshotAfterEventsAt(e, in.Tick)
		ss.AdvanceEntityTo(e, nowTick)
		e.LastMoveTimeMs = ss.TickToMs(in.Tick)
		return true, "", snap
	}

	return ss.RebuildEntityWithInput(e, in, nowTick)
}

// RebuildEntityWithInput rewinds to the tick of a late input, applies it, and
// replays every more recent recorded input up to the current tick. The rebuild
// is transactional: if the new input is rejected, the entity, snapshots and
// input history are restored exactly as they were.
func (ss *worldServer) RebuildEntityWithInput(e *playerEntity, in moveInput, nowTick int64) (bool, string, entitySnapshot) {
	restore, ok := LatestSnapshotAtOrBefore(e, in.Tick)
	if !ok {
		return false, "回滚快照不存在", entitySnapshot{}
	}

	// Transactional undo data.
	undoEntity := CaptureEntitySnapshot(e)
	undoSnapshots := append([]entitySnapshot(nil), e.Snapshots...)
	undoInputs := append([]moveInput(nil), e.Inputs...)
	undoSeq := e.InputSeq

	undo := func() {
		RestoreEntitySnapshot(e, undoEntity)
		e.Snapshots = undoSnapshots
		e.Inputs = undoInputs
		e.InputSeq = undoSeq
	}

	e.Inputs = InsertMoveInput(e.Inputs, in)
	events := append([]moveInput(nil), e.Inputs...)

	// Rewind to the tick boundary the input belongs to. The snapshot stores the
	// state before any inputs stamped on that tick (inputs are applied after the
	// tick step), so every event with Tick >= restore.Tick is replayed in
	// (Tick, Seq) order - including events that share the candidate's tick.
	RestoreEntitySnapshot(e, restore)
	e.SimTick = restore.Tick
	e.Snapshots = append(e.Snapshots[:0], restore)

	skipped := make(map[uint64]bool)
	for _, ev := range events {
		if ev.Tick < restore.Tick {
			continue // already reflected in the restore snapshot
		}
		ss.AdvanceEntityTo(e, ev.Tick)
		if ok, msg := ss.ApplyMoveInputToEntity(e, ev); !ok {
			if ev.Seq == in.Seq {
				undo()
				return false, msg, entitySnapshot{}
			}
			// A previously accepted input may no longer be legal after the
			// late input changed the reconstructed state; drop it.
			skipped[ev.Seq] = true
		}
	}

	ss.AdvanceEntityTo(e, nowTick)

	kept := make([]moveInput, 0, len(events))
	for _, ev := range events {
		if !skipped[ev.Seq] {
			kept = append(kept, ev)
		}
	}
	e.Inputs = kept
	cutoff := e.SimTick - ss.rollbackTicks
	pruned := e.Inputs[:0]
	for _, ev := range e.Inputs {
		if ev.Tick >= cutoff {
			pruned = append(pruned, ev)
		}
	}
	e.Inputs = pruned
	e.InputSeq = undoSeq + 1
	e.LastMoveTimeMs = ss.TickToMs(in.Tick)
	snap, ok := ss.SnapshotAfterEventsAt(e, in.Tick)
	if !ok {
		snap = CaptureEntitySnapshot(e)
	}
	return true, "", snap
}

// TickMoves advances every player on the fixed tick timeline up to the current
// wall-clock tick. Ticks are frame-based: each step advances exactly one tick,
// never a variable "elapsed time since last loop".
func (ss *worldServer) TickMoves() {
	nowTick := ss.NowTick()
	ss.mu.Lock()
	defer ss.mu.Unlock()
	for _, e := range ss.players {
		ss.AdvanceEntityTo(e, nowTick)
	}
}

// MoveLoop drives the fixed-tick movement timeline.
func (ss *worldServer) MoveLoop() {
	ticker := time.NewTicker(time.Duration(ss.moveTickMs) * time.Millisecond)
	defer ticker.Stop()
	for range ticker.C {
		ss.TickMoves()
	}
}
