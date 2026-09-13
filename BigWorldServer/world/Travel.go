package main

import (
	"log"
	"time"

	"bigworld/common"
	pb "bigworld/common/pb"
)

const teleportFreezeMs int64 = 5000

func defaultSceneTriggers() []common.SceneTriggerConfig {
	return []common.SceneTriggerConfig{
		{
			FromScene: "MainCity",
			ToScene:   "ColorfulTown",
			MinX:      7.5,
			MaxX:      9.5,
			MinZ:      7.5,
			MaxZ:      9.5,
		},
	}
}

func (ss *worldServer) CheckSceneTriggers(now time.Time) {
	for _, e := range ss.players {
		if e == nil || e.Scene == nil || e.Transferring {
			continue
		}
		for _, trigger := range ss.sceneTriggers {
			if e.Scene.SceneId != trigger.FromScene {
				continue
			}
			if e.X < trigger.MinX || e.X > trigger.MaxX || e.Z < trigger.MinZ || e.Z > trigger.MaxZ {
				continue
			}
			if !ss.TransferEntityToScene(e, trigger.ToScene, now) {
				log.Printf("[world %s] scene trigger %s -> %s failed for player %d", ss.ServerId, trigger.FromScene, trigger.ToScene, e.PlayerId)
			}
			break
		}
	}
}

func (ss *worldServer) ClearExpiredTransfers(now time.Time) {
	for _, e := range ss.players {
		if e != nil && e.Transferring && now.UnixMilli() >= e.TransferUntilMs {
			e.Transferring = false
		}
	}
}

func (ss *worldServer) TransferEntityToScene(e *playerEntity, targetSceneID string, now time.Time) bool {
	if e == nil || e.Scene == nil || e.Transferring || e.Scene.SceneId == targetSceneID {
		return false
	}
	if _, ok := SceneConfig().Get(targetSceneID); !ok {
		log.Printf("[world %s] scene trigger target %q is not configured", ss.ServerId, targetSceneID)
		return false
	}
	target := ss.sceneMgr.GetOrCreate(targetSceneID)
	if target == nil || target.voxelGrid == nil {
		log.Printf("[world %s] scene trigger target %q has no voxel grid", ss.ServerId, targetSceneID)
		return false
	}
	x, z, k := target.SpawnPosition(false, 0, 0)
	oldSceneID := e.Scene.SceneId
	delete(e.Scene.Players, e.PlayerId)
	e.Scene = target
	e.X = x
	e.Z = z
	e.VoxelK = k
	e.Y = target.voxelGrid.SurfaceHeight(x, z, k)
	e.Airborne = false
	e.State = pb.MoveState_MOVE_IDLE
	e.MoveDirX = 0
	e.MoveDirZ = 0
	e.CurveNorm = 0
	e.FallVelY = 0
	e.Inputs = nil
	e.InputSeq = 0
	ss.InitEntityTimeline(e)
	e.Transferring = true
	e.TransferUntilMs = now.UnixMilli() + teleportFreezeMs
	target.Players[e.PlayerId] = e
	ss.SavePlayer(e)
	ss.SendEnterSceneNotify(e, target)
	log.Printf("[world %s] player %d transfer %s -> %s at (%.2f, %.2f)", ss.ServerId, e.PlayerId, oldSceneID, target.SceneId, x, z)
	return true
}

func (ss *worldServer) SendEnterSceneNotify(e *playerEntity, target *scene) {
	if e.GatewayConn == nil {
		log.Printf("[world %s] player %d has no gateway connection for scene notify", ss.ServerId, e.PlayerId)
		return
	}
	notify := &common.EnterSceneNotify{
		WorldId:  ss.ServerId,
		PlayerId: e.PlayerId,
		X:        e.X,
		Z:        e.Z,
		Width:    target.Width,
		Height:   target.Height,
		SceneId:  target.SceneId,
	}
	if err := common.SendMsg(e.GatewayConn, common.Wd2Cli_EnterSceneNotify, notify); err != nil {
		log.Printf("[world %s] send EnterSceneNotify to player %d failed: %v", ss.ServerId, e.PlayerId, err)
	}
}
