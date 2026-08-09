package main

import (
	"math"
	"time"

	pb "bigworld/common/pb"
)

// moveStateClass is the per-state server-side movement simulator. Instances are
// stateless; per-entity progress (curve norm, start time, direction, fall speed)
// lives on the playerEntity. update() runs once per movement tick and advances
// the entity exactly as the matching Unity state drives the client character,
// using the same exported displacement curves and the same voxel validation.
type moveStateClass interface {
	update(e *playerEntity, sc *scene, dtMs, nowMs int64)
}

// moveStateClassFor returns the simulator class for a state, or nil for states
// that drive no position (IDLE, landing states — the client sends MoveStop on
// entering them and the entity just sits).
func moveStateClassFor(s pb.MoveState) moveStateClass {
	switch s {
	case pb.MoveState_MOVE_WALK:
		return walkState
	case pb.MoveState_MOVE_RUN:
		return runState
	case pb.MoveState_MOVE_SPRINT:
		return sprintState
	case pb.MoveState_MOVE_DASH:
		return dashState
	case pb.MoveState_MOVE_ROLL:
		return rollState
	case pb.MoveState_MOVE_STOP_LIGHT, pb.MoveState_MOVE_STOP_MED, pb.MoveState_MOVE_STOP_HARD:
		return stopState
	case pb.MoveState_MOVE_JUMP_UP, pb.MoveState_MOVE_JUMP_DOWN:
		return jumpState
	case pb.MoveState_MOVE_FALL:
		return fallState
	}
	return nil
}

var (
	walkState   moveStateClass = cyclicState{pb.MoveState_MOVE_WALK}
	runState    moveStateClass = cyclicState{pb.MoveState_MOVE_RUN}
	sprintState moveStateClass = constSpeedState{pb.MoveState_MOVE_SPRINT}
	rollState   moveStateClass = constSpeedState{pb.MoveState_MOVE_ROLL}
	dashState   moveStateClass = dashStateClass{}
	stopState   moveStateClass = stopStateClass{}
	jumpState   moveStateClass = jumpStateClass{}
	fallState   moveStateClass = fallStateClass{}
)

// setMoveState switches e to state s and resets the per-state sim fields.
// Direction and position are preserved (direction changes come from protocols).
func setMoveState(e *playerEntity, s pb.MoveState, nowMs int64) {
	e.State = s
	e.StateStartMs = nowMs
	e.CurveNorm = 0
	if s == pb.MoveState_MOVE_FALL {
		e.FallVelY = 0
	}
}

// setMoveDir normalizes and stores the entity's world-space horizontal direction
// (a degenerate input keeps the current direction).
func setMoveDir(e *playerEntity, x, z float64) {
	l := math.Hypot(x, z)
	if l < 1e-4 {
		return
	}
	e.MoveDirX = x / l
	e.MoveDirZ = z / l
}

// worldDelta projects a local-space displacement (curve local x/z) into world
// space using the entity's direction, mirroring the client's ApplyWorldDisplacement:
// world = forward*localZ + right*localX with right = cross(up, forward) = (dirZ, 0, -dirX).
func worldDelta(e *playerEntity, localX, localZ float64) (float64, float64) {
	return e.MoveDirX*localZ + e.MoveDirZ*localX,
		e.MoveDirZ*localZ - e.MoveDirX*localX
}

// allowedDelta is the voxel per-tick displacement cap scaled by the actual tick
// length, so a delayed tick isn't falsely rejected (mirrors the old moveDeltaMs
// Δt scaling).
func allowedDelta(dtMs int64) float64 {
	d := maxMoveDelta * float64(dtMs) / 20.0
	if d < maxMoveDelta {
		d = maxMoveDelta
	}
	return d
}

const (
	moveApplied = iota
	moveBlocked
	moveLedged
)

// groundDropAt reports whether moving from the current column into the target
// column would step off a ledge (empty/out-of-bounds target column, or the
// resolved layer's top is more than maxStep below the current top). The client
// turns those into a fall; the legacy validateGroundMove instead allowed or
// blocked them silently.
func groundDropAt(g *voxelGrid, fromX, fromZ float64, curK int, dx, dz, maxStep float64) bool {
	if g == nil || curK < 0 {
		return false
	}
	curX, curZ, ok := g.worldToColumn(fromX, fromZ)
	if !ok {
		return false
	}
	targetX, targetZ, ok := g.worldToColumn(fromX+dx, fromZ+dz)
	if !ok {
		return true
	}
	if targetX == curX && targetZ == curZ {
		return false
	}
	targetIdx := g.gridIndex(targetX, targetZ)
	if g.columns[targetIdx] == 0 {
		return true
	}
	resolvedK := g.resolveTargetLayer(curX, curZ, curK, targetX, targetZ)
	if resolvedK < 0 {
		return true
	}
	curVoxel := g.voxels[g.starts[g.gridIndex(curX, curZ)]+curK]
	targetVoxel := g.voxels[g.starts[targetIdx]+resolvedK]
	return curVoxel.maxY-targetVoxel.maxY > maxStep
}

// applyGroundedDelta applies a world-space horizontal delta with voxel
// validation, mirroring the client's ApplyVoxelMovement for grounded states.
func applyGroundedDelta(sc *scene, e *playerEntity, dx, dz, maxDelta float64) int {
	vg := sc.voxelGrid
	if vg == nil {
		e.X += dx
		e.Z += dz
		return moveApplied
	}
	k := e.VoxelK
	if e.Airborne || k < 0 {
		k = vg.resolveLayerNearY(e.X, e.Z, e.Y)
	}
	if k < 0 {
		e.X += dx
		e.Z += dz
		return moveApplied
	}
	if groundDropAt(vg, e.X, e.Z, k, dx, dz, sc.maxStep) {
		e.X += dx
		e.Z += dz
		return moveLedged
	}
	walkable, newK, newY := vg.validateGroundMove(e.X, e.Z, k, dx, dz, sc.maxStep, maxDelta)
	if !walkable || newK < 0 {
		return moveBlocked
	}
	e.X += dx
	e.Z += dz
	e.Y = newY
	e.VoxelK = newK
	e.Airborne = false
	return moveApplied
}

// applyJumpHorizontal applies a world-space horizontal delta during airborne
// jump states, mirroring ApplyVoxelMovement. Unlike applyGroundedDelta it never
// re-snaps Y (the curve drives it) and never treats a drop as a ledge.
func applyJumpHorizontal(sc *scene, e *playerEntity, dx, dz, maxDelta float64) {
	vg := sc.voxelGrid
	if vg == nil {
		e.X += dx
		e.Z += dz
		return
	}
	k := e.VoxelK
	if e.Airborne || k < 0 {
		k = vg.resolveLayerNearY(e.X, e.Z, e.Y)
	}
	if k < 0 {
		e.X += dx
		e.Z += dz
		return
	}
	walkable, _, _ := vg.validateGroundMove(e.X, e.Z, k, dx, dz, sc.maxStep, maxDelta)
	if walkable {
		e.X += dx
		e.Z += dz
	}
}

// tryLandOnVoxel mirrors the client's voxel landing check (pivot Y reached a
// voxel top surface). Returns true and snaps the entity when landed.
func tryLandOnVoxel(sc *scene, e *playerEntity, newY float64) bool {
	vg := sc.voxelGrid
	if vg == nil {
		return false
	}
	k := vg.resolveLayerNearY(e.X, e.Z, newY)
	top := vg.surfaceHeight(e.X, e.Z, k)
	if k >= 0 && newY <= top {
		e.Y = top
		e.Airborne = false
		e.VoxelK = k
		return true
	}
	return false
}

// --- cyclic states (walk / run): looping curves, normalizedTime % 1 ---

type cyclicState struct {
	state pb.MoveState
}

func (s cyclicState) update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.stateEntry(s.state)
	if !ok {
		return
	}
	elapsed := float64(nowMs-e.StateStartMs) / 1000.0
	if elapsed < 0 {
		elapsed = 0
	}
	norm := math.Mod(elapsed/entry.Duration, 1.0)
	a := e.CurveNorm
	b := norm
	if b < a {
		b += 1 // cycle wrapped: tail of old cycle + head of new cycle
	}
	dx := axisDelta(entry.Curves["x"], a, b)
	dz := axisDelta(entry.Curves["z"], a, b)
	// Always advance the curve norm, mirroring the animator (which keeps looping
	// even while pinned against a wall), so direction changes stay in sync.
	e.CurveNorm = norm
	if dx == 0 && dz == 0 {
		return
	}
	wx, wz := worldDelta(e, dx, dz)
	res := applyGroundedDelta(sc, e, wx, wz, allowedDelta(dtMs))
	if res == moveLedged {
		setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- constant-speed states (sprint / roll): speed * dt along the direction ---

type constSpeedState struct {
	state pb.MoveState
}

func (s constSpeedState) update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	speed := sc.sprintSpeedMps
	if s.state == pb.MoveState_MOVE_ROLL {
		speed = sc.rollSpeedMps
	}
	if speed <= 0 {
		return
	}
	dist := speed * float64(dtMs) / 1000.0
	res := applyGroundedDelta(sc, e, e.MoveDirX*dist, e.MoveDirZ*dist, allowedDelta(dtMs))
	if res == moveLedged {
		setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- one-shot states (dash / stop): timed curve, norm clamped to [0,1] ---

type dashStateClass struct{}

func (dashStateClass) update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.stateEntry(pb.MoveState_MOVE_DASH)
	if !ok {
		setMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	norm := oneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := axisDelta(entry.Curves["x"], a, norm)
	dz := axisDelta(entry.Curves["z"], a, norm)
	e.CurveNorm = norm
	if dx != 0 || dz != 0 {
		wx, wz := worldDelta(e, dx, dz)
		res := applyGroundedDelta(sc, e, wx, wz, allowedDelta(dtMs))
		if res == moveLedged {
			setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
			return
		}
	}
	if norm >= 1 {
		setMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
	}
}

type stopStateClass struct{}

func (stopStateClass) update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.stateEntry(e.State)
	if !ok || len(entry.Curves) == 0 {
		// STOP_HARD has no exported curves and the client doesn't move during it;
		// hold still until the client sends the next protocol.
		return
	}
	norm := oneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := axisDelta(entry.Curves["x"], a, norm)
	dz := axisDelta(entry.Curves["z"], a, norm)
	e.CurveNorm = norm
	if dx != 0 || dz != 0 {
		wx, wz := worldDelta(e, dx, dz)
		res := applyGroundedDelta(sc, e, wx, wz, allowedDelta(dtMs))
		if res == moveLedged {
			setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
			return
		}
	}
	if norm >= 1 {
		setMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
	}
}

func oneShotNorm(e *playerEntity, duration float64, nowMs int64) float64 {
	if duration <= 0 {
		return 1
	}
	elapsed := float64(nowMs-e.StateStartMs) / 1000.0
	norm := elapsed / duration
	if norm < 0 {
		return 0
	}
	if norm > 1 {
		return 1
	}
	return norm
}

// --- jump (composite): JUMP_UP curve arc then JUMP_DOWN curve descent ---

type jumpStateClass struct{}

func (jumpStateClass) update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	if e.State == pb.MoveState_MOVE_JUMP_UP {
		jumpUpUpdate(e, sc, dtMs, nowMs)
	} else {
		jumpDownUpdate(e, sc, dtMs, nowMs)
	}
}

func jumpUpUpdate(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.stateEntry(pb.MoveState_MOVE_JUMP_UP)
	if !ok {
		setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	norm := oneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := axisDelta(entry.Curves["x"], a, norm)
	dz := axisDelta(entry.Curves["z"], a, norm)
	dy := evalCurve(entry.Curves["y"], norm) - evalCurve(entry.Curves["y"], a)
	e.CurveNorm = norm

	if dx != 0 || dz != 0 {
		wx, wz := worldDelta(e, dx, dz)
		applyJumpHorizontal(sc, e, wx, wz, allowedDelta(dtMs))
	}
	newY := e.Y + dy
	if sc.voxelGrid != nil {
		half := sc.playerHeight / 2
		headTop := newY + sc.playerCenterY + half
		feet := newY + sc.playerCenterY - half
		if hit, ceilingY := sc.voxelGrid.ceilingHit(e.X, e.Z, feet, headTop); hit {
			e.Y = ceilingY - sc.playerCenterY - half
			e.Airborne = true
			setMoveState(e, pb.MoveState_MOVE_JUMP_DOWN, nowMs)
			return
		}
	}
	e.Y = newY
	e.Airborne = true
	if norm >= 1 {
		setMoveState(e, pb.MoveState_MOVE_JUMP_DOWN, nowMs)
	}
}

func jumpDownUpdate(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.stateEntry(pb.MoveState_MOVE_JUMP_DOWN)
	if !ok {
		setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	norm := oneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	if norm < a {
		// animation wrapped while still airborne -> FallingState
		setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	dx := axisDelta(entry.Curves["x"], a, norm)
	dz := axisDelta(entry.Curves["z"], a, norm)
	dy := evalCurve(entry.Curves["y"], norm) - evalCurve(entry.Curves["y"], a)
	if dy > 0 {
		dy = 0 // descent only, mirroring the client
	}
	e.CurveNorm = norm

	if dx != 0 || dz != 0 {
		wx, wz := worldDelta(e, dx, dz)
		applyJumpHorizontal(sc, e, wx, wz, allowedDelta(dtMs))
	}
	newY := e.Y + dy
	if tryLandOnVoxel(sc, e, newY) {
		setMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	e.Y = newY
	e.Airborne = true
	if norm >= 1 {
		setMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- fall: gravity accumulation, no horizontal drift, voxel landing ---

type fallStateClass struct{}

func (fallStateClass) update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	dt := float64(dtMs) / 1000.0
	if dt > 0.05 {
		dt = 0.05 // clamp a late tick so a single stall can't tunnel the map
	}
	e.FallVelY -= sc.fallGravityMps2 * dt
	if e.FallVelY < -sc.fallSpeedLimitMps {
		e.FallVelY = -sc.fallSpeedLimitMps
	}
	newY := e.Y + e.FallVelY*dt
	if tryLandOnVoxel(sc, e, newY) {
		setMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	e.Y = newY
	e.Airborne = true
}

// tickMoves advances every moving player's position by one sim tick. Runs on the
// moveLoop goroutine under ss.mu.
func (ss *worldServer) tickMoves() {
	now := time.Now().UnixMilli()
	ss.mu.Lock()
	defer ss.mu.Unlock()
	for _, e := range ss.players {
		cl := moveStateClassFor(e.State)
		if cl == nil {
			continue
		}
		dt := now - ss.lastTickMs
		if dt <= 0 {
			dt = int64(ss.moveTickMs)
		}
		cl.update(e, e.Scene, dt, now)
	}
	ss.lastTickMs = now
}
