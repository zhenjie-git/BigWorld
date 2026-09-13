package main

import (
	"fmt"
	"log"
	"time"

	"bigworld/common"
)

func (cs *centralServer) senderID(conn *common.ConnWrapper) string {
	return cs.connToID[conn]
}

func (cs *centralServer) HandleLoginPrepareReq(conn *common.ConnWrapper, req *common.LoginPrepareReq) {
	log.Printf("[central] login prepare request: reqID=%d account=%s gateway=%s session=%s",
		req.ReqId, req.Account, cs.senderID(conn), req.SessionId)

	if cs.IsShuttingDown() {
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "server shutting down",
		})
		return
	}

	if req.Account == "" {
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "empty account",
		})
		return
	}

	state, exists := cs.accountStates[req.Account]
	if !exists || state.Status != AccountLoggingIn {
		log.Printf("[central] account %s has unexpected state, rejecting login prepare", req.Account)
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "account state error",
		})
		return
	}

	sender := cs.senderID(conn)
	if sender == "" || sender != state.GatewayId {
		log.Printf("[central] rejecting login prepare for account %s: gateway %s != assigned %s",
			req.Account, sender, state.GatewayId)
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "gateway assignment mismatch",
		})
		return
	}

	if _, ok := cs.loginDeadlines[req.Account]; !ok {
		log.Printf("[central] account %s has no active login timer", req.Account)
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "login timed out",
		})
		return
	}

	if state.SessionId == "" {
		state.SessionId = req.SessionId
	} else if req.SessionId != "" && state.SessionId != req.SessionId {
		log.Printf("[central] rejecting login prepare for account %s: session %s != %s",
			req.Account, req.SessionId, state.SessionId)
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "session mismatch",
		})
		return
	}

	worldID, worldAddr := cs.pickWorldLocked(state.WorldId)

	if worldID == "" {
		log.Printf("[central] no world server available for account %s", req.Account)
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "no world server",
		})
		return
	}

	state.WorldId = worldID
	cs.loginDeadlines[req.Account] = time.Now().Add(loginFinishTimeout)

	log.Printf("[central] login prepared: account=%s gateway=%s world=%s",
		req.Account, state.GatewayId, worldID)

	common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &common.LoginPrepareRsp{
		ReqId:     req.ReqId,
		Success:   true,
		WorldId:   worldID,
		WorldAddr: worldAddr,
		Message:   "login prepared",
	})
}

func (cs *centralServer) HandleLoginFinishReq(conn *common.ConnWrapper, req *common.LoginFinishReq) {
	log.Printf("[central] login finish request: account=%s player=%d success=%v gateway=%s session=%s",
		req.Account, req.PlayerId, req.Success, cs.senderID(conn), req.SessionId)

	rsp := common.LoginFinishRsp{
		PlayerId:  req.PlayerId,
		Account:   req.Account,
		SessionId: req.SessionId,
	}

	state, exists := cs.accountStates[req.Account]
	if !exists || state.Status != AccountLoggingIn {
		log.Printf("[central] login finish for unexpected account %s", req.Account)
		rsp.Success = false
		rsp.Message = "account state error"
		common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
		return
	}

	sender := cs.senderID(conn)
	if sender == "" || sender != state.GatewayId {
		rsp.Success = false
		rsp.Message = "gateway assignment mismatch"
		common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
		return
	}

	if req.SessionId != "" && state.SessionId != "" && state.SessionId != req.SessionId {
		rsp.Success = false
		rsp.Message = "session mismatch"
		common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
		return
	}

	if req.Success {
		if req.PlayerId == 0 {
			log.Printf("[central] login finish success but playerID is 0 for account %s", req.Account)
			rsp.Success = false
			rsp.Message = "invalid player id"
			common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
			return
		}

		state.Status = AccountOnline
		state.PlayerId = req.PlayerId
		state.WorldId = req.WorldId
		if req.SessionId != "" {
			state.SessionId = req.SessionId
		}

		cs.onlinePlayers[req.PlayerId] = &onlinePlayer{
			PlayerId:  req.PlayerId,
			Account:   req.Account,
			WorldId:   req.WorldId,
			GatewayId: sender,
			SessionId: state.SessionId,
		}
		delete(cs.loginDeadlines, req.Account)
		delete(cs.forceKickDeadlines, req.PlayerId)

		rsp.Success = true
		rsp.Message = "login complete"
		log.Printf("[central] player %d (account=%s) now online via gateway %s world %s session %s",
			req.PlayerId, req.Account, sender, req.WorldId, state.SessionId)
	} else {
		if state.SessionId == "" || state.SessionId == req.SessionId {
			delete(cs.accountStates, req.Account)
			delete(cs.loginDeadlines, req.Account)
		}
		rsp.Success = false
		rsp.Message = fmt.Sprintf("login failed: %s", req.Message)
		log.Printf("[central] account %s login rolled back: %s", req.Account, req.Message)
	}

	common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
}

func (cs *centralServer) HandleLogoutBeginReq(conn *common.ConnWrapper, req *common.LogoutBeginReq) {
	sender := cs.senderID(conn)
	rsp := common.LogoutBeginRsp{
		Success:   true,
		PlayerId:  req.PlayerId,
		Message:   "logout begin",
		SessionId: req.SessionId,
	}

	if player, ok := cs.onlinePlayers[req.PlayerId]; ok {
		if sender == "" || player.GatewayId != sender || player.SessionId != req.SessionId {
			rsp.Success = false
			rsp.Message = "stale logout session"
			common.SendMsg(conn, common.Ct2Gw_LogoutBeginRsp, &rsp)
			log.Printf("[central] rejecting logout begin for player %d: gateway/session mismatch", req.PlayerId)
			return
		}
		log.Printf("[central] player %d marked as logging out", req.PlayerId)
	}

	common.SendMsg(conn, common.Ct2Gw_LogoutBeginRsp, &rsp)
}

func (cs *centralServer) HandleLogoutCleanupReq(conn *common.ConnWrapper, req *common.LogoutCleanupReq) {
	sender := cs.senderID(conn)
	player, exists := cs.onlinePlayers[req.PlayerId]
	ok := false
	account := ""

	if exists {
		account = player.Account
		if sender == "" || player.GatewayId != sender || player.SessionId != req.SessionId {
			common.SendMsg(conn, common.Ct2Gw_LogoutCleanupRsp, &common.LogoutCleanupRsp{
				Success:   false,
				PlayerId:  req.PlayerId,
				Message:   "stale cleanup ignored",
				SessionId: req.SessionId,
			})
			log.Printf("[central] ignoring stale cleanup for player %d (gateway/session mismatch)", req.PlayerId)
			return
		}

		if state, stExists := cs.accountStates[account]; stExists && state.PlayerId == req.PlayerId && state.Status != AccountWaitingKick {
			delete(cs.accountStates, account)
			delete(cs.loginDeadlines, account)
		}
		delete(cs.onlinePlayers, req.PlayerId)
		delete(cs.forceKickDeadlines, req.PlayerId)
		ok = true
	}

	var (
		assignRsp  common.GatewayAssignRsp
		assignConn *common.ConnWrapper
		needAssign bool
	)
	if account != "" {
		assignRsp, assignConn, needAssign = cs.TryContinuePendingAssign(account)
	}

	common.SendMsg(conn, common.Ct2Gw_LogoutCleanupRsp, &common.LogoutCleanupRsp{
		Success:   true,
		PlayerId:  req.PlayerId,
		Message:   "cleanup done",
		SessionId: req.SessionId,
	})
	if needAssign {
		common.SendMsg(assignConn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}

	if ok {
		log.Printf("[central] player %d removed from online table", req.PlayerId)
	} else {
		log.Printf("[central] player %d not found in online table (stale cleanup ignored)", req.PlayerId)
	}
}

func (cs *centralServer) HandleForceKickTimeout(playerID uint64, kickedGatewayId, kickedSessionId, kickedAccount string) {
	player, exists := cs.onlinePlayers[playerID]
	if !exists {
		delete(cs.forceKickDeadlines, playerID)
		if kickedAccount == "" {
			return
		}
		if state, ok := cs.accountStates[kickedAccount]; ok && state.Status == AccountWaitingKick {
			assignRsp, assignConn, needAssign := cs.TryContinuePendingAssign(kickedAccount)
			if needAssign {
				common.SendMsg(assignConn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
			}
		}
		return
	}

	if player.GatewayId != kickedGatewayId || (kickedSessionId != "" && player.SessionId != kickedSessionId) {
		delete(cs.forceKickDeadlines, playerID)
		log.Printf("[central] force-kick timeout for player %d ignored (ownership changed)", playerID)
		return
	}

	account := player.Account

	if state, ok := cs.accountStates[account]; ok && state.PlayerId == playerID && state.Status != AccountWaitingKick {
		delete(cs.accountStates, account)
		delete(cs.loginDeadlines, account)
	}
	delete(cs.onlinePlayers, playerID)
	delete(cs.forceKickDeadlines, playerID)
	log.Printf("[central] force-kick timeout for player %d, state cleaned up", playerID)

	assignRsp, assignConn, needAssign := cs.TryContinuePendingAssign(account)
	if needAssign {
		common.SendMsg(assignConn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}
}
