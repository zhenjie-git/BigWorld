package main

import (
	"fmt"
	"log"

	"bigworld/common"
)

// handleLoginPrepareReq is called when the gateway confirms the client has connected.
// It cancels the gateway timeout, assigns a world, and updates the account state.
// PlayerId is not yet known at this point — the world generates it during entity creation.
func (cs *centralServer) HandleLoginPrepareReq(conn *common.ConnWrapper, req *common.LoginPrepareReq) {
	log.Printf("[central] login prepare request: reqID=%d account=%s", req.ReqId, req.Account)

	if cs.IsShuttingDown() {
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "服务器正在关闭",
		})
		return
	}

	cs.mu.Lock()

	// Cancel the gateway timeout timer.
	if timer, ok := cs.loginTimers[req.Account]; ok {
		timer.Stop()
		delete(cs.loginTimers, req.Account)
	} else {
		cs.mu.Unlock()
		log.Printf("[central] account %s has no active login timer (may have timed out)", req.Account)
		rsp := common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "登录超时，请重新登录",
		}
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &rsp)
		return
	}

	state, exists := cs.accountStates[req.Account]
	if !exists || state.Status != AccountLoggingIn {
		cs.mu.Unlock()
		log.Printf("[central] account %s has unexpected state, rejecting login prepare", req.Account)
		rsp := common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "账号状态异常",
		}
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &rsp)
		return
	}

	// Pick a world server.
	var worldID, worldAddr string
	for id, rec := range cs.servers {
		if rec.info.ServerType == common.ServerWorld {
			worldID = id
			worldAddr = rec.info.ListenAddr
			break
		}
	}

	if worldID == "" {
		cs.mu.Unlock()
		log.Printf("[central] no world server available for account %s", req.Account)
		rsp := common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "没有可用的世界服",
		}
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &rsp)
		return
	}

	state.WorldId = worldID
	cs.mu.Unlock()

	log.Printf("[central] login prepared: account=%s gateway=%s world=%s",
		req.Account, state.GatewayId, worldID)

	rsp := common.LoginPrepareRsp{
		ReqId:     req.ReqId,
		Success:   true,
		WorldId:   worldID,
		WorldAddr: worldAddr,
		Message:   "登录准备就绪",
	}
	common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &rsp)
}

// handleLoginFinishReq handles login completion or failure/rollback.
// On success, transitions the account from AccountLoggingIn to AccountOnline
// and creates the onlinePlayers entry with the world-generated playerID.
func (cs *centralServer) HandleLoginFinishReq(conn *common.ConnWrapper, req *common.LoginFinishReq) {
	log.Printf("[central] login finish request: account=%s player=%d success=%v",
		req.Account, req.PlayerId, req.Success)

	cs.mu.Lock()

	state, exists := cs.accountStates[req.Account]

	rsp := common.LoginFinishRsp{
		PlayerId: req.PlayerId,
		Account:  req.Account,
	}

	if !exists || state.Status != AccountLoggingIn {
		cs.mu.Unlock()
		log.Printf("[central] login finish for unexpected account %s (state=%v)", req.Account, state)
		rsp.Success = false
		rsp.Message = "账号状态异常"
		common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
		return
	}

	if req.Success {
		if req.PlayerId == 0 {
			cs.mu.Unlock()
			log.Printf("[central] login finish success but playerID is 0 for account %s", req.Account)
			rsp.Success = false
			rsp.Message = "实体创建未返回有效ID"
			common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
			return
		}

		// Transition account to online.
		state.Status = AccountOnline
		state.PlayerId = req.PlayerId
		state.WorldId = req.WorldId

		// Create online player entry (only on successful login).
		cs.onlinePlayers[req.PlayerId] = &onlinePlayer{
			PlayerId:  req.PlayerId,
			Account:   req.Account,
			WorldId:   req.WorldId,
			GatewayId: req.GatewayId,
		}

		rsp.Success = true
		rsp.Message = "登录完成"
		log.Printf("[central] player %d (account=%s) now online via gateway %s world %s",
			req.PlayerId, req.Account, req.GatewayId, req.WorldId)
	} else {
		// Login failed or timed out — roll back the account state.
		delete(cs.accountStates, req.Account)
		if timer, ok := cs.loginTimers[req.Account]; ok {
			timer.Stop()
			delete(cs.loginTimers, req.Account)
		}
		rsp.Success = false
		rsp.Message = fmt.Sprintf("登录失败: %s", req.Message)
		log.Printf("[central] account %s login rolled back: %s", req.Account, req.Message)
	}
	cs.mu.Unlock()

	common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
}

// handleLogoutBeginReq is called when a gateway notifies central that a player is logging out.
func (cs *centralServer) HandleLogoutBeginReq(conn *common.ConnWrapper, req *common.LogoutBeginReq) {

	cs.mu.Lock()
	if _, ok := cs.onlinePlayers[req.PlayerId]; ok {
		log.Printf("[central] player %d marked as logging out", req.PlayerId)
	}
	cs.mu.Unlock()

	rsp := common.LogoutBeginRsp{
		Success:  true,
		PlayerId: req.PlayerId,
		Message:  "登出开始",
	}
	common.SendMsg(conn, common.Ct2Gw_LogoutBeginRsp, &rsp)
	log.Printf("[central] logout begin processed for player %d", req.PlayerId)
}

// handleLogoutCleanupReq removes the player from the online table and cleans up the account state.
// It checks GatewayId to prevent a stale cleanup from a force-kicked session
// from deleting the state of a new login with the same playerID (from persistent storage).
// After cleanup, if a pending assign is waiting for the 顶号 to complete, it continues the login flow.
func (cs *centralServer) HandleLogoutCleanupReq(conn *common.ConnWrapper, req *common.LogoutCleanupReq) {
	var (
		account    string
		ok         bool
		assignRsp  common.GatewayAssignRsp
		assignConn *common.ConnWrapper
		needAssign bool
	)

	cs.mu.Lock()

	player, exists := cs.onlinePlayers[req.PlayerId]
	if exists {
		account = player.Account
		if player.GatewayId == req.GatewayId {
			// Only clean up the account state if it still points to this player
			// and is NOT waiting for a kick (顶号 pending assign).
			if state, stExists := cs.accountStates[account]; stExists && state.PlayerId == req.PlayerId && state.Status != AccountWaitingKick {
				delete(cs.accountStates, account)
			}
			delete(cs.onlinePlayers, req.PlayerId)
			ok = true
		} else {
			// GatewayId mismatch: a new login with the same playerID has replaced this session.
			log.Printf("[central] ignoring stale cleanup for player %d (gateway %s != current %s)",
				req.PlayerId, req.GatewayId, player.GatewayId)
		}
	}

	// Stop force-kick timer if this was a 顶号 cleanup.
	if timer, ok2 := cs.forceKickTimers[req.PlayerId]; ok2 {
		timer.Stop()
		delete(cs.forceKickTimers, req.PlayerId)
	}

	// If the old player was cleaned up and a pending assign is waiting for this account,
	// continue the login flow now. 锁内只取响应,锁外发送。
	if account != "" {
		assignRsp, assignConn, needAssign = cs.TryContinuePendingAssign(account)
	}

	cs.mu.Unlock()

	common.SendMsg(conn, common.Ct2Gw_LogoutCleanupRsp, &common.LogoutCleanupRsp{
		Success:  true,
		PlayerId: req.PlayerId,
		Message:  "在线表清理完成",
	})
	if needAssign {
		common.SendMsg(assignConn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}

	if ok {
		log.Printf("[central] player %d removed from online table", req.PlayerId)
	} else {
		log.Printf("[central] player %d not found in online table (or stale cleanup ignored)", req.PlayerId)
	}
}

// handleForceKickTimeout cleans up the PlayerForceOffline entry when the old
// gateway doesn't respond to a force-kick notification in time.
// kickedGatewayId is checked to avoid cleaning up a session that was already
// replaced by a new login with the same playerID.
// After cleanup, if a pending assign is waiting for the 顶号 to complete, it continues the login flow.
func (cs *centralServer) HandleForceKickTimeout(playerID uint64, kickedGatewayId string) {
	var (
		assignRsp  common.GatewayAssignRsp
		assignConn *common.ConnWrapper
		needAssign bool
	)

	cs.mu.Lock()

	player, exists := cs.onlinePlayers[playerID]
	if !exists {
		cs.mu.Unlock()
		return // already cleaned up by LogoutCleanupReq
	}

	// Only clean up if the session still belongs to the kicked gateway.
	if player.GatewayId != kickedGatewayId {
		log.Printf("[central] force-kick timeout for player %d ignored (gateway changed to %s)",
			playerID, player.GatewayId)
		cs.mu.Unlock()
		return
	}

	account := player.Account

	// Only remove account state if it still points to this kicked player
	// and is NOT waiting for a kick (顶号 pending assign).
	if state, ok := cs.accountStates[account]; ok && state.PlayerId == playerID && state.Status != AccountWaitingKick {
		delete(cs.accountStates, account)
	}
	delete(cs.onlinePlayers, playerID)
	delete(cs.forceKickTimers, playerID)
	log.Printf("[central] force-kick timeout for player %d, state cleaned up", playerID)

	// If a pending assign is waiting for this cleanup, continue the login flow.
	// 锁内只取响应,锁外发送。
	assignRsp, assignConn, needAssign = cs.TryContinuePendingAssign(account)

	cs.mu.Unlock()

	if needAssign {
		common.SendMsg(assignConn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}
}
