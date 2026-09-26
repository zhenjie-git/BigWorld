package main

import "math/rand"

type scene struct {
	SceneID    string
	InstanceID uint64
	Width      float64
	Height     float64
	Players    map[uint64]*playerEntity

	voxelGrid *voxelGrid
	spawnX    float64
	spawnZ    float64
}

func NewScene(id string, instanceID uint64, w, h float64, g *voxelGrid, spawnX, spawnZ float64) *scene {
	return &scene{
		SceneID:    id,
		InstanceID: instanceID,
		Width:      w,
		Height:     h,
		Players:    make(map[uint64]*playerEntity),
		voxelGrid:  g,
		spawnX:     spawnX,
		spawnZ:     spawnZ,
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
