package main

import (
	"fmt"
	"log"
	"time"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

const (
	transferIncomingTimeout = 1500 * time.Millisecond
	transferReadyTimeout    = 1500 * time.Millisecond
)

type transferRecord struct {
	ID          string
	ReqID       uint64
	Account     string
	PlayerID    uint64
	GatewayID   string
	SessionID   string
	FromWorldID string
	ToWorldID   string
	FromSceneID int64
	ToSceneID   int64
	ToX         float64
	ToZ         float64
	Seamless    bool
	Width       float64
	Height      float64
	Stage       int
	Deadline    time.Time
}

const (
	transferStageIncomingAck = iota
	transferStageReady
)

func (cs *centralServer) HandleTransferPrepareReq(conn *common.ConnWrapper, req *common.TransferPrepareReq) {
	sender := cs.connToID[conn]
	fail := func(message string) {
		rsp := &common.TransferPrepareRsp{ReqId: req.ReqId, Success: false, Message: message}
		if sender != "" {
			cs.SendToWorldMsg(sender, common.Ct2Wd_TransferPrepareRsp, rsp)
		} else {
			_ = common.SendMsg(conn, common.Ct2Wd_TransferPrepareRsp, rsp)
		}
	}
	if sender == "" {
		return
	}
	if req.Account == "" || req.PlayerId == 0 || req.ToSceneId == 0 {
		fail("invalid transfer request")
		return
	}
	state, ok := cs.accountStates[req.Account]
	if !ok || state.Status != AccountOnline {
		fail("account not online")
		return
	}
	if state.WorldID != sender || state.PlayerID != req.PlayerId {
		fail("stale owner")
		return
	}
	if req.SessionId != "" && state.SessionID != "" && req.SessionId != state.SessionID {
		fail("session mismatch")
		return
	}
	if _, busy := cs.transferByPlayer[req.PlayerId]; busy {
		fail("transfer already in progress")
		return
	}
	if _, busy := cs.transferByReq[req.ReqId]; busy {
		fail("duplicate request")
		return
	}
	targetWorldID, _, local, ok := cs.PickZoneWorld(req.ToSceneId, sender)
	if !ok || targetWorldID == "" || local {
		rsp := &common.TransferPrepareRsp{
			ReqId:     req.ReqId,
			Success:   true,
			ToWorldId: sender,
			ToSceneId: req.ToSceneId,
			ToX:       req.ToX,
			ToZ:       req.ToZ,
			Seamless:  req.Seamless,
		}
		_ = cs.SendToWorldMsg(sender, common.Ct2Wd_TransferPrepareRsp, rsp)
		log.Printf("[central] transfer req=%d scene=%s stays on world %s", req.ReqId, req.ToSceneId, sender)
		return
	}
	targetRec, ok := cs.servers[targetWorldID]
	if !ok || targetRec.info.ServerType != common.ServerWorld || targetRec.conn == nil {
		fail("target world unavailable")
		return
	}
	cs.transferSeq++
	transferID := fmt.Sprintf("%s-%d-%d", cs.ServerID, cs.transferSeq, time.Now().UnixNano())
	rec := &transferRecord{
		ID:          transferID,
		ReqID:       req.ReqId,
		Account:     req.Account,
		PlayerID:    req.PlayerId,
		GatewayID:   state.GatewayID,
		SessionID:   state.SessionID,
		FromWorldID: sender,
		ToWorldID:   targetWorldID,
		FromSceneID: req.FromSceneId,
		ToSceneID:   req.ToSceneId,
		ToX:         req.ToX,
		ToZ:         req.ToZ,
		Seamless:    req.Seamless,
		Stage:       transferStageIncomingAck,
		Deadline:    time.Now().Add(transferIncomingTimeout),
	}
	cs.transfers[transferID] = rec
	cs.transferByReq[req.ReqId] = transferID
	cs.transferByPlayer[req.PlayerId] = transferID
	state.Status = AccountTransferring
	state.TransferID = transferID

	notify := &common.TransferIncomingNotify{
		TransferId:  transferID,
		PlayerId:    req.PlayerId,
		Account:     req.Account,
		GatewayId:   state.GatewayID,
		SessionId:   state.SessionID,
		FromWorldId: sender,
		FromSceneId: req.FromSceneId,
		ToSceneId:   req.ToSceneId,
		ToX:         req.ToX,
		ToZ:         req.ToZ,
		Seamless:    req.Seamless,
	}
	if err := cs.SendToWorldMsg(targetWorldID, common.Ct2Wd_TransferIncomingNotify, notify); err != nil {
		log.Printf("[central] transfer %s incoming notify send failed: %v", transferID, err)
		cs.FinishTransfer(rec, false, "target unavailable")
		fail("target unavailable")
		return
	}
	log.Printf("[central] transfer %s prepared player=%d %s -> %s scene=%s", transferID, req.PlayerId, sender, targetWorldID, req.ToSceneId)
}

func (cs *centralServer) HandleTransferIncomingAck(conn *common.ConnWrapper, ack *common.TransferIncomingAck) {
	sender := cs.connToID[conn]
	rec := cs.transfers[ack.TransferId]
	if rec == nil || sender != rec.ToWorldID {
		return
	}
	if !ack.Success {
		cs.SendTransferPrepareFailure(rec, ack.Message)
		cs.FinishTransfer(rec, false, ack.Message)
		return
	}
	rec.Stage = transferStageReady
	rec.Deadline = time.Now().Add(transferReadyTimeout)
	worldRec, ok := cs.servers[rec.ToWorldID]
	if !ok {
		cs.SendTransferPrepareFailure(rec, "target world unavailable")
		cs.FinishTransfer(rec, false, "target world unavailable")
		return
	}
	rsp := &common.TransferPrepareRsp{
		ReqId:       rec.ReqID,
		Success:     true,
		TransferId:  rec.ID,
		ToWorldId:   rec.ToWorldID,
		ToWorldAddr: worldRec.info.ListenAddr,
		ToSceneId:   rec.ToSceneID,
		ToX:         rec.ToX,
		ToZ:         rec.ToZ,
		Seamless:    rec.Seamless,
	}
	_ = cs.SendToWorldMsg(rec.FromWorldID, common.Ct2Wd_TransferPrepareRsp, rsp)
	log.Printf("[central] transfer %s authorized %s -> %s", rec.ID, rec.FromWorldID, rec.ToWorldID)
}

func (cs *centralServer) HandleTransferReadyReq(conn *common.ConnWrapper, req *common.TransferReadyReq) {
	sender := cs.connToID[conn]
	rec := cs.transfers[req.TransferId]
	if rec == nil || sender != rec.ToWorldID {
		return
	}
	if !req.Success {
		cs.FinishTransfer(rec, false, req.Message)
		return
	}
	rec.ToX = req.X
	rec.ToZ = req.Z
	rec.Width = req.Width
	rec.Height = req.Height
	cs.FinishTransfer(rec, true, "ready")
}

func (cs *centralServer) HandleTransferAbortReq(conn *common.ConnWrapper, req *common.TransferAbortReq) {
	sender := cs.connToID[conn]
	rec := cs.transfers[req.TransferId]
	if rec == nil || (sender != rec.FromWorldID && sender != rec.ToWorldID) {
		return
	}
	cs.FinishTransfer(rec, false, req.Reason)
}

func (cs *centralServer) SendTransferPrepareFailure(rec *transferRecord, message string) {
	rsp := &common.TransferPrepareRsp{ReqId: rec.ReqID, Success: false, Message: message}
	_ = cs.SendToWorldMsg(rec.FromWorldID, common.Ct2Wd_TransferPrepareRsp, rsp)
}

func (cs *centralServer) SendToWorldMsg(worldID string, msgType common.MessageType, msg proto.Message) error {
	rec, ok := cs.servers[worldID]
	if !ok || rec.conn == nil {
		return fmt.Errorf("world %s not connected", worldID)
	}
	data, err := common.MarshalHelper(msg)
	if err != nil {
		return err
	}
	return rec.conn.Send(common.Message{Type: msgType, Data: data})
}

func (cs *centralServer) FinishTransfer(rec *transferRecord, success bool, reason string) {
	if rec == nil {
		return
	}
	if success {
		if player, ok := cs.onlinePlayers[rec.PlayerID]; ok {
			player.WorldID = rec.ToWorldID
			player.SceneID = rec.ToSceneID
		}
		if state, ok := cs.accountStates[rec.Account]; ok && state.PlayerID == rec.PlayerID {
			state.WorldID = rec.ToWorldID
			state.SceneID = rec.ToSceneID
			state.Status = AccountOnline
			state.TransferID = ""
		}
	}
	finish := &common.TransferFinishNotify{TransferId: rec.ID, Success: success}
	_ = cs.SendToWorldMsg(rec.FromWorldID, common.Ct2Wd_TransferFinishNotify, finish)
	_ = cs.SendToWorldMsg(rec.ToWorldID, common.Ct2Wd_TransferFinishNotify, finish)
	if success {
		commit := &common.TransferCommitNotify{
			TransferId: rec.ID,
			PlayerId:   rec.PlayerID,
			Account:    rec.Account,
			SessionId:  rec.SessionID,
			WorldId:    rec.ToWorldID,
			SceneId:    rec.ToSceneID,
			X:          rec.ToX,
			Z:          rec.ToZ,
			Width:      rec.Width,
			Height:     rec.Height,
			Seamless:   rec.Seamless,
		}
		if gwRec, ok := cs.servers[rec.GatewayID]; ok && gwRec.conn != nil {
			common.SendMsg(gwRec.conn, common.Ct2Gw_TransferCommitNotify, commit)
		} else {
			log.Printf("[central] transfer %s commit gateway %s missing", rec.ID, rec.GatewayID)
		}
	} else {
		if state, ok := cs.accountStates[rec.Account]; ok && state.PlayerID == rec.PlayerID {
			state.Status = AccountOnline
			state.TransferID = ""
		}
	}
	delete(cs.transfers, rec.ID)
	delete(cs.transferByReq, rec.ReqID)
	delete(cs.transferByPlayer, rec.PlayerID)
	log.Printf("[central] transfer %s finished success=%v reason=%s", rec.ID, success, reason)
}

func (cs *centralServer) CheckTransferTimeouts(now time.Time) {
	for _, rec := range cs.transfers {
		if now.After(rec.Deadline) {
			cs.FinishTransfer(rec, false, "timeout")
		}
	}
}
