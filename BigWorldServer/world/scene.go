package main

import "math/rand"

// scene represents a single playable 2D scene hosted by the world process.
// The world server may host many such scenes; gameplay logic (movement, skills,
// quests, ...) lives beside them in the same process.
type scene struct {
	SceneId string
	Width   float64
	Height  float64
	Players map[uint64]*playerEntity

	voxelGrid   *voxelGrid
	displacement *displacementTable
	maxStep     float64
	spawnX      float64
	spawnZ      float64

	// Server-authoritative movement simulation parameters (constant-speed states
	// and the character collider metrics used by the per-state classes).
	sprintSpeedMps    float64
	rollSpeedMps      float64
	fallGravityMps2   float64
	fallSpeedLimitMps float64
	playerHeight      float64
	playerCenterY     float64
}

func newScene(id string, w, h float64, g *voxelGrid, d *displacementTable, maxStep, spawnX, spawnZ float64,
	sprintSpeed, rollSpeed, fallGravity, fallSpeedLimit, playerHeight, playerCenterY float64) *scene {
	return &scene{
		SceneId:          id,
		Width:            w,
		Height:           h,
		Players:          make(map[uint64]*playerEntity),
		voxelGrid:        g,
		displacement:     d,
		maxStep:          maxStep,
		spawnX:           spawnX,
		spawnZ:           spawnZ,
		sprintSpeedMps:   sprintSpeed,
		rollSpeedMps:     rollSpeed,
		fallGravityMps2:  fallGravity,
		fallSpeedLimitMps: fallSpeedLimit,
		playerHeight:     playerHeight,
		playerCenterY:    playerCenterY,
	}
}

func (sc *scene) spawnPosition(resume bool, rx, ry float64) (float64, float64, int) {
	if sc.voxelGrid == nil {
		if resume {
			return rx, ry, -1
		}
		return 100 + rand.Float64()*(sc.Width-200), 100 + rand.Float64()*(sc.Height-200), -1
	}
	if resume {
		if cx, cz, ok := sc.voxelGrid.worldToColumn(rx, ry); ok {
			if k := sc.voxelGrid.topLayerAt(cx, cz); k >= 0 {
				return rx, ry, k
			}
		}
	}
	return sc.voxelGrid.resolveSpawn(sc.spawnX, sc.spawnZ)
}
