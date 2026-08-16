package main

import (
	"log"
	"time"

	"bigworld/common"
)

func (gs *gatewayServer) HandleCentralLoginPrepareRsp(_ *common.ConnWrapper, rsp *common.LoginPrepareRsp) {
	gs.mu.Lock()
	pending, ok := gs.pendings[rsp.ReqId]
	if ok {
		delete(gs.pendings, rsp.ReqId)
		if pending.timer != nil {
			pending.timer.Stop()
		}
	}
	gs.mu.Unlock()

	if !ok {
		return
	}

	if !rsp.Success {
		pending.conn.Close()
		return
	}

	log.Printf("[gateway] login prepared: account=%s world=%s", pending.account, rsp.WorldId)

	pend := &loginPending{
		conn:      pending.conn,
		account:   pending.account,
		worldID:   rsp.WorldId,
		worldAddr: rsp.WorldAddr,
	}
	pend.timer = time.AfterFunc(createEntityTimeout, func() {
		gs.HandleCreateEntityTimeout(pending.account)
	})

	gs.mu.Lock()
	gs.loginPendings[pending.account] = pend
	gs.mu.Unlock()

	createReq := common.CreateEntityReq{Account: pending.account}
	gs.ForwardMsgToWorld(rsp.WorldId, common.Gw2Wd_CreateEntityReq, &createReq)
}

func (gs *gatewayServer) HandleCentralLoginFinishRsp(_ *common.ConnWrapper, rsp *common.LoginFinishRsp) {
	gs.mu.Lock()
	pend, ok := gs.loginPendings[rsp.Account]
	if ok {
		delete(gs.loginPendings, rsp.Account)
		if pend.timer != nil {
			pend.timer.Stop()
		}
	}
	gs.mu.Unlock()

	if !ok || !rsp.Success {
		if pend != nil {
			pend.conn.Close()
		}
		return
	}

	loginRsp := common.LoginRsp{
		Success:   true,
		Message:   rsp.Message,
		WorldAddr: pend.worldAddr,
		WorldId:   pend.worldID,
		PlayerId:  pend.playerID,
		X:         pend.x,
		Z:         pend.z,
		Width:     pend.width,
		Height:    pend.height,
	}
	common.SendMsg(pend.conn, common.Gw2Cli_LoginRsp, &loginRsp)

	gs.mu.Lock()
	gs.sessions[pend.conn] = &clientSession{
		PlayerId:      pend.playerID,
		WorldId:       pend.worldID,
		WorldAddr:     pend.worldAddr,
		Account:       rsp.Account,
		LastHeartbeat: time.Now(),
		State:         stateHealthy,
	}
	gs.playerConns[pend.playerID] = pend.conn
	gs.mu.Unlock()

	notify := common.EnterSceneNotify{
		WorldId:  pend.worldID,
		PlayerId: pend.playerID,
		X:        pend.x,
		Z:        pend.z,
		Width:    pend.width,
		Height:   pend.height,
	}
	time.AfterFunc(100*time.Millisecond, func() {
		common.SendMsg(pend.conn, common.Wd2Cli_EnterSceneNotify, &notify)
	})
}

func (gs *gatewayServer) HandleCentralForceKick(_ *common.ConnWrapper, notify *common.ForceKickNotify) {
	log.Printf("[gateway] force-kick: player=%d account=%s", notify.PlayerId, notify.Account)

	// ForceKick 到达时立即回收本网关会话并断开旧客户端,不能等 destroy->cleanup 往返
	// 才清 session:窗口期内旧客户端无被踢感知、操作全失败,且 cleanup 响应一旦丢失
	// 会留下长时孤儿 session。destroy/cleanup 只负责 world 与 central 收尾。
	gs.mu.Lock()
	if timer, ok := gs.suspectTimers[notify.Account]; ok {
		timer.Stop()
		delete(gs.suspectTimers, notify.Account)
	}
	delete(gs.recoverableSessions, notify.Account)

	// 若玩家正在主动下线,让 logout 流程完成,不重复触发 destroy/断连。
	// logout 流程的 cleanup 会到达 central,顶号那边靠它继续(或 forceKickTimers 兜底)。
	if _, loggingOut := gs.loggingOut[notify.PlayerId]; loggingOut {
		gs.mu.Unlock()
		log.Printf("[gateway] force-kick skipped, player %d already logging out", notify.PlayerId)
		return
	}

	oldConn := gs.playerConns[notify.PlayerId]
	if oldConn != nil {
		delete(gs.sessions, oldConn)
		delete(gs.playerConns, notify.PlayerId)
	}
	delete(gs.loggingOut, notify.PlayerId)
	gs.mu.Unlock()

	if oldConn != nil {
		rsp := common.LogoutRsp{Success: false, Message: "账号在其他设备登录"}
		common.SendMsg(oldConn, common.Gw2Cli_LogoutRsp, &rsp)
		oldConn.Close()
	}

	// playerConns 已清,后续 cleanup 响应回来时 HandleCentralLogoutCleanupRsp
	// 命中 clientConn==nil 直接 return,不会重复发下线通知或重复断连。
	if notify.WorldId != "" {
		gs.SendDestroyAndArmTimer(notify.PlayerId, notify.WorldId)
	} else {
		gs.SendLogoutCleanup(notify.PlayerId)
	}
}

func (gs *gatewayServer) HandleCentralRegisterRsp(_ *common.ConnWrapper, rsp *common.RegisterRsp) {
	if rsp.Success {
		log.Printf("[gateway %s] registered: %s", gs.ServerId, rsp.Message)
		req := common.ServerListReq{Type: common.ServerWorld}
		gs.SendToCentralMsg(common.Srv2Ct_ServerListReq, &req)
	}
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
	if !rsp.Success {
		return
	}
	gs.mu.Lock()
	playerConn := gs.playerConns[rsp.PlayerId]
	if playerConn == nil {
		// session 已不在(suspect/断线场景):destroy 由 handleSuspectTimeout 自行发送,
		// cleanup 由 DestroyEntityRsp 触发,这里不重复发。
		gs.mu.Unlock()
		return
	}
	session := gs.sessions[playerConn]
	var worldID string
	if session != nil {
		worldID = session.WorldId
	}
	gs.mu.Unlock()

	if worldID == "" {
		gs.SendLogoutCleanup(rsp.PlayerId)
		return
	}

	gs.SendDestroyAndArmTimer(rsp.PlayerId, worldID)
}

// HandleCentralShutdownNotify broadcasts a shutdown notice to every connected
// client and closes each connection with SO_LINGER so the final message is
// actually delivered (rather than dropped by an immediate Close). Then it acks
// central and stops. Central shuts the gateway down first, so cutting clients
// here is the cleanest point to signal "server is closing".
func (gs *gatewayServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	gs.mu.Lock()
	clientConns := make([]*common.ConnWrapper, 0, len(gs.sessions))
	for c := range gs.sessions {
		clientConns = append(clientConns, c)
	}
	gs.mu.Unlock()

	for _, c := range clientConns {
		common.SendMsg(c, common.Gw2Cli_ServerShutdownNotify, &common.ServerShutdownNotify{Message: notify.Reason})
		c.CloseAfterSend()
	}

	gs.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: gs.ServerId})
	log.Printf("[gateway %s] notified %d clients of shutdown, stopping", gs.ServerId, len(clientConns))
	go gs.Stop()
}

func (gs *gatewayServer) HandleCentralLogoutCleanupRsp(_ *common.ConnWrapper, rsp *common.LogoutCleanupRsp) {
	gs.mu.Lock()
	clientConn, ok := gs.playerConns[rsp.PlayerId]
	if ok {
		delete(gs.playerConns, rsp.PlayerId)
	}
	if clientConn != nil {
		delete(gs.sessions, clientConn)
	}
	delete(gs.loggingOut, rsp.PlayerId)

	for account, session := range gs.recoverableSessions {
		if session.PlayerId == rsp.PlayerId {
			if timer, tOk := gs.suspectTimers[account]; tOk {
				timer.Stop()
				delete(gs.suspectTimers, account)
			}
			delete(gs.recoverableSessions, account)
			break
		}
	}
	gs.mu.Unlock()

	if clientConn == nil {
		return
	}

	logoutRsp := common.LogoutRsp{Success: true, Message: "已下线"}
	common.SendMsg(clientConn, common.Gw2Cli_LogoutRsp, &logoutRsp)
	clientConn.Close()
}
