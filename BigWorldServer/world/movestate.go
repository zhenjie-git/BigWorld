package main

import (
	"math"

	pb "bigworld/common/pb"
)

// moveStateClass is the per-state server-side movement simulator. Instances are
// stateless; per-entity progress (curve norm, start time, direction, fall speed)
// lives on the playerEntity. Update runs once per movement tick and advances
// the entity exactly as the matching Unity state drives the client character,
// using the same exported displacement curves and the same voxel validation.
type moveStateClass interface {
	Update(e *playerEntity, sc *scene, dtMs, nowMs int64)
}

// MoveStateClassFor returns the simulator class for a state, or nil for states
// that drive no position (IDLE, landing states — the client sends MoveStop on
// entering them and the entity just sits).
func MoveStateClassFor(s pb.MoveState) moveStateClass {
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
	sprintState moveStateClass = sprintStateClass{}
	rollState   moveStateClass = oneShotGroundedState{pb.MoveState_MOVE_ROLL}
	dashState   moveStateClass = dashStateClass{}
	stopState   moveStateClass = stopStateClass{}
	jumpState   moveStateClass = jumpStateClass{}
	fallState   moveStateClass = fallStateClass{}
)

// SetMoveState switches e to state s and resets the per-state sim fields.
// Direction and position are preserved (direction changes come from protocols).
func SetMoveState(e *playerEntity, s pb.MoveState, nowMs int64) {
	e.State = s
	e.StateStartMs = nowMs
	e.CurveNorm = 0
	if s == pb.MoveState_MOVE_FALL {
		e.FallVelY = 0
	}
}

// SetMoveDir normalizes and stores the entity's world-space horizontal direction
// (a degenerate input keeps the current direction).
func SetMoveDir(e *playerEntity, x, z float64) {
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
func WorldDelta(e *playerEntity, localX, localZ float64) (float64, float64) {
	return e.MoveDirX*localZ + e.MoveDirZ*localX,
		e.MoveDirZ*localZ - e.MoveDirX*localX
}

// allowedDelta is the voxel per-tick displacement cap scaled by the actual tick
// length, so a delayed tick isn't falsely rejected (mirrors the old moveDeltaMs
// Δt scaling).
func AllowedDelta(dtMs int64) float64 {
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
func GroundDropAt(g *voxelGrid, fromX, fromZ float64, curK int, dx, dz, maxStep float64) bool {
	if g == nil || curK < 0 {
		return false
	}
	curX, curZ, ok := g.WorldToColumn(fromX, fromZ)
	if !ok {
		return false
	}
	targetX, targetZ, ok := g.WorldToColumn(fromX+dx, fromZ+dz)
	if !ok {
		return true
	}
	if targetX == curX && targetZ == curZ {
		return false
	}
	targetIdx := g.GridIndex(targetX, targetZ)
	if g.columns[targetIdx] == 0 {
		return true
	}
	resolvedK := g.ResolveTargetLayer(curX, curZ, curK, targetX, targetZ)
	if resolvedK < 0 {
		return true
	}
	curVoxel := g.voxels[g.starts[g.GridIndex(curX, curZ)]+curK]
	targetVoxel := g.voxels[g.starts[targetIdx]+resolvedK]
	return curVoxel.maxY-targetVoxel.maxY > maxStep
}

// applyGroundedDelta applies a world-space horizontal delta with voxel
// validation, mirroring the client's ApplyVoxelMovement for grounded states.
func ApplyGroundedDelta(sc *scene, e *playerEntity, dx, dz, maxDelta float64) int {
	vg := sc.voxelGrid
	if vg == nil {
		e.X += dx
		e.Z += dz
		return moveApplied
	}
	k := e.VoxelK
	if e.Airborne || k < 0 {
		k = vg.ResolveLayerNearY(e.X, e.Z, e.Y)
	}
	if k < 0 {
		e.X += dx
		e.Z += dz
		return moveApplied
	}
	if GroundDropAt(vg, e.X, e.Z, k, dx, dz, sc.maxStep) {
		e.X += dx
		e.Z += dz
		return moveLedged
	}
	walkable, newK, newY := vg.ValidateGroundMove(e.X, e.Z, k, dx, dz, sc.maxStep, maxDelta)
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
func ApplyJumpHorizontal(sc *scene, e *playerEntity, dx, dz, maxDelta float64) {
	vg := sc.voxelGrid
	if vg == nil {
		e.X += dx
		e.Z += dz
		return
	}
	k := e.VoxelK
	if e.Airborne || k < 0 {
		k = vg.ResolveLayerNearY(e.X, e.Z, e.Y)
	}
	if k < 0 {
		e.X += dx
		e.Z += dz
		return
	}
	walkable, _, _ := vg.ValidateGroundMove(e.X, e.Z, k, dx, dz, sc.maxStep, maxDelta)
	if walkable {
		e.X += dx
		e.Z += dz
	}
}

// tryLandOnVoxel mirrors the client's voxel landing check (pivot Y reached a
// voxel top surface). Returns true and snaps the entity when landed.
func TryLandOnVoxel(sc *scene, e *playerEntity, newY float64) bool {
	vg := sc.voxelGrid
	if vg == nil {
		return false
	}
	k := vg.ResolveLayerNearY(e.X, e.Z, newY)
	top := vg.SurfaceHeight(e.X, e.Z, k)
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

func (s cyclicState) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(s.state)
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
	dx := AxisDelta(entry.Curves["x"], a, b)
	dz := AxisDelta(entry.Curves["z"], a, b)
	// Always advance the curve norm, mirroring the animator (which keeps looping
	// even while pinned against a wall), so direction changes stay in sync.
	e.CurveNorm = norm
	if dx == 0 && dz == 0 {
		return
	}
	wx, wz := WorldDelta(e, dx, dz)
	res := ApplyGroundedDelta(sc, e, wx, wz, AllowedDelta(dtMs))
	if res == moveLedged {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- sprint: cyclic curve movement, then auto transition to RUN after timeout ---

type sprintStateClass struct{}

func (sprintStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(pb.MoveState_MOVE_SPRINT)
	if !ok || entry.Duration <= 0 {
		return
	}
	elapsed := float64(nowMs-e.StateStartMs) / 1000.0
	if elapsed < 0 {
		elapsed = 0
	}
	if elapsed >= sc.sprintToRunTime {
		SetMoveState(e, pb.MoveState_MOVE_RUN, nowMs)
		return
	}
	norm := math.Mod(elapsed/entry.Duration, 1.0)
	a := e.CurveNorm
	b := norm
	if b < a {
		b += 1
	}
	dx := AxisDelta(entry.Curves["x"], a, b)
	dz := AxisDelta(entry.Curves["z"], a, b)
	e.CurveNorm = norm
	if dx == 0 && dz == 0 {
		return
	}
	wx, wz := WorldDelta(e, dx, dz)
	res := ApplyGroundedDelta(sc, e, wx, wz, AllowedDelta(dtMs))
	if res == moveLedged {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- constant-speed states (roll fallback when curves are missing): speed * dt along the direction ---

type constSpeedState struct {
	state pb.MoveState
}

func (s constSpeedState) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	speed := sc.sprintSpeedMps
	if s.state == pb.MoveState_MOVE_ROLL {
		speed = sc.rollSpeedMps
	}
	if speed <= 0 {
		return
	}
	dist := speed * float64(dtMs) / 1000.0
	res := ApplyGroundedDelta(sc, e, e.MoveDirX*dist, e.MoveDirZ*dist, AllowedDelta(dtMs))
	if res == moveLedged {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- one-shot grounded states (roll / dash / stop): timed curve, norm clamped to [0,1] ---

type oneShotGroundedState struct {
	state pb.MoveState
}

func (s oneShotGroundedState) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(s.state)
	if !ok || len(entry.Curves) == 0 {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := AxisDelta(entry.Curves["x"], a, norm)
	dz := AxisDelta(entry.Curves["z"], a, norm)
	e.CurveNorm = norm
	if dx != 0 || dz != 0 {
		wx, wz := WorldDelta(e, dx, dz)
		res := ApplyGroundedDelta(sc, e, wx, wz, AllowedDelta(dtMs))
		if res == moveLedged {
			SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
			return
		}
	}
	if norm >= 1 {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
	}
}

type dashStateClass struct{}

func (dashStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(pb.MoveState_MOVE_DASH)
	if !ok {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := AxisDelta(entry.Curves["x"], a, norm)
	dz := AxisDelta(entry.Curves["z"], a, norm)
	e.CurveNorm = norm
	if dx != 0 || dz != 0 {
		wx, wz := WorldDelta(e, dx, dz)
		res := ApplyGroundedDelta(sc, e, wx, wz, AllowedDelta(dtMs))
		if res == moveLedged {
			SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
			return
		}
	}
	if norm >= 1 {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
	}
}

type stopStateClass struct{}

func (stopStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(e.State)
	if !ok || len(entry.Curves) == 0 {
		// STOP_HARD has no exported curves and the client doesn't move during it;
		// hold still until the client sends the next protocol.
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := AxisDelta(entry.Curves["x"], a, norm)
	dz := AxisDelta(entry.Curves["z"], a, norm)
	e.CurveNorm = norm
	if dx != 0 || dz != 0 {
		wx, wz := WorldDelta(e, dx, dz)
		res := ApplyGroundedDelta(sc, e, wx, wz, AllowedDelta(dtMs))
		if res == moveLedged {
			SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
			return
		}
	}
	if norm >= 1 {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
	}
}

func OneShotNorm(e *playerEntity, duration float64, nowMs int64) float64 {
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

func (jumpStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	if e.State == pb.MoveState_MOVE_JUMP_UP {
		JumpUpUpdate(e, sc, dtMs, nowMs)
	} else {
		JumpDownUpdate(e, sc, dtMs, nowMs)
	}
}

func JumpUpUpdate(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(pb.MoveState_MOVE_JUMP_UP)
	if !ok {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := AxisDelta(entry.Curves["x"], a, norm)
	dz := AxisDelta(entry.Curves["z"], a, norm)
	dy := EvalCurve(entry.Curves["y"], norm) - EvalCurve(entry.Curves["y"], a)
	e.CurveNorm = norm

	if dx != 0 || dz != 0 {
		wx, wz := WorldDelta(e, dx, dz)
		ApplyJumpHorizontal(sc, e, wx, wz, AllowedDelta(dtMs))
	}
	newY := e.Y + dy
	if sc.voxelGrid != nil {
		half := sc.playerHeight / 2
		headTop := newY + sc.playerCenterY + half
		feet := newY + sc.playerCenterY - half
		if hit, ceilingY := sc.voxelGrid.CeilingHit(e.X, e.Z, feet, headTop); hit {
			e.Y = ceilingY - sc.playerCenterY - half
			e.Airborne = true
			SetMoveState(e, pb.MoveState_MOVE_JUMP_DOWN, nowMs)
			return
		}
	}
	e.Y = newY
	e.Airborne = true
	if norm >= 1 {
		SetMoveState(e, pb.MoveState_MOVE_JUMP_DOWN, nowMs)
	}
}

func JumpDownUpdate(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := sc.displacement.StateEntry(pb.MoveState_MOVE_JUMP_DOWN)
	if !ok {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	if norm < a {
		// animation wrapped while still airborne -> FallingState
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	dx := AxisDelta(entry.Curves["x"], a, norm)
	dz := AxisDelta(entry.Curves["z"], a, norm)
	dy := EvalCurve(entry.Curves["y"], norm) - EvalCurve(entry.Curves["y"], a)
	if dy > 0 {
		dy = 0 // descent only, mirroring the client
	}
	e.CurveNorm = norm

	if dx != 0 || dz != 0 {
		wx, wz := WorldDelta(e, dx, dz)
		ApplyJumpHorizontal(sc, e, wx, wz, AllowedDelta(dtMs))
	}
	newY := e.Y + dy
	if TryLandOnVoxel(sc, e, newY) {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	e.Y = newY
	e.Airborne = true
	if norm >= 1 {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
	}
}

// --- fall: gravity accumulation, no horizontal drift, voxel landing ---

type fallStateClass struct{}

func (fallStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	dt := float64(dtMs) / 1000.0
	if dt > 0.05 {
		dt = 0.05 // clamp a late tick so a single stall can't tunnel the map
	}
	e.FallVelY -= sc.fallGravityMps2 * dt
	if e.FallVelY < -sc.fallSpeedLimitMps {
		e.FallVelY = -sc.fallSpeedLimitMps
	}
	newY := e.Y + e.FallVelY*dt
	if TryLandOnVoxel(sc, e, newY) {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	e.Y = newY
	e.Airborne = true
}
