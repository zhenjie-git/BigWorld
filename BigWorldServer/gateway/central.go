package main

import (
	"log"
	"time"

	"bigworld/common"
)

func (gs *gatewayServer) HandleCentralLoginPrepareRsp(_ *common.ConnWrapper, rsp *common.LoginPrepareRsp) {
	pending, ok := gs.pendings[rsp.ReqId]
	if ok {
		delete(gs.pendings, rsp.ReqId)
	}

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
		deadline:  time.Now().Add(createEntityTimeout),
	}
	gs.loginPendings[pending.account] = pend

	createReq := common.CreateEntityReq{Account: pending.account}
	gs.ForwardMsgToWorld(rsp.WorldId, common.Gw2Wd_CreateEntityReq, &createReq)
}

func (gs *gatewayServer) HandleCentralLoginFinishRsp(_ *common.ConnWrapper, rsp *common.LoginFinishRsp) {
	pend, ok := gs.loginPendings[rsp.Account]
	if ok {
		delete(gs.loginPendings, rsp.Account)
	}

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
		SceneId:   pend.sceneID,
	}
	common.SendMsg(pend.conn, common.Gw2Cli_LoginRsp, &loginRsp)

	gs.sessions[pend.conn] = &clientSession{
		PlayerId:      pend.playerID,
		WorldId:       pend.worldID,
		WorldAddr:     pend.worldAddr,
		SceneId:       pend.sceneID,
		Account:       rsp.Account,
		LastHeartbeat: time.Now(),
		State:         stateHealthy,
		X:             pend.x,
		Z:             pend.z,
		Width:         pend.width,
		Height:        pend.height,
	}
	gs.playerConns[pend.playerID] = pend.conn

	pendConn := pend.conn
	worldID, x, z := pend.worldID, pend.x, pend.z
	width, height, playerID := pend.width, pend.height, pend.playerID
	sceneID := pend.sceneID
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
			common.SendMsg(pendConn, common.Wd2Cli_EnterSceneNotify, &notify)
		})
	})
}

func (gs *gatewayServer) HandleCentralForceKick(_ *common.ConnWrapper, notify *common.ForceKickNotify) {
	log.Printf("[gateway] force-kick: player=%d account=%s", notify.PlayerId, notify.Account)

	delete(gs.recoverableSessions, notify.Account)

	if _, loggingOut := gs.loggingOut[notify.PlayerId]; loggingOut {
		log.Printf("[gateway] force-kick skipped, player %d already logging out", notify.PlayerId)
		return
	}

	oldConn := gs.playerConns[notify.PlayerId]
	if oldConn != nil {
		delete(gs.sessions, oldConn)
		delete(gs.playerConns, notify.PlayerId)
	}
	delete(gs.loggingOut, notify.PlayerId)

	if oldConn != nil {
		rsp := common.LogoutRsp{Success: false, Message: "账号在其他设备登录"}
		common.SendMsg(oldConn, common.Gw2Cli_LogoutRsp, &rsp)
		oldConn.Close()
	}

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
	playerConn := gs.playerConns[rsp.PlayerId]
	if playerConn == nil {

		return
	}
	session := gs.sessions[playerConn]
	var worldID string
	if session != nil {
		worldID = session.WorldId
	}

	if worldID == "" {
		gs.SendLogoutCleanup(rsp.PlayerId)
		return
	}

	gs.SendDestroyAndArmTimer(rsp.PlayerId, worldID)
}

func (gs *gatewayServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	clientConns := make([]*common.ConnWrapper, 0, len(gs.sessions))
	for c := range gs.sessions {
		clientConns = append(clientConns, c)
	}

	for _, c := range clientConns {
		common.SendMsg(c, common.Gw2Cli_ServerShutdownNotify, &common.ServerShutdownNotify{Message: notify.Reason})
		c.CloseAfterSend()
	}

	gs.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: gs.ServerId})
	log.Printf("[gateway %s] notified %d clients of shutdown, stopping", gs.ServerId, len(clientConns))
	go gs.Stop()
}

func (gs *gatewayServer) HandleCentralLogoutCleanupRsp(_ *common.ConnWrapper, rsp *common.LogoutCleanupRsp) {
	clientConn, ok := gs.playerConns[rsp.PlayerId]
	if ok {
		delete(gs.playerConns, rsp.PlayerId)
	}
	if clientConn != nil {
		delete(gs.sessions, clientConn)
	}
	delete(gs.loggingOut, rsp.PlayerId)

	if clientConn == nil {
		return
	}

	logoutRsp := common.LogoutRsp{Success: true, Message: "已下线"}
	common.SendMsg(clientConn, common.Gw2Cli_LogoutRsp, &logoutRsp)
	clientConn.Close()
}
