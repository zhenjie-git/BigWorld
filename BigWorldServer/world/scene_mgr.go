package main

import (
	"log"

	"bigworld/common"
)

// sceneMgr owns every scene hosted by the world process. It loads the world's
// terrain config once and hands it to each scene it creates. Callers hold
// worldServer.mu while invoking its methods.
type sceneMgr struct {
	scenes      map[string]*scene
	voxelGrid   *voxelGrid
	displacement *displacementTable
	maxStep     float64
	spawnX      float64
	spawnZ      float64
	move        moveParams
}

type moveParams struct {
	sprintSpeed    float64
	rollSpeed      float64
	fallGravity    float64
	fallSpeedLimit float64
	playerHeight   float64
	playerCenterY  float64
}

func newSceneMgr(id string) *sceneMgr {
	cfg := common.Config.Servers["world"]
	m := &sceneMgr{
		scenes: make(map[string]*scene),
		spawnX: cfg.SpawnX,
		spawnZ: cfg.SpawnZ,
	}

	// GameConfig is the single source of truth: load the shared player config
	// and state transition table once at startup. Movement params come purely
	// from player_config.json (derived in gameconfig.go); if it fails to load
	// we log loudly and fall back to builtin defaults below.
	if gc, err := loadPlayerConfig(cfg.PlayerConfigFile); err != nil {
		log.Printf("[world %s] WARN: player config %q failed to load: %v (using builtin defaults)",
			id, cfg.PlayerConfigFile, err)
	} else {
		m.maxStep = gc.maxStep
		m.move.sprintSpeed = gc.sprintSpeedMps
		m.move.rollSpeed = gc.rollSpeedMps
		m.move.fallGravity = gc.fallGravityMps2
		m.move.fallSpeedLimit = gc.fallSpeedLimitMps
		m.move.playerHeight = gc.playerHeight
		m.move.playerCenterY = gc.playerCenterY
		log.Printf("[world %s] shared player config loaded: maxStep=%.2f sprint=%.1fm/s roll=%.1fm/s gravity=%.1f fallLimit=%.1f h=%.2f cy=%.2f",
			id, m.maxStep, m.move.sprintSpeed, m.move.rollSpeed, m.move.fallGravity, m.move.fallSpeedLimit, m.move.playerHeight, m.move.playerCenterY)
	}
	if tt, err := loadStateTransitionTable(cfg.StateTransitionTableFile); err != nil {
		log.Printf("[world %s] WARN: state transition table %q failed to load: %v (transition validation disabled)",
			id, cfg.StateTransitionTableFile, err)
	} else {
		moveTransitions = tt
		log.Printf("[world %s] state transition table loaded: %d states", id, len(tt))
	}

	if m.maxStep <= 0 {
		m.maxStep = 0.5
	}
	if m.move.sprintSpeed <= 0 {
		m.move.sprintSpeed = 8.5
	}
	if m.move.rollSpeed <= 0 {
		m.move.rollSpeed = 5.0
	}
	if m.move.fallGravity <= 0 {
		m.move.fallGravity = 10.0
	}
	if m.move.fallSpeedLimit <= 0 {
		m.move.fallSpeedLimit = 15.0
	}
	if m.move.playerHeight <= 0 {
		m.move.playerHeight = 1.8
	}
	if m.move.playerCenterY <= 0 {
		m.move.playerCenterY = 0.9
	}
	if cfg.VoxelFile != "" {
		g, err := loadVoxelGrid(cfg.VoxelFile)
		if err != nil {
			log.Printf("[world %s] WARN: voxel grid %q failed to load: %v (movement validation disabled)", id, cfg.VoxelFile, err)
		} else {
			m.voxelGrid = g
			g.debugDump()
		}
	}
	if cfg.DisplacementFile != "" {
		t, err := loadDisplacementTable(cfg.DisplacementFile)
		if err != nil {
			log.Printf("[world %s] WARN: displacement curves %q failed to load: %v (per-state move caps disabled)", id, cfg.DisplacementFile, err)
		} else {
			m.displacement = t
			t.debugDump()
		}
	}
	return m
}

func (m *sceneMgr) GetOrCreate(sceneId string) *scene {
	if sc, ok := m.scenes[sceneId]; ok {
		return sc
	}
	sw, sh := m.bounds()
	sc := newScene(sceneId, sw, sh, m.voxelGrid, m.displacement, m.maxStep, m.spawnX, m.spawnZ,
		m.move.sprintSpeed, m.move.rollSpeed, m.move.fallGravity, m.move.fallSpeedLimit,
		m.move.playerHeight, m.move.playerCenterY)
	m.scenes[sceneId] = sc
	return sc
}

func (m *sceneMgr) bounds() (float64, float64) {
	if m.voxelGrid != nil {
		return m.voxelGrid.width, m.voxelGrid.height
	}
	return 1280, 720
}
