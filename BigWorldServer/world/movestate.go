package main

import (
	"math"

	pb "bigworld/common/pb"
)

func IsAirborneState(s pb.MoveState) bool {
	return s == pb.MoveState_MOVE_JUMP_UP ||
		s == pb.MoveState_MOVE_JUMP_DOWN ||
		s == pb.MoveState_MOVE_FALL
}

type moveStateClass interface {
	Update(e *playerEntity, sc *scene, dtMs, nowMs int64)
}

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

func SetMoveState(e *playerEntity, s pb.MoveState, nowMs int64) {
	e.State = s
	e.StateStartMs = nowMs
	e.CurveNorm = 0
	if s == pb.MoveState_MOVE_FALL {
		e.FallVelY = 0
	}
}

func SetMoveDir(e *playerEntity, x, z float64) {
	l := math.Hypot(x, z)
	if l < 1e-4 {
		return
	}
	e.MoveDirX = x / l
	e.MoveDirZ = z / l
}

func WorldDelta(e *playerEntity, localX, localZ float64) (float64, float64) {
	return e.MoveDirX*localZ + e.MoveDirZ*localX,
		e.MoveDirZ*localZ - e.MoveDirX*localX
}

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
	if GroundDropAt(vg, e.X, e.Z, k, dx, dz, PlayerConfig().MaxStep()) {
		e.X += dx
		e.Z += dz
		return moveLedged
	}
	walkable, newK, newY := vg.ValidateGroundMove(e.X, e.Z, k, dx, dz, PlayerConfig().MaxStep(), maxDelta)
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
	walkable, _, _ := vg.ValidateGroundMove(e.X, e.Z, k, dx, dz, PlayerConfig().MaxStep(), maxDelta)
	if walkable {
		e.X += dx
		e.Z += dz
	}
}

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

type cyclicState struct {
	state pb.MoveState
}

func (s cyclicState) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := StateConfig().StateEntry(s.state)
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
		b += 1
	}
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, b)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, b)

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

type sprintStateClass struct{}

func (sprintStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := StateConfig().StateEntry(pb.MoveState_MOVE_SPRINT)
	if !ok || entry.Duration <= 0 {
		return
	}
	elapsed := float64(nowMs-e.StateStartMs) / 1000.0
	if elapsed < 0 {
		elapsed = 0
	}
	if elapsed >= PlayerConfig().SprintToRunTime() {
		SetMoveState(e, pb.MoveState_MOVE_RUN, nowMs)
		return
	}
	norm := math.Mod(elapsed/entry.Duration, 1.0)
	a := e.CurveNorm
	b := norm
	if b < a {
		b += 1
	}
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, b)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, b)
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

type oneShotGroundedState struct {
	state pb.MoveState
}

func (s oneShotGroundedState) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := StateConfig().StateEntry(s.state)
	if !ok || len(entry.Curves) == 0 {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, norm)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, norm)
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
	entry, ok := StateConfig().StateEntry(pb.MoveState_MOVE_DASH)
	if !ok {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, norm)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, norm)
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
	entry, ok := StateConfig().StateEntry(e.State)
	if !ok || len(entry.Curves) == 0 {

		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, norm)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, norm)
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

type jumpStateClass struct{}

func (jumpStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	if e.State == pb.MoveState_MOVE_JUMP_UP {
		JumpUpUpdate(e, sc, dtMs, nowMs)
	} else {
		JumpDownUpdate(e, sc, dtMs, nowMs)
	}
}

func JumpUpUpdate(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	entry, ok := StateConfig().StateEntry(pb.MoveState_MOVE_JUMP_UP)
	if !ok {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, norm)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, norm)
	dy := StateConfig().EvalCurve(entry.Curves["y"], norm) - StateConfig().EvalCurve(entry.Curves["y"], a)
	e.CurveNorm = norm

	if dx != 0 || dz != 0 {
		wx, wz := WorldDelta(e, dx, dz)
		ApplyJumpHorizontal(sc, e, wx, wz, AllowedDelta(dtMs))
	}
	newY := e.Y + dy
	if sc.voxelGrid != nil {
		half := PlayerConfig().PlayerHeight() / 2
		headTop := newY + PlayerConfig().PlayerCenterY() + half
		feet := newY + PlayerConfig().PlayerCenterY() - half
		if hit, ceilingY := sc.voxelGrid.CeilingHit(e.X, e.Z, feet, headTop); hit {
			e.Y = ceilingY - PlayerConfig().PlayerCenterY() - half
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
	entry, ok := StateConfig().StateEntry(pb.MoveState_MOVE_JUMP_DOWN)
	if !ok {
		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	norm := OneShotNorm(e, entry.Duration, nowMs)
	a := e.CurveNorm
	if norm < a {

		SetMoveState(e, pb.MoveState_MOVE_FALL, nowMs)
		return
	}
	dx := StateConfig().AxisDelta(entry.Curves["x"], a, norm)
	dz := StateConfig().AxisDelta(entry.Curves["z"], a, norm)
	dy := StateConfig().EvalCurve(entry.Curves["y"], norm) - StateConfig().EvalCurve(entry.Curves["y"], a)
	if dy > 0 {
		dy = 0
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

type fallStateClass struct{}

func (fallStateClass) Update(e *playerEntity, sc *scene, dtMs, nowMs int64) {
	dt := float64(dtMs) / 1000.0
	if dt > 0.05 {
		dt = 0.05
	}
	e.FallVelY -= PlayerConfig().FallGravityMps2() * dt
	if e.FallVelY < -PlayerConfig().FallSpeedLimitMps() {
		e.FallVelY = -PlayerConfig().FallSpeedLimitMps()
	}
	newY := e.Y + e.FallVelY*dt
	if TryLandOnVoxel(sc, e, newY) {
		SetMoveState(e, pb.MoveState_MOVE_IDLE, nowMs)
		return
	}
	e.Y = newY
	e.Airborne = true
}
