package main

import (
	"math/rand"

	"bigworld/world/Config"
)

type scene struct {
	SceneID   config.SceneID
	SceneName string
	Width     float64
	Height    float64
	Players   map[uint64]*playerEntity

	voxelGrid *voxelGrid
	spawnX    float64
	spawnZ    float64
}

func NewScene(id config.SceneID, name string, w, h float64, g *voxelGrid, spawnX, spawnZ float64) *scene {
	return &scene{
		SceneID:   id,
		SceneName: name,
		Width:     w,
		Height:    h,
		Players:   make(map[uint64]*playerEntity),
		voxelGrid: g,
		spawnX:    spawnX,
		spawnZ:    spawnZ,
	}
}

func (sc *scene) SpawnPosition(resume bool, rx, ry float64) (float64, float64, int) {
	if sc.voxelGrid == nil {
		if resume {
			return rx, ry, -1
		}
		return 100 + rand.Float64()*(sc.Width-200), 100 + rand.Float64()*(sc.Height-200), -1
	}
	if resume {
		if cx, cz, ok := sc.voxelGrid.WorldToColumn(rx, ry); ok {
			if k := sc.voxelGrid.TopLayerAt(cx, cz); k >= 0 {
				return rx, ry, k
			}
		}
	}
	return sc.voxelGrid.ResolveSpawn(sc.spawnX, sc.spawnZ)
}

func (sc *scene) voxelWorldColumn(wx, wz float64) (int, int, bool) {
	if sc.voxelGrid == nil {
		return 0, 0, false
	}
	return sc.voxelGrid.WorldToColumn(wx, wz)
}

func (sc *scene) voxelColumnCount(cx, cz int) int {
	if sc.voxelGrid == nil {
		return 0
	}
	return sc.voxelGrid.ColumnCount(cx, cz)
}

func (sc *scene) voxelTopLayer(cx, cz int) int {
	if sc.voxelGrid == nil {
		return -1
	}
	return sc.voxelGrid.TopLayerAt(cx, cz)
}
