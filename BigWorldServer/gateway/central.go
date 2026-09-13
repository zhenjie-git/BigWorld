package main

import (
	"log"
	"time"

	"bigworld/common"
)

func (gs *gatewayServer) SendLogoutCleanupRaw(playerID uint64, sessionID string) {
	req := common.LogoutCleanupReq{
		PlayerId:  playerID,
		GatewayId: gs.ServerId,
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
		pending.Account, rsp.WorldId, pending.SessionId)

	pending.WorldId = rsp.WorldId
	pending.WorldAddr = rsp.WorldAddr
	pending.State = sessionCreating
	pending.Deadline = time.Now().Add(createEntityTimeout)

	createReq := common.CreateEntityReq{
		Account:   pending.Account,
		GatewayId: gs.ServerId,
		SessionId: pending.SessionId,
	}
	if err := gs.ForwardMsgToWorld(rsp.WorldId, common.Gw2Wd_CreateEntityReq, &createReq); err != nil {
		gs.SendLoginFinishFailure(pending.Account, pending.SessionId, "world unavailable")
		gs.FailSession(pending, "world unavailable")
	}
}

func (gs *gatewayServer) HandleCentralLoginFinishRsp(_ *common.ConnWrapper, rsp *common.LoginFinishRsp) {
	session := gs.GetByAccount(rsp.Account)
	if session == nil {
		return
	}
	if rsp.SessionId != "" && session.SessionId != "" && rsp.SessionId != session.SessionId {
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
		if session.PlayerId != 0 && session.WorldId != "" {
			owner := gs.byPlayer[session.PlayerId]
			if owner == nil || owner == session {
				gs.AttachPlayer(session)
				gs.EnterDestroy(session, session.WorldId)
				return
			}
		}
		gs.RemoveSession(session)
		return
	}

	if rsp.PlayerId != 0 {
		session.PlayerId = rsp.PlayerId
	}

	if session.State == sessionResuming {
		gs.AttachPlayer(session)
		session.State = sessionResumingWorld
		session.Deadline = time.Now().Add(createEntityTimeout)
		req := common.ResumeEntityReq{
			PlayerId:  session.PlayerId,
			GatewayId: gs.ServerId,
			SessionId: session.SessionId,
		}
		if err := gs.ForwardMsgToWorld(session.WorldId, common.Gw2Wd_ResumeEntityReq, &req); err != nil {
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
	gs.AttachPlayer(session)

	loginRsp := common.LoginRsp{
		Success:   true,
		Message:   rsp.Message,
		WorldAddr: session.WorldAddr,
		WorldId:   session.WorldId,
		PlayerId:  session.PlayerId,
		X:         snapshot.X,
		Z:         snapshot.Z,
		Width:     snapshot.Width,
		Height:    snapshot.Height,
		SceneId:   snapshot.SceneId,
	}
	common.SendMsg(session.Conn, common.Gw2Cli_LoginRsp, &loginRsp)

	conn := session.Conn
	worldID, x, z := session.WorldId, snapshot.X, snapshot.Z
	width, height, playerID := snapshot.Width, snapshot.Height, session.PlayerId
	sceneID := snapshot.SceneId
	time.AfterFunc(100*time.Millisecond, func() {
		gs.Loop.Defer(func() {
			notify := common.EnterSceneNotify{
				WorldId:  worldID,
				PlayerId: playerID,
				X:        x,
				Z:        z,
				Width:    width,
				Height:   height,
				SceneId:  sceneID,
			}
			common.SendMsg(conn, common.Wd2Cli_EnterSceneNotify, &notify)
		})
	})
}
func (gs *gatewayServer) HandleCentralForceKick(_ *common.ConnWrapper, notify *common.ForceKickNotify) {
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
	if notify.SessionId != "" && session.SessionId != "" && session.SessionId != notify.SessionId {
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
			worldID = session.WorldId
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
		log.Printf("[gateway %s] hello rejected: %s", gs.ServerId, rsp.Message)
		return
	}
	log.Printf("[gateway %s] registered: %s", gs.ServerId, rsp.Message)
	gs.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerWorld})
}

func (gs *gatewayServer) HandleCentralServerListRsp(_ *common.ConnWrapper, rsp *common.ServerListRsp) {
	for _, s := range rsp.Servers {
		gs.ConnectToWorld(s.ServerId, s.ListenAddr)
	}
}

func (gs *gatewayServer) HandleCentralNewWorld(_ *common.ConnWrapper, entry *common.ServerEntry) {
	gs.ConnectToWorld(entry.ServerId, entry.ListenAddr)
}

func (gs *gatewayServer) HandleCentralLogoutBeginRsp(_ *common.ConnWrapper, rsp *common.LogoutBeginRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil {
		return
	}
	if rsp.SessionId != "" && session.SessionId != "" && session.SessionId != rsp.SessionId {
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
	if rsp.SessionId != "" && session.SessionId != "" && session.SessionId != rsp.SessionId {
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
	gs.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: gs.ServerId})
	log.Printf("[gateway %s] notified %d clients of shutdown, stopping", gs.ServerId, len(conns))
	go gs.Stop()
}
