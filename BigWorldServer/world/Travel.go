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
		{
			FromScene: "ColorfulTown",
			ToScene:   "MainCity",
			MinX:      -2.0,
			MaxX:      2.0,
			MinZ:      -32.0,
			MaxZ:      -28.0,
		},
	}
}

func (ss *worldServer) CheckSceneTriggers(now time.Time) {
	for _, e := range ss.players {
		if e == nil || e.Scene == nil || e.Transferring {
			continue
		}
		if !e.TransferCooldown.IsZero() && now.Before(e.TransferCooldown) {
			continue
		}
		for _, trigger := range ss.sceneTriggers {
			if e.Scene.SceneID != trigger.FromScene {
				continue
			}
			if e.X < trigger.MinX || e.X > trigger.MaxX || e.Z < trigger.MinZ || e.Z > trigger.MaxZ {
				continue
			}
			var ok bool
			if ss.sceneMgr.Has(trigger.ToScene) {
				ok = ss.TransferEntityToScene(e, trigger.ToScene, now)
			} else {
				ok = ss.BeginCrossWorldTransfer(e, trigger, now)
			}
			if !ok {
				log.Printf("[world %s] scene trigger %s -> %s failed for player %d", ss.ServerID, trigger.FromScene, trigger.ToScene, e.PlayerID)
			}
			break
		}
	}
}

func (ss *worldServer) ClearExpiredTransfers(now time.Time) {
	for _, e := range ss.players {
		if e != nil && e.Transferring && !e.CrossWorld && now.UnixMilli() >= e.TransferUntilMs {
			e.Transferring = false
		}
	}
}

func (ss *worldServer) TransferEntityToScene(e *playerEntity, targetSceneID string, now time.Time) bool {
	if e == nil || e.Scene == nil || e.Transferring || e.Scene.SceneID == targetSceneID {
		return false
	}
	if _, ok := SceneConfig().Get(targetSceneID); !ok {
		log.Printf("[world %s] scene trigger target %q is not configured", ss.ServerID, targetSceneID)
		return false
	}
	target := ss.sceneMgr.GetOrCreate(targetSceneID)
	if target == nil || target.voxelGrid == nil {
		log.Printf("[world %s] scene trigger target %q has no voxel grid", ss.ServerID, targetSceneID)
		return false
	}
	x, z, k := target.SpawnPosition(false, 0, 0)
	oldSceneID := e.Scene.SceneID
	delete(e.Scene.Players, e.PlayerID)
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
	e.CrossWorld = false
	e.TransferID = ""
	e.TransferStartTick = ss.NowTick()
	e.TransferUntilMs = now.UnixMilli() + teleportFreezeMs
	target.Players[e.PlayerID] = e
	ss.SavePlayer(e)
	ss.SendEnterSceneNotify(e, target)
	log.Printf("[world %s] player %d transfer %s -> %s at (%.2f, %.2f)", ss.ServerID, e.PlayerID, oldSceneID, target.SceneID, x, z)
	return true
}

func (ss *worldServer) TryFinishTransfer(e *playerEntity, clientServerTimeMs int64) bool {
	if e == nil || !e.Transferring || e.CrossWorld || clientServerTimeMs <= 0 {
		return false
	}
	if clientServerTimeMs <= ss.TickToMs(e.TransferStartTick) {
		return false
	}
	e.Transferring = false
	return true
}

func (ss *worldServer) SendEnterSceneNotify(e *playerEntity, target *scene) {
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
		SceneId:  target.SceneID,
	}
	if err := common.SendMsg(e.GatewayConn, common.Wd2Cli_EnterSceneNotify, notify); err != nil {
		log.Printf("[world %s] send EnterSceneNotify to player %d failed: %v", ss.ServerID, e.PlayerID, err)
	}
}
