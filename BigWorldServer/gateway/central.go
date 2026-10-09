package main

import (
	"log"
	"time"

	"bigworld/common"
)

func (gs *gatewayServer) SendLogoutCleanupRaw(playerID uint64, sessionID string) {
	req := common.LogoutCleanupReq{
		PlayerId:  playerID,
		GatewayId: gs.ServerID,
		SessionId: sessionID,
	}
	gs.SendToCentralMsg(common.Gw2Ct_LogoutCleanupReq, &req)
}

func (gs *gatewayServer) FailLogoutSession(session *clientSession, message string) {
	if session == nil {
		return
	}
	conn := session.Conn
	gs.RemoveSession(session)
	if conn != nil {
		rsp := common.LogoutRsp{Success: false, Message: message}
		common.SendMsg(conn, common.Gw2Cli_LogoutRsp, &rsp)
		conn.CloseAfterSend()
	}
}

func (gs *gatewayServer) HandleCentralLoginPrepareRsp(_ *common.ConnWrapper, rsp *common.LoginPrepareRsp) {
	var pending *clientSession
	for _, session := range gs.byConn {
		if session.ReqID == rsp.ReqId && session.State == sessionPreparing {
			pending = session
			break
		}
	}
	if pending == nil {
		return
	}

	if !rsp.Success {
		gs.FailSession(pending, rsp.Message)
		return
	}

	log.Printf("[gateway] login prepared: account=%s world=%s session=%s",
		pending.Account, rsp.WorldId, pending.SessionID)

	pending.WorldID = rsp.WorldId
	pending.WorldAddr = rsp.WorldAddr
	pending.State = sessionCreating
	pending.Deadline = time.Now().Add(createEntityTimeout)

	createReq := common.CreateEntityReq{
		Account:   pending.Account,
		GatewayId: gs.ServerID,
		SessionId: pending.SessionID,
	}
	if err := gs.ForwardMsgToWorld(rsp.WorldId, common.Gw2Wd_CreateEntityReq, &createReq); err != nil {
		gs.SendLoginFinishFailure(pending.Account, pending.SessionID, "world unavailable")
		gs.FailSession(pending, "world unavailable")
	}
}

func (gs *gatewayServer) HandleCentralLoginFinishRsp(_ *common.ConnWrapper, rsp *common.LoginFinishRsp) {
	session := gs.GetByAccount(rsp.Account)
	if session == nil {
		return
	}
	if rsp.SessionId != "" && session.SessionID != "" && rsp.SessionId != session.SessionID {
		return
	}
	if session.State != sessionFinishing && session.State != sessionResuming {
		return
	}

	if !rsp.Success {
		if session.State == sessionResuming {
			gs.FailSession(session, rsp.Message)
			return
		}
		conn := session.Conn
		gs.DetachConn(session)
		if conn != nil {
			gs.FailClientLogin(conn, rsp.Message)
		}
		if session.PlayerID != 0 && session.WorldID != "" {
			owner := gs.byPlayer[session.PlayerID]
			if owner == nil || owner == session {
				gs.AttachPlayer(session)
				gs.EnterDestroy(session, session.WorldID)
				return
			}
		}
		gs.RemoveSession(session)
		return
	}

	if rsp.PlayerId != 0 {
		session.PlayerID = rsp.PlayerId
	}

	if session.State == sessionResuming {
		gs.AttachPlayer(session)
		session.State = sessionResumingWorld
		session.Deadline = time.Now().Add(createEntityTimeout)
		req := common.ResumeEntityReq{
			PlayerId:  session.PlayerID,
			GatewayId: gs.ServerID,
			SessionId: session.SessionID,
		}
		if err := gs.ForwardMsgToWorld(session.WorldID, common.Gw2Wd_ResumeEntityReq, &req); err != nil {
			gs.SendLogoutCleanup(session)
			gs.FailSession(session, "world unavailable")
		}
		return
	}

	snapshot := session.Login
	session.Login = nil
	if snapshot == nil {
		snapshot = &loginSnapshot{}
	}
	session.State = sessionActive
	session.LastHeartbeat = time.Now()
	session.Deadline = time.Time{}
	session.SceneID = snapshot.SceneID
	gs.AttachPlayer(session)

	loginRsp := common.LoginRsp{
		Success:   true,
		Message:   rsp.Message,
		WorldAddr: session.WorldAddr,
		WorldId:   session.WorldID,
		PlayerId:  session.PlayerID,
		X:         snapshot.X,
		Z:         snapshot.Z,
		Width:     snapshot.Width,
		Height:    snapshot.Height,
		SceneId:   snapshot.SceneID,
	}
	common.SendMsg(session.Conn, common.Gw2Cli_LoginRsp, &loginRsp)
}
func (gs *gatewayServer) HandleCentralForceKickNotify(_ *common.ConnWrapper, notify *common.ForceKickNotify) {
	log.Printf("[gateway] force-kick: player=%d account=%s session=%s world=%s",
		notify.PlayerId, notify.Account, notify.SessionId, notify.WorldId)

	session := gs.GetByPlayer(notify.PlayerId)
	if session == nil {
		session = gs.GetByAccount(notify.Account)
	}
	if session == nil {
		gs.SendLogoutCleanupRaw(notify.PlayerId, notify.SessionId)
		return
	}
	if notify.SessionId != "" && session.SessionID != "" && session.SessionID != notify.SessionId {
		log.Printf("[gateway] ignoring stale force-kick for player %d session=%s", notify.PlayerId, notify.SessionId)
		return
	}

	switch session.State {
	case sessionSuspect:
		gs.SendLogoutCleanup(session)
		gs.RemoveSession(session)
		return
	case sessionLoggingOut, sessionDestroying:
		return
	case sessionActive:
		conn := session.Conn
		gs.DetachConn(session)
		if conn != nil {
			rsp := common.LogoutRsp{Success: false, Message: "account logged in elsewhere"}
			common.SendMsg(conn, common.Gw2Cli_LogoutRsp, &rsp)
			conn.CloseAfterSend()
		}
		worldID := notify.WorldId
		if worldID == "" {
			worldID = session.WorldID
		}
		gs.AttachPlayer(session)
		gs.EnterDestroy(session, worldID)
	default:
		if session.State == sessionResumingWorld {
			gs.SendLogoutCleanup(session)
		}
		conn := session.Conn
		gs.RemoveSession(session)
		if conn != nil {
			gs.FailClientLogin(conn, "account logged in elsewhere")
		}
	}
}

func (gs *gatewayServer) HandleCentralHelloRsp(_ *common.ConnWrapper, rsp *common.HelloRsp) {
	if !rsp.Success {
		log.Printf("[gateway %s] hello rejected: %s", gs.ServerID, rsp.Message)
		return
	}
	log.Printf("[gateway %s] registered: %s", gs.ServerID, rsp.Message)
	gs.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerWorld})
}

func (gs *gatewayServer) HandleCentralServerListRsp(_ *common.ConnWrapper, rsp *common.ServerListRsp) {
	for _, s := range rsp.Servers {
		gs.ConnectToWorld(s.ServerId, s.ListenAddr)
	}
}

func (gs *gatewayServer) HandleCentralNewWorldNotify(_ *common.ConnWrapper, entry *common.ServerEntry) {
	gs.ConnectToWorld(entry.ServerId, entry.ListenAddr)
}

func (gs *gatewayServer) HandleCentralTransferCommitNotify(_ *common.ConnWrapper, notify *common.TransferCommitNotify) {
	session := gs.GetByPlayer(notify.PlayerId)
	if session == nil {
		session = gs.GetByAccount(notify.Account)
	}
	if session == nil {
		return
	}
	if notify.SessionId != "" && session.SessionID != "" && notify.SessionId != session.SessionID {
		return
	}
	session.WorldID = notify.WorldId
	session.SceneID = notify.SceneId
	gs.worldMu.Lock()
	if addr, ok := gs.worldAddrs[notify.WorldId]; ok {
		session.WorldAddr = addr
	}
	gs.worldMu.Unlock()
	if session.Conn == nil {
		return
	}
	if session.State != sessionActive && session.State != sessionSuspect {
		return
	}
	enter := &common.EnterSceneNotify{
		WorldId:  notify.WorldId,
		PlayerId: notify.PlayerId,
		X:        notify.X,
		Z:        notify.Z,
		Width:    notify.Width,
		Height:   notify.Height,
		SceneId:  notify.SceneId,
		Seamless: notify.Seamless,
	}
	common.SendMsg(session.Conn, common.Wd2Cli_EnterSceneNotify, enter)
	log.Printf("[gateway] transfer commit: player=%d world=%s scene=%d", notify.PlayerId, notify.WorldId, notify.SceneId)
}

func (gs *gatewayServer) HandleCentralLogoutBeginRsp(_ *common.ConnWrapper, rsp *common.LogoutBeginRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil {
		return
	}
	if rsp.SessionId != "" && session.SessionID != "" && session.SessionID != rsp.SessionId {
		return
	}
	if !rsp.Success {
		gs.FailLogoutSession(session, rsp.Message)
		return
	}
	gs.StartLogoutCleanup(session)
}

func (gs *gatewayServer) HandleCentralLogoutCleanupRsp(_ *common.ConnWrapper, rsp *common.LogoutCleanupRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil {
		return
	}
	if rsp.SessionId != "" && session.SessionID != "" && session.SessionID != rsp.SessionId {
		return
	}
	conn := session.Conn
	gs.RemoveSession(session)
	if conn != nil {
		message := rsp.Message
		if message == "" {
			if rsp.Success {
				message = "logged out"
			} else {
				message = "logout failed"
			}
		}
		logoutRsp := common.LogoutRsp{Success: rsp.Success, Message: message}
		common.SendMsg(conn, common.Gw2Cli_LogoutRsp, &logoutRsp)
		conn.CloseAfterSend()
	}
}

func (gs *gatewayServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	conns := make([]*common.ConnWrapper, 0, len(gs.byConn))
	for conn := range gs.byConn {
		conns = append(conns, conn)
	}
	for _, conn := range conns {
		common.SendMsg(conn, common.Gw2Cli_ServerShutdownNotify, &common.ServerShutdownNotify{Message: notify.Reason})
		conn.CloseAfterSend()
	}
	gs.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: gs.ServerID})
	log.Printf("[gateway %s] notified %d clients of shutdown, stopping", gs.ServerID, len(conns))
	go gs.Stop()
}
