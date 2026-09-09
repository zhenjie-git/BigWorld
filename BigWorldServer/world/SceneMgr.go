package main

import (
	"log"
	"path/filepath"

	"bigworld/common"
)

type sceneMgr struct {
	scenes       map[string]*scene
	grids        map[string]*voxelGrid
	defaultScene string
	voxelDir     string
}

func NewSceneMgr(id string) *sceneMgr {
	cfg := common.Config.Servers["world"]
	if err := SceneConfig().Load(cfg.SceneConfigFile); err != nil {
		log.Printf("[world %s] WARN: scene config %q failed to load: %v (using builtin defaults)",
			id, cfg.SceneConfigFile, err)
	}

	voxelDir := "../GameConfig"
	if cfg.SceneConfigFile != "" {
		voxelDir = filepath.Dir(filepath.Dir(cfg.SceneConfigFile))
	}

	m := &sceneMgr{
		scenes:       make(map[string]*scene),
		grids:        make(map[string]*voxelGrid),
		defaultScene: SceneConfig().DefaultScene(),
		voxelDir:     voxelDir,
	}
	m.gridFor(m.defaultScene)
	return m
}

func (m *sceneMgr) GetOrCreate(sceneId string) *scene {
	if _, ok := SceneConfig().Get(sceneId); !ok {
		if sceneId != "" {
			log.Printf("[scene] WARN: unknown scene %q, fallback to default %q", sceneId, m.defaultScene)
		}
		sceneId = m.defaultScene
	}
	if sc, ok := m.scenes[sceneId]; ok {
		return sc
	}
	entry, _ := SceneConfig().Get(sceneId)
	grid := m.gridFor(sceneId)
	width, height := 1280.0, 720.0
	if grid != nil {
		width, height = grid.width, grid.height
	}
	sc := NewScene(sceneId, width, height, grid, entry.spawnX, entry.spawnZ)
	m.scenes[sceneId] = sc
	return sc
}

func (m *sceneMgr) gridFor(sceneId string) *voxelGrid {
	if g, ok := m.grids[sceneId]; ok {
		return g
	}
	g, err := LoadVoxelGrid(filepath.Join(m.voxelDir, sceneId+"_voxels.bytes"))
	if err != nil {
		log.Printf("[scene] WARN: voxel grid for scene %q failed to load: %v (movement validation disabled)", sceneId, err)
		g = nil
	} else {
		g.DebugDump()
	}
	m.grids[sceneId] = g
	return g
}
