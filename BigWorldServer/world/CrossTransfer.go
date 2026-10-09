package main

import (
	"bigworld/world/Config"
	"fmt"
	"log"
	"time"

	"bigworld/common"
)

const (
	crossTransferReportTimeout = 3500 * time.Millisecond
	crossTransferGiveUpTimeout = 8 * time.Second
)

type outboundTransfer struct {
	TransferID  string
	ReqID       uint64
	PlayerID    uint64
	Account     string
	GatewayID   string
	SessionID   string
	FromSceneID config.SceneID
	ToWorldID   string
	ToSceneID   config.SceneID
	ToX         float64
	ToZ         float64
	Seamless    bool
	OldX        float64
	OldY        float64
	OldZ        float64
	OldVoxelK   int
	OldScene    *scene
	Snap        entitySnapshot
	Inputs      []moveInput
	InputSeq    uint64
	Deadline    time.Time
	GiveUpAt    time.Time
	AbortSent   bool
}

type incomingTransfer struct {
	TransferID  string
	PlayerID    uint64
	Account     string
	GatewayID   string
	SessionID   string
	FromWorldID string
	ToSceneID   config.SceneID
	ToX         float64
	ToZ         float64
	Seamless    bool
	Deadline    time.Time
	GiveUpAt    time.Time
	AbortSent   bool
}

func (ss *worldServer) BeginCrossWorldTransfer(e *playerEntity, toSceneID config.SceneID, toX, toZ float64, seamless bool, now time.Time) bool {
	if e == nil || e.Scene == nil || !e.Phase.CanStartTravel() || !toSceneID.Valid() {
		return false
	}
	if _, ok := config.SceneConfig().Get(toSceneID); !ok {
		log.Printf("[world %s] cross transfer target scene %d(%s) is not configured", ss.ServerID, int64(toSceneID), config.SceneConfig().NameOf(toSceneID))
		return false
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
		ToSceneID:   toSceneID,
		ToX:         toX,
		ToZ:         toZ,
		Seamless:    seamless,
		OldX:        e.X,
		OldY:        e.Y,
		OldZ:        e.Z,
		OldVoxelK:   e.VoxelK,
		OldScene:    e.Scene,
		Deadline:    now.Add(crossTransferReportTimeout),
		GiveUpAt:    now.Add(crossTransferGiveUpTimeout),
	}
	ss.outboundTransfers[transferID] = ot

	if seamless {
		ss.applyPhase(e, PhaseZoneHandoff)
	} else {
		ss.applyPhase(e, PhaseWorldHandoff)
	}
	e.TransferID = transferID

	req := &common.TransferPrepareReq{
		ReqId:       reqID,
		Account:     e.Account,
		PlayerId:    e.PlayerID,
		GatewayId:   e.GatewayID,
		SessionId:   e.SessionID,
		FromSceneId: int64(e.Scene.SceneID),
		ToSceneId:   int64(toSceneID),
		ToX:         toX,
		ToZ:         toZ,
		Seamless:    seamless,
	}
	if err := ss.SendToCentralMsg(common.Wd2Ct_TransferPrepareReq, req); err != nil {
		log.Printf("[world %s] cross transfer prepare send failed for player %d: %v", ss.ServerID, e.PlayerID, err)
		ss.AbortOutbound(ot, "prepare send failed")
		return false
	}
	log.Printf("[world %s] cross transfer prepare player=%d %s -> %s seamless=%v", ss.ServerID, e.PlayerID, config.SceneConfig().NameOf(ot.FromSceneID), config.SceneConfig().NameOf(ot.ToSceneID), seamless)
	return true
}

func (ss *worldServer) CompleteLocalOutbound(ot *outboundTransfer) {
	e := ss.players[ot.PlayerID]
	if e == nil || e.TransferID != ot.TransferID {
		delete(ss.outboundTransfers, ot.TransferID)
		return
	}
	ss.applyPhase(e, PhaseActive)
	if ot.Seamless {
		if !ss.TransferEntityZoneLocal(e, ot.ToSceneID, time.Now()) {
			ss.AbortOutbound(ot, "local transfer failed")
			return
		}
	} else if !ss.TransferEntityToScene(e, ot.ToSceneID, ot.ToX, ot.ToZ, time.Now()) {
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

func fillTimelineReq(req *common.TransferEntityReq, e *playerEntity) {
	snap := CaptureEntitySnapshot(e)
	req.Y = snap.Y
	req.VoxelK = int32(snap.VoxelK)
	req.Airborne = snap.Airborne
	req.State = snap.State
	req.MoveDirX = snap.MoveDirX
	req.MoveDirZ = snap.MoveDirZ
	req.CurveNorm = snap.CurveNorm
	req.StateStartMs = snap.StateStartMs
	req.FallVelY = snap.FallVelY
	req.SimTick = snap.Tick
	req.InputSeq = e.InputSeq
	req.Inputs = make([]*common.MoveInputPb, 0, len(e.Inputs))
	for _, in := range e.Inputs {
		req.Inputs = append(req.Inputs, &common.MoveInputPb{
			Seq:   in.Seq,
			Tick:  in.Tick,
			Kind:  uint32(in.Kind),
			State: in.State,
			DirX:  float32(in.DirX),
			DirZ:  float32(in.DirZ),
		})
	}
}

func applyTimelineEntity(e *playerEntity, req *common.TransferEntityReq) {
	e.Y = req.Y
	e.VoxelK = int(req.VoxelK)
	e.Airborne = req.Airborne
	e.State = req.State
	e.MoveDirX = req.MoveDirX
	e.MoveDirZ = req.MoveDirZ
	e.CurveNorm = req.CurveNorm
	e.StateStartMs = req.StateStartMs
	e.FallVelY = req.FallVelY
	e.SimTick = req.SimTick
	e.InputSeq = req.InputSeq
	if req.SimTick > 0 {
		e.Snapshots = []entitySnapshot{CaptureEntitySnapshot(e)}
	}
	e.Inputs = nil
	for _, in := range req.Inputs {
		if in == nil {
			continue
		}
		e.Inputs = append(e.Inputs, moveInput{
			Seq:   in.Seq,
			Tick:  in.Tick,
			Kind:  moveInputKind(in.Kind),
			State: in.State,
			DirX:  float64(in.DirX),
			DirZ:  float64(in.DirZ),
		})
	}
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
	ot.ToSceneID = config.SceneID(rsp.ToSceneId)
	ot.ToX = rsp.ToX
	ot.ToZ = rsp.ToZ
	ot.Seamless = rsp.Seamless
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
	if ot.Seamless {
		ot.ToX, ot.ToZ = e.X, e.Z
	}
	ot.Snap = CaptureEntitySnapshot(e)
	ot.Inputs = append([]moveInput(nil), e.Inputs...)
	ot.InputSeq = e.InputSeq
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
		FromSceneId: int64(ot.FromSceneID),
		ToSceneId:   int64(ot.ToSceneID),
		X:           ot.ToX,
		Z:           ot.ToZ,
		Seamless:    ot.Seamless,
	}
	fillTimelineReq(req, e)
	if err := ss.peerMgr.SendWorld(ot.ToWorldID, common.Wd2Wd_TransferEntityReq, req); err != nil {
		log.Printf("[world %s] cross transfer entity send failed player=%d world=%s: %v", ss.ServerID, ot.PlayerID, ot.ToWorldID, err)
		ss.SendAbortToCentral(ot.TransferID, "entity send failed")
		ss.AbortOutbound(ot, "entity send failed")
		return
	}
	log.Printf("[world %s] cross transfer entity sent player=%d to %s scene=%s", ss.ServerID, ot.PlayerID, ot.ToWorldID, config.SceneConfig().NameOf(ot.ToSceneID))
}

func (ss *worldServer) HandlePeerTransferEntityRsp(_ *common.ConnWrapper, rsp *common.TransferEntityRsp) {
	ot := ss.outboundTransfers[rsp.TransferId]
	if ot == nil {
		return
	}
	if !rsp.Success {
		log.Printf("[world %s] cross transfer target rejected player=%d: %s", ss.ServerID, ot.PlayerID, rsp.Message)
		ss.SendAbortToCentral(ot.TransferID, rsp.Message)
		return
	}
	log.Printf("[world %s] cross transfer target ready player=%d transfer=%s", ss.ServerID, ot.PlayerID, rsp.TransferId)
}

func (ss *worldServer) HandleCentralTransferIncomingNotify(_ *common.ConnWrapper, notify *common.TransferIncomingNotify) {
	ack := func(success bool, message string) {
		rsp := &common.TransferIncomingAck{TransferId: notify.TransferId, Success: success, Message: message}
		if err := ss.SendToCentralMsg(common.Wd2Ct_TransferIncomingAck, rsp); err != nil {
			log.Printf("[world %s] transfer incoming ack send failed: %v", ss.ServerID, err)
		}
	}
	log.Printf("[world %s] transfer incoming notify transfer=%s player=%d scene=%d", ss.ServerID, notify.TransferId, notify.PlayerId, notify.ToSceneId)
	if notify.TransferId == "" || notify.Account == "" {
		ack(false, "invalid transfer")
		return
	}
	if _, ok := config.SceneConfig().Get(config.SceneID(notify.ToSceneId)); !ok {
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
			ToSceneID:   config.SceneID(notify.ToSceneId),
			ToX:         notify.ToX,
			ToZ:         notify.ToZ,
			Seamless:    notify.Seamless,
			Deadline:    time.Now().Add(crossTransferReportTimeout),
			GiveUpAt:    time.Now().Add(crossTransferGiveUpTimeout),
		}
	}
	ack(true, "reserved")
}

func (ss *worldServer) HandlePeerTransferEntityReq(conn *common.ConnWrapper, req *common.TransferEntityReq) {
	log.Printf("[world %s] transfer peer entity req transfer=%s player=%d account=%s scene=%s seamless=%v", ss.ServerID, req.TransferId, req.PlayerId, req.Account, config.SceneConfig().NameOf(config.SceneID(req.ToSceneId)), req.Seamless)
	rsp := &common.TransferEntityRsp{TransferId: req.TransferId}
	it := ss.incomingTransfers[req.TransferId]
	if it == nil {
		rsp.Message = "transfer not reserved"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	if _, ok := config.SceneConfig().Get(config.SceneID(req.ToSceneId)); !ok {
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
	target := ss.sceneMgr.GetOrCreate(config.SceneID(req.ToSceneId))
	if target == nil {
		rsp.Message = "scene unavailable"
		_ = common.SendMsg(conn, common.Wd2Wd_TransferEntityRsp, rsp)
		return
	}
	x, z := req.X, req.Z
	k := int(req.VoxelK)
	if req.SimTick <= 0 || k < 0 {
		x, z, k = target.SpawnPosition(true, req.X, req.Z)
	} else if cx, cz, ok := target.voxelWorldColumn(x, z); ok && k >= target.voxelColumnCount(cx, cz) {
		k = target.voxelTopLayer(cx, cz)
	}
	phase := PhaseArrivingFrozen
	if req.Seamless {
		phase = PhaseArriving
	}
	e := &playerEntity{
		PlayerID:    req.PlayerId,
		Account:     req.Account,
		GatewayID:   req.GatewayId,
		SessionID:   req.SessionId,
		GatewayConn: gatewayConn,
		X:           x,
		Z:           z,
		VoxelK:      k,
		Scene:       target,
		Phase:       phase,
		TransferID:  req.TransferId,
	}
	applyTimelineEntity(e, req)
	if !req.Seamless {
		if cx, cz, ok := target.voxelWorldColumn(x, z); ok {
			if top := target.voxelTopLayer(cx, cz); top >= 0 {
				e.VoxelK = top
			}
		}
		if target.voxelGrid != nil {
			e.Y = target.voxelGrid.SurfaceHeight(x, z, e.VoxelK)
		}
		ss.resetMovement(e)
	}
	if target.voxelGrid != nil && e.Y == 0 {
		e.Y = target.voxelGrid.SurfaceHeight(x, z, k)
	}
	if e.SimTick <= 0 {
		ss.InitEntityTimeline(e)
	}
	ss.RemoveForeign(req.PlayerId)
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
	log.Printf("[world %s] cross transfer incoming player=%d ready scene=%s", ss.ServerID, req.PlayerId, target.SceneName)
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
		if ot.Seamless {
			ss.applyPhase(e, PhaseActive)
		} else {
			e.Scene = ot.OldScene
			e.X = ot.OldX
			e.Y = ot.OldY
			e.Z = ot.OldZ
			e.VoxelK = ot.OldVoxelK
			ss.resetMovement(e)
			ss.applyPhase(e, PhaseActive)
			if e.Scene != nil && e.Scene.Players[e.PlayerID] != e {
				e.Scene.Players[e.PlayerID] = e
			}
			ss.SavePlayer(e)
		}
	}
	delete(ss.outboundTransfers, ot.TransferID)
	log.Printf("[world %s] cross transfer abort out player=%d: %s", ss.ServerID, ot.PlayerID, reason)
}

func (ss *worldServer) CommitIncoming(it *incomingTransfer) {
	if e := ss.players[it.PlayerID]; e != nil && e.TransferID == it.TransferID {
		ss.applyPhase(e, PhaseActive)
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
		SceneId:  int64(ot.ToSceneID),
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
		if now.After(ot.GiveUpAt) {
			ss.AbortOutbound(ot, "no verdict")
			continue
		}
		if now.After(ot.Deadline) && !ot.AbortSent {
			ot.AbortSent = true
			ss.SendAbortToCentral(ot.TransferID, "timeout")
		}
	}
	for _, it := range ss.incomingTransfers {
		if now.After(it.GiveUpAt) {
			ss.AbortIncoming(it)
			continue
		}
		if now.After(it.Deadline) && !it.AbortSent {
			it.AbortSent = true
			ss.SendAbortToCentral(it.TransferID, "timeout")
		}
	}
}
