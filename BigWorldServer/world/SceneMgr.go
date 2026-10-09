package main

import (
	"bigworld/world/Config"
	"log"
	"path/filepath"

	"bigworld/common"
)

type sceneMgr struct {
	scenes       map[config.SceneID]*scene
	grids        map[string]*voxelGrid
	defaultScene config.SceneID
	voxelDir     string
}

func NewSceneMgr(id string, cfg common.ServerConfig) *sceneMgr {
	if err := config.SceneConfig().Load(cfg.SceneConfigFile); err != nil {
		log.Fatalf("[world %s] scene config %q failed to load: %v", id, cfg.SceneConfigFile, err)
	}

	m := &sceneMgr{
		scenes:       make(map[config.SceneID]*scene),
		grids:        make(map[string]*voxelGrid),
		defaultScene: config.SceneConfig().DefaultSceneID(),
		voxelDir:     filepath.Dir(filepath.Dir(cfg.SceneConfigFile)),
	}
	for _, id := range config.SceneConfig().AllSceneIDs() {
		m.GridFor(config.SceneConfig().NameOf(id))
	}
	return m
}

func (m *sceneMgr) NewInstance(sceneID config.SceneID) *scene {
	if sc, ok := m.scenes[sceneID]; ok {
		return sc
	}
	entry, _ := config.SceneConfig().Get(sceneID)
	grid := m.GridFor(entry.Name)
	width, height := 1280.0, 720.0
	if grid != nil {
		width, height = grid.width, grid.height
	}
	sc := NewScene(sceneID, entry.Name, width, height, grid, entry.SpawnX, entry.SpawnZ)
	m.scenes[sceneID] = sc
	return sc
}

func (m *sceneMgr) GetOrCreate(sceneID config.SceneID) *scene {
	if _, ok := config.SceneConfig().Get(sceneID); !ok {
		if sceneID.Valid() {
			log.Printf("[scene] WARN: scene %d(%s) not configured, fallback to default %s",
				int64(sceneID), config.SceneConfig().NameOf(sceneID), config.SceneConfig().DefaultSceneName())
		}
		sceneID = m.defaultScene
	}
	return m.NewInstance(sceneID)
}

func (m *sceneMgr) CreateInstance(sceneID config.SceneID) *scene {
	if _, ok := config.SceneConfig().Get(sceneID); !ok {
		return nil
	}
	return m.NewInstance(config.SceneConfig().NextInstance(sceneID))
}

func (m *sceneMgr) Has(sceneID config.SceneID) bool {
	_, ok := m.scenes[sceneID]
	return ok
}

func (m *sceneMgr) PlayerCount(sceneID config.SceneID) int {
	sc, ok := m.scenes[sceneID]
	if !ok {
		return 0
	}
	return len(sc.Players)
}

func (m *sceneMgr) GridFor(sceneName string) *voxelGrid {
	if g, ok := m.grids[sceneName]; ok {
		return g
	}
	g, err := LoadVoxelGrid(filepath.Join(m.voxelDir, sceneName+"_voxels.bytes"))
	if err != nil {
		log.Printf("[scene] WARN: voxel grid for scene %q failed to load: %v (movement validation disabled)", sceneName, err)
		g = nil
	} else {
		g.DebugDump()
	}
	m.grids[sceneName] = g
	return g
}
