package main

import (
	"fmt"
	"log"
	"time"

	"bigworld/common"
	pb "bigworld/common/pb"
)

const crossWorldTransferTimeout = 5 * time.Second

type outboundTransfer struct {
	TransferID  string
	ReqID       uint64
	PlayerID    uint64
	Account     string
	GatewayID   string
	SessionID   string
	FromSceneID string
	ToWorldID   string
	ToSceneID   string
	ToX         float64
	ToZ         float64
	OldX        float64
	OldY        float64
	OldZ        float64
	OldVoxelK   int
	OldScene    *scene
	Deadline    time.Time
}

type incomingTransfer struct {
	TransferID  string
	PlayerID    uint64
	Account     string
	GatewayID   string
	SessionID   string
	FromWorldID string
	ToSceneID   string
	ToX         float64
	ToZ         float64
	Deadline    time.Time
}

func (ss *worldServer) BeginCrossWorldTransfer(e *playerEntity, trigger common.SceneTriggerConfig, now time.Time) bool {
	if e == nil || e.Scene == nil || e.Transferring || trigger.ToScene == "" {
		return false
	}
	entry, ok := SceneConfig().Get(trigger.ToScene)
	if !ok {
		log.Printf("[world %s] cross transfer target scene %q is not configured", ss.ServerID, trigger.ToScene)
		return false
	}
	toX, toZ := entry.spawnX, entry.spawnZ
	if trigger.ToX != nil {
		toX = *trigger.ToX
	}
	if trigger.ToZ != nil {
		toZ = *trigger.ToZ
	}
	ss.transferReqSeq++
	reqID := ss.transferReqSeq
	transferID := fmt.Sprintf("%s-%d-%d", ss.ServerID, reqID, now.UnixNano())
	ot := &outboundTransfer{
		TransferID:  transferID,
		ReqID:       reqID,
		PlayerID:    e.PlayerID,
		Account:     e.Account,
		GatewayID:   e.GatewayID,
		SessionID:   e.SessionID,
		FromSceneID: e.Scene.SceneID,
		ToSceneID:   trigger.ToScene,
		ToX:         toX,
		ToZ:         toZ,
		OldX:        e.X,
		OldY:        e.Y,
		OldZ:        e.Z,
		OldVoxelK:   e.VoxelK,
		OldScene:    e.Scene,
		Deadline:    now.Add(crossWorldTransferTimeout),
	}
	ss.outboundTransfers[transferID] = ot

	e.Transferring = true
	e.CrossWorld = true
	e.TransferID = transferID
	e.TransferUntilMs = ot.Deadline.UnixMilli()

	req := &common.TransferPrepareReq{
		ReqId:       reqID,
		Account:     e.Account,
		PlayerId:    e.PlayerID,
		GatewayId:   e.GatewayID,
		SessionId:   e.SessionID,
		FromSceneId: e.Scene.SceneID,
		ToSceneId:   trigger.ToScene,
		ToX:         toX,
		ToZ:         toZ,
	}
	if err := ss.SendToCentralMsg(common.Wd2Ct_TransferPrepareReq, req); err != nil {
		log.Printf("[world %s] cross transfer prepare send failed for player %d: %v", ss.ServerID, e.PlayerID, err)
		ss.AbortOutbound(ot, "prepare send failed")
		return false
	}
	log.Printf("[world %s] cross transfer prepare player=%d %s -> %s world pending", ss.ServerID, e.PlayerID, ot.FromSceneID, ot.ToSceneID)
	return true
}

func (ss *worldServer) CompleteLocalOutbound(ot *outboundTransfer) {
	e := ss.players[ot.PlayerID]
	if e == nil || e.TransferID != ot.TransferID {
		delete(ss.outboundTransfers, ot.TransferID)
		return
	}
	e.Transferring = false
	if !ss.TransferEntityToScene(e, ot.ToSceneID, time.Now()) {
		ss.AbortOutbound(ot, "local transfer failed")
		return
	}
	delete(ss.outboundTransfers, ot.TransferID)
}

func (ss *worldServer) OutboundByReqID(reqID uint64) *outboundTransfer {
	for _, ot := range ss.outboundTransfers {
		if ot.ReqID == reqID {
			return ot
		}
	}
	return nil
}

func (ss *worldServer) HandleCentralTransferPrepareRsp(_ *common.ConnWrapper, rsp *common.TransferPrepareRsp) {
	log.Printf("[world %s] transfer prepare rsp req=%d success=%v transfer=%s", ss.ServerID, rsp.ReqId, rsp.Success, rsp.TransferId)
	ot := ss.OutboundByReqID(rsp.ReqId)
	if ot == nil {
		return
	}
	if !rsp.Success {
		log.Printf("[world %s] cross transfer prepare rejected player=%d: %s", ss.ServerID, ot.PlayerID, rsp.Message)
		ss.AbortOutbound(ot, rsp.Message)
		return
	}
	if rsp.ToWorldId == ss.ServerID {
		ss.CompleteLocalOutbound(ot)
		return
	}
	ot.ToWorldID = rsp.ToWorldId
	ot.ToSceneID = rsp.ToSceneId
	ot.ToX = rsp.ToX
	ot.ToZ = rsp.ToZ
	if rsp.TransferId != "" && rsp.TransferId != ot.TransferID {
		oldID := ot.TransferID
		delete(ss.outboundTransfers, oldID)
		ot.TransferID = rsp.TransferId
		ss.outboundTransfers[ot.TransferID] = ot
		if cur := ss.players[ot.PlayerID]; cur != nil && cur.TransferID == oldID {
			cur.TransferID = ot.TransferID
		}
	}

	e := ss.players[ot.PlayerID]
	if e == nil || e.TransferID != ot.TransferID {
		ss.SendAbortToCentral(ot.TransferID, "entity missing")
		delete(ss.outboundTransfers, ot.TransferID)
		return
	}
	ss.SavePlayerAt(ot)
	ss.peerMgr.AddRoute(ot.ToWorldID, rsp.ToWorldAddr)
	ss.peerMgr.Ensure(ot.ToWorldID)

	req := &common.TransferEntityReq{
		TransferId:  ot.TransferID,
		PlayerId:    ot.PlayerID,
		Account:     ot.Account,
		GatewayId:   ot.GatewayID,
		SessionId:   ot.SessionID,
		FromWorldId: ss.ServerID,
		FromSceneId: ot.FromSceneID,
		ToSceneId:   ot.ToSceneID,
		X:           ot.ToX,
		Z:           ot.ToZ,
	}
	if err := ss.peerMgr.SendWorld(ot.ToWorldID, common.Wd2Wd_TransferEntityReq, req); err != nil {
		log.Printf("[world %s] cross transfer entity send failed player=%d world=%s: %v", ss.ServerID, ot.PlayerID, ot.ToWorldID, err)
		ss.SendAbortToCentral(ot.TransferID, "entity send failed")
		ss.AbortOutbound(ot, "entity send failed")
		return
	}
	log.Printf("[world %s] cross transfer entity sent player=%d to %s scene=%s", ss.ServerID, ot.PlayerID, ot.ToWorldID, ot.ToSceneID)
}

func (ss *worldServer) HandlePeerTransferEntityRsp(_ *common.ConnWrapper, rsp *common.TransferEntityRsp) {
	ot := ss.outboundTransfers[rsp.TransferId]
	if ot == nil {
		return
	}
	if !rsp.Success {
		log.Printf("[world %s] cross transfer target rejected player=%d: %s", ss.ServerID, ot.PlayerID, rsp.Message)
		ss.SendAbortToCentral(ot.TransferID, rsp.Message)
		ss.AbortOutbound(ot, rsp.Message)
		return
	}
	log.Printf("[world %s] cross transfer target ready player=%d transfer=%s", ss.ServerID, ot.PlayerID, ot.TransferID)
}

func (ss *worldServer) HandleCentralTransferIncomingNotify(_ *common.ConnWrapper, notify *common.TransferIncomingNotify) {
	ack := func(success bool, message string) {
		rsp := &common.TransferIncomingAck{TransferId: notify.TransferId, Success: success, Message: message}
		if err := ss.SendToCentralMsg(common.Wd2Ct_TransferIncomingAck, rsp); err != nil {
			log.Printf("[world %s] transfer incoming ack send failed: %v", ss.ServerID, err)
		}
	}
	log.Printf("[world %s] transfer incoming notify transfer=%s player=%d scene=%s", ss.ServerID, notify.TransferId, notify.PlayerId, notify.ToSceneId)
	if notify.TransferId == "" || notify.Account == "" {
		ack(false, "invalid transfer")
		return
	}
	if _, ok := SceneConfig().Get(notify.ToSceneId); !ok {
		ack(false, "scene not configured")
		return
	}
	if ss.gatewayConns[notify.GatewayId] == nil {
		ack(false, "gateway not connected")
		return
	}
	if _, exists := ss.incomingTransfers[notify.TransferId]; !exists {
		ss.incomingTransfers[notify.TransferId] = &incomingTransfer{
			TransferID:  notify.TransferId,
			PlayerID:    notify.PlayerId,
			Account:     notify.Account,
			GatewayID:   notify.GatewayId,
			SessionID:   notify.SessionId,
			FromWorldID: notify.FromWorldId,
			ToSceneID:   notify.ToSceneId,
			ToX:         notify.ToX,
			ToZ:         notify.ToZ,
			Deadline:    time.Now().Add(crossWorldTransferTimeout),
		}
	}
	ack(true, "reserved")
}

func (ss *worldServer) HandlePeerTransferEntityReq(conn *common.ConnWrapper, req *common.TransferEntityReq) {
	log.Printf("[world %s] transfer peer entity req transfer=%s player=%d account=%s scene=%s", ss.ServerID, req.TransferId, req.PlayerId, req.Account, req.ToSceneId)
	rsp := &common.TransferEntityRsp{TransferId: req.TransferId}
	it := ss.incomingTransfers[req.TransferId]
	if it == nil {
		rsp.Message = "transfer not reserved"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	if _, ok := SceneConfig().Get(req.ToSceneId); !ok {
		rsp.Message = "scene not configured"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	if ss.playersByAccount[req.Account] != nil {
		rsp.Message = "account already online"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	gatewayConn := ss.gatewayConns[req.GatewayId]
	if gatewayConn == nil {
		rsp.Message = "gateway not connected"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	target := ss.sceneMgr.GetOrCreate(req.ToSceneId)
	if target == nil {
		rsp.Message = "scene unavailable"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	x, z, k := target.SpawnPosition(true, req.X, req.Z)
	e := &playerEntity{
		PlayerID:         req.PlayerId,
		Account:          req.Account,
		GatewayID:        req.GatewayId,
		SessionID:        req.SessionId,
		GatewayConn:      gatewayConn,
		X:                x,
		Z:                z,
		VoxelK:           k,
		Scene:            target,
		Transferring:     true,
		CrossWorld:       true,
		IncomingTransfer: true,
		TransferID:       req.TransferId,
		TransferUntilMs:  it.Deadline.UnixMilli(),
	}
	if target.voxelGrid != nil {
		e.Y = target.voxelGrid.SurfaceHeight(x, z, k)
	}
	ss.InitEntityTimeline(e)
	ss.ReplacePlayerEntity(e)

	rsp.Success = true
	rsp.Message = "ready"
	rsp.X = x
	rsp.Z = z
	rsp.Width = target.Width
	rsp.Height = target.Height
	_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)

	ready := &common.TransferReadyReq{
		TransferId: req.TransferId,
		Success:    true,
		X:          x,
		Z:          z,
		Width:      target.Width,
		Height:     target.Height,
	}
	_ = ss.SendToCentralMsg(common.Wd2Ct_TransferReadyReq, ready)
	log.Printf("[world %s] cross transfer incoming player=%d ready scene=%s", ss.ServerID, req.PlayerId, req.ToSceneId)
}

func (ss *worldServer) HandleCentralTransferFinishNotify(_ *common.ConnWrapper, notify *common.TransferFinishNotify) {
	if notify.Success {
		if ot := ss.outboundTransfers[notify.TransferId]; ot != nil {
			ss.CommitOutbound(ot)
		}
		if it := ss.incomingTransfers[notify.TransferId]; it != nil {
			ss.CommitIncoming(it)
		}
		return
	}
	if ot := ss.outboundTransfers[notify.TransferId]; ot != nil {
		ss.AbortOutbound(ot, "central abort")
	}
	if it := ss.incomingTransfers[notify.TransferId]; it != nil {
		ss.AbortIncoming(it)
	}
}

func (ss *worldServer) CommitOutbound(ot *outboundTransfer) {
	if e := ss.players[ot.PlayerID]; e != nil && e.TransferID == ot.TransferID {
		ss.RemoveEntityLocal(e)
	}
	delete(ss.outboundTransfers, ot.TransferID)
	log.Printf("[world %s] cross transfer commit out player=%d -> %s", ss.ServerID, ot.PlayerID, ot.ToWorldID)
}

func (ss *worldServer) AbortOutbound(ot *outboundTransfer, reason string) {
	e := ss.players[ot.PlayerID]
	if e != nil && e.TransferID == ot.TransferID {
		e.TransferCooldown = time.Now().Add(2 * time.Second)
		e.Scene = ot.OldScene
		e.X = ot.OldX
		e.Y = ot.OldY
		e.Z = ot.OldZ
		e.VoxelK = ot.OldVoxelK
		e.Airborne = false
		e.State = pb.MoveState_MOVE_IDLE
		e.MoveDirX = 0
		e.MoveDirZ = 0
		e.CurveNorm = 0
		e.FallVelY = 0
		e.Transferring = false
		e.CrossWorld = false
		e.TransferID = ""
		e.TransferUntilMs = 0
		e.TransferStartTick = 0
		ss.InitEntityTimeline(e)
		if e.Scene != nil && e.Scene.Players[e.PlayerID] != e {
			e.Scene.Players[e.PlayerID] = e
		}
		ss.SavePlayer(e)
	}
	delete(ss.outboundTransfers, ot.TransferID)
	log.Printf("[world %s] cross transfer abort out player=%d: %s", ss.ServerID, ot.PlayerID, reason)
}

func (ss *worldServer) CommitIncoming(it *incomingTransfer) {
	if e := ss.players[it.PlayerID]; e != nil && e.TransferID == it.TransferID {
		e.Transferring = false
		e.CrossWorld = false
		e.IncomingTransfer = false
		e.TransferID = ""
		e.TransferUntilMs = 0
		e.TransferStartTick = 0
	}
	delete(ss.incomingTransfers, it.TransferID)
	log.Printf("[world %s] cross transfer commit in player=%d", ss.ServerID, it.PlayerID)
}

func (ss *worldServer) AbortIncoming(it *incomingTransfer) {
	if e := ss.players[it.PlayerID]; e != nil && e.TransferID == it.TransferID {
		ss.RemoveEntityLocal(e)
	}
	delete(ss.incomingTransfers, it.TransferID)
	log.Printf("[world %s] cross transfer abort in player=%d", ss.ServerID, it.PlayerID)
}

func (ss *worldServer) RemoveEntityLocal(e *playerEntity) {
	if e == nil {
		return
	}
	delete(ss.players, e.PlayerID)
	if e.Account != "" && ss.playersByAccount[e.Account] == e {
		delete(ss.playersByAccount, e.Account)
	}
	if e.Scene != nil {
		delete(e.Scene.Players, e.PlayerID)
	}
}

func (ss *worldServer) SavePlayerAt(ot *outboundTransfer) {
	req := &common.SavePlayerReq{Players: []*common.PlayerData{{
		PlayerId: ot.PlayerID,
		Account:  ot.Account,
		X:        ot.ToX,
		Z:        ot.ToZ,
		SceneId:  ot.ToSceneID,
	}}}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] save transfer arrival failed player=%d: %v", ss.ServerID, ot.PlayerID, err)
	}
}

func (ss *worldServer) SendAbortToCentral(transferID, reason string) {
	req := &common.TransferAbortReq{TransferId: transferID, Reason: reason}
	if err := ss.SendToCentralMsg(common.Wd2Ct_TransferAbortReq, req); err != nil {
		log.Printf("[world %s] transfer abort send failed transfer=%s: %v", ss.ServerID, transferID, err)
	}
}

func (ss *worldServer) CheckTransferTimeouts(now time.Time) {
	for _, ot := range ss.outboundTransfers {
		if now.After(ot.Deadline) {
			ss.SendAbortToCentral(ot.TransferID, "timeout")
			ss.AbortOutbound(ot, "timeout")
		}
	}
	for _, it := range ss.incomingTransfers {
		if now.After(it.Deadline) {
			ss.AbortIncoming(it)
		}
	}
}
