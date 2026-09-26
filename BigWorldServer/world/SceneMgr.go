package main

import (
	"log"
	"path/filepath"

	"bigworld/common"
)

const mainInstanceID uint64 = 0

type sceneKey struct {
	SceneID    string
	InstanceID uint64
}

type sceneMgr struct {
	scenes       map[sceneKey]*scene
	grids        map[string]*voxelGrid
	nextInstance map[string]uint64
	defaultScene string
	voxelDir     string
}

func NewSceneMgr(id string, cfg common.ServerConfig) *sceneMgr {
	if err := SceneConfig().Load(cfg.SceneConfigFile); err != nil {
		log.Printf("[world %s] WARN: scene config %q failed to load: %v (using builtin defaults)",
			id, cfg.SceneConfigFile, err)
	}

	voxelDir := "../GameConfig"
	if cfg.SceneConfigFile != "" {
		voxelDir = filepath.Dir(filepath.Dir(cfg.SceneConfigFile))
	}

	defaultScene := ""
	for _, id := range SceneConfig().AllSceneIDs() {
		if SceneConfig().IsDefault(id) {
			defaultScene = id
			break
		}
	}
	if defaultScene == "" {
		for _, id := range SceneConfig().AllSceneIDs() {
			defaultScene = id
			break
		}
	}
	if defaultScene == "" {
		defaultScene = SceneConfig().DefaultScene()
	}

	m := &sceneMgr{
		scenes:       make(map[sceneKey]*scene),
		grids:        make(map[string]*voxelGrid),
		nextInstance: make(map[string]uint64),
		defaultScene: defaultScene,
		voxelDir:     voxelDir,
	}
	m.GridFor(m.defaultScene)
	return m
}

func (m *sceneMgr) NewInstance(sceneID string, instanceID uint64) *scene {
	key := sceneKey{SceneID: sceneID, InstanceID: instanceID}
	if sc, ok := m.scenes[key]; ok {
		return sc
	}
	entry, _ := SceneConfig().Get(sceneID)
	grid := m.GridFor(sceneID)
	width, height := 1280.0, 720.0
	if grid != nil {
		width, height = grid.width, grid.height
	}
	sc := NewScene(sceneID, instanceID, width, height, grid, entry.spawnX, entry.spawnZ)
	m.scenes[key] = sc
	return sc
}

func (m *sceneMgr) GetOrCreate(sceneID string) *scene {
	if _, ok := SceneConfig().Get(sceneID); !ok {
		if sceneID != "" {
			log.Printf("[scene] WARN: scene %q not configured, fallback to default %q", sceneID, m.defaultScene)
		}
		sceneID = m.defaultScene
	}
	return m.NewInstance(sceneID, mainInstanceID)
}

func (m *sceneMgr) CreateInstance(sceneID string) *scene {
	if _, ok := SceneConfig().Get(sceneID); !ok {
		return nil
	}
	m.nextInstance[sceneID]++
	return m.NewInstance(sceneID, m.nextInstance[sceneID])
}

func (m *sceneMgr) Has(sceneID string) bool {
	_, ok := m.scenes[sceneKey{SceneID: sceneID, InstanceID: mainInstanceID}]
	return ok
}

func (m *sceneMgr) GridFor(sceneID string) *voxelGrid {
	if g, ok := m.grids[sceneID]; ok {
		return g
	}
	g, err := LoadVoxelGrid(filepath.Join(m.voxelDir, sceneID+"_voxels.bytes"))
	if err != nil {
		log.Printf("[scene] WARN: voxel grid for scene %q failed to load: %v (movement validation disabled)", sceneID, err)
		g = nil
	} else {
		g.DebugDump()
	}
	m.grids[sceneID] = g
	return g
}
