package main

import (
	"bigworld/world/Config"
	"log"
	"time"

	"bigworld/common"
)

const teleportFreezeMs int64 = 5000
const zoneTransferCooldown = 500 * time.Millisecond
const zoneHysteresis = 0.25

type zoneTrigger struct {
	fromTemplate int
	toScene      config.SceneID
	toName       string
	box          common.SceneTriggerConfig
}

func zoneDepth(entry config.SceneEntry, x, z float64) float64 {
	depth := x - entry.OriginX
	if d := entry.OriginX + entry.ZoneSize - x; d < depth {
		depth = d
	}
	if d := z - entry.OriginZ; d < depth {
		depth = d
	}
	if d := entry.OriginZ + entry.ZoneSize - z; d < depth {
		depth = d
	}
	return depth
}

func resolveTriggers(triggers []common.SceneTriggerConfig) []zoneTrigger {
	var resolved []zoneTrigger
	for _, trigger := range triggers {
		from, ok := config.SceneConfig().GetByName(trigger.FromScene)
		if !ok {
			log.Printf("[world] WARN: trigger from_scene %q 不在 scene_config，忽略", trigger.FromScene)
			continue
		}
		to, ok := config.SceneConfig().GetByName(trigger.ToScene)
		if !ok {
			log.Printf("[world] WARN: trigger to_scene %q 不在 scene_config，忽略", trigger.ToScene)
			continue
		}
		resolved = append(resolved, zoneTrigger{
			fromTemplate: from.Template,
			toScene:      config.SceneIDFromMain(to.MainID, config.MainInstance),
			toName:       to.Name,
			box:          trigger,
		})
	}
	return resolved
}

func (ss *worldServer) CheckZoneBoundaries(now time.Time) {
	for _, e := range ss.players {
		if e == nil || e.Scene == nil || !e.Phase.CanStartTravel() {
			continue
		}
		if e.moveStateBlocksTravel() {
			continue
		}
		if !e.TransferCooldown.IsZero() && now.Before(e.TransferCooldown) {
			continue
		}
		targetID, ok := config.SceneConfig().ZoneAt(e.Scene.SceneID, e.X, e.Z)
		if !ok || targetID == e.Scene.SceneID {
			continue
		}
		if target, ok := config.SceneConfig().Get(targetID); ok && zoneDepth(target, e.X, e.Z) < zoneHysteresis {
			continue
		}
		ss.BeginCrossWorldTransfer(e, targetID, e.X, e.Z, true, now)
	}
}

func (ss *worldServer) CheckSceneTriggers(now time.Time) {
	for _, e := range ss.players {
		if e == nil || e.Scene == nil || !e.Phase.CanStartTravel() {
			continue
		}
		if !e.TransferCooldown.IsZero() && now.Before(e.TransferCooldown) {
			continue
		}
		for _, trigger := range ss.zoneTriggers {
			if e.Scene.SceneID.Template() != trigger.fromTemplate {
				continue
			}
			if e.X < trigger.box.MinX || e.X > trigger.box.MaxX || e.Z < trigger.box.MinZ || e.Z > trigger.box.MaxZ {
				continue
			}
			toX, toZ := triggerSpawn(trigger)
			if !ss.BeginCrossWorldTransfer(e, trigger.toScene, toX, toZ, false, now) {
				log.Printf("[world %s] scene trigger %s -> %s failed for player %d", ss.ServerID, trigger.box.FromScene, trigger.toName, e.PlayerID)
			}
			break
		}
	}
}

func triggerSpawn(trigger zoneTrigger) (float64, float64) {
	entry, ok := config.SceneConfig().GetByName(trigger.toName)
	if !ok {
		return 0, 0
	}
	toX, toZ := entry.SpawnX, entry.SpawnZ
	if trigger.box.ToX != nil {
		toX = *trigger.box.ToX
	}
	if trigger.box.ToZ != nil {
		toZ = *trigger.box.ToZ
	}
	return toX, toZ
}

func (ss *worldServer) ClearExpiredTransfers(now time.Time) {
	for _, e := range ss.players {
		if e != nil && e.Phase == PhaseTeleportFrozen && now.UnixMilli() >= e.PhaseUntilMs {
			ss.applyPhase(e, PhaseActive)
		}
	}
}

func (ss *worldServer) TransferEntityZoneLocal(e *playerEntity, targetID config.SceneID, now time.Time) bool {
	if e == nil || e.Scene == nil || e.Scene.SceneID == targetID {
		return false
	}
	if _, ok := config.SceneConfig().Get(targetID); !ok {
		return false
	}
	target := ss.sceneMgr.GetOrCreate(targetID)
	if target == nil {
		return false
	}
	oldScene := e.Scene.SceneName
	delete(e.Scene.Players, e.PlayerID)
	e.Scene = target
	target.Players[e.PlayerID] = e
	if target.voxelGrid != nil && e.VoxelK >= target.voxelGrid.ColumnCountAtWorld(e.X, e.Z) {
		e.VoxelK = target.voxelGrid.TopLayerAtWorld(e.X, e.Z)
		e.Y = target.voxelGrid.SurfaceHeight(e.X, e.Z, e.VoxelK)
	}
	e.TransferCooldown = now.Add(zoneTransferCooldown)
	ss.SavePlayer(e)
	ss.SendEnterSceneNotify(e, target, true)
	log.Printf("[world %s] player %d zone %s -> %s at (%.2f, %.2f)", ss.ServerID, e.PlayerID, oldScene, target.SceneName, e.X, e.Z)
	return true
}

func (ss *worldServer) TransferEntityToScene(e *playerEntity, targetID config.SceneID, toX, toZ float64, now time.Time) bool {
	if e == nil || e.Scene == nil || !e.Phase.CanStartTravel() || e.Scene.SceneID == targetID {
		return false
	}
	if _, ok := config.SceneConfig().Get(targetID); !ok {
		log.Printf("[world %s] scene trigger target %d is not configured", ss.ServerID, int64(targetID))
		return false
	}
	target := ss.sceneMgr.GetOrCreate(targetID)
	if target == nil || target.voxelGrid == nil {
		log.Printf("[world %s] scene trigger target %q has no voxel grid", ss.ServerID, target.SceneName)
		return false
	}
	x, z, k := target.SpawnPosition(true, toX, toZ)
	if k < 0 {
		x, z, k = target.SpawnPosition(false, 0, 0)
	}
	oldScene := e.Scene.SceneName
	delete(e.Scene.Players, e.PlayerID)
	e.Scene = target
	e.X = x
	e.Z = z
	e.VoxelK = k
	e.Y = target.voxelGrid.SurfaceHeight(x, z, k)
	ss.beginTeleportFreeze(e, now.UnixMilli()+teleportFreezeMs)
	target.Players[e.PlayerID] = e
	ss.SavePlayer(e)
	ss.SendEnterSceneNotify(e, target, false)
	log.Printf("[world %s] player %d transfer %s -> %s at (%.2f, %.2f)", ss.ServerID, e.PlayerID, oldScene, target.SceneName, x, z)
	return true
}

func (ss *worldServer) SendEnterSceneNotify(e *playerEntity, target *scene, seamless bool) {
	if e.GatewayConn == nil {
		log.Printf("[world %s] player %d has no gateway connection for scene notify", ss.ServerID, e.PlayerID)
		return
	}
	notify := &common.EnterSceneNotify{
		WorldId:  ss.ServerID,
		PlayerId: e.PlayerID,
		X:        e.X,
		Z:        e.Z,
		Width:    target.Width,
		Height:   target.Height,
		SceneId:  int64(target.SceneID),
		Seamless: seamless,
	}
	if err := common.SendMsg(e.GatewayConn, common.Wd2Cli_EnterSceneNotify, notify); err != nil {
		log.Printf("[world %s] send EnterSceneNotify to player %d failed: %v", ss.ServerID, e.PlayerID, err)
	}
}
