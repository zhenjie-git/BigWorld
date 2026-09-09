package main

import (
	"fmt"
	"log"

	"bigworld/common"
)

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

	if _, ok := cs.loginDeadlines[req.Account]; ok {
		delete(cs.loginDeadlines, req.Account)
	} else {
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
		log.Printf("[central] account %s has unexpected state, rejecting login prepare", req.Account)
		rsp := common.LoginPrepareRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "账号状态异常",
		}
		common.SendMsg(conn, common.Ct2Gw_LoginPrepareRsp, &rsp)
		return
	}

	var worldID, worldAddr string
	for id, rec := range cs.servers {
		if rec.info.ServerType == common.ServerWorld {
			worldID = id
			worldAddr = rec.info.ListenAddr
			break
		}
	}

	if worldID == "" {
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

func (cs *centralServer) HandleLoginFinishReq(conn *common.ConnWrapper, req *common.LoginFinishReq) {
	log.Printf("[central] login finish request: account=%s player=%d success=%v",
		req.Account, req.PlayerId, req.Success)

	state, exists := cs.accountStates[req.Account]

	rsp := common.LoginFinishRsp{
		PlayerId: req.PlayerId,
		Account:  req.Account,
	}

	if !exists || state.Status != AccountLoggingIn {
		log.Printf("[central] login finish for unexpected account %s (state=%v)", req.Account, state)
		rsp.Success = false
		rsp.Message = "账号状态异常"
		common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
		return
	}

	if req.Success {
		if req.PlayerId == 0 {
			log.Printf("[central] login finish success but playerID is 0 for account %s", req.Account)
			rsp.Success = false
			rsp.Message = "实体创建未返回有效ID"
			common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
			return
		}

		state.Status = AccountOnline
		state.PlayerId = req.PlayerId
		state.WorldId = req.WorldId

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

		delete(cs.accountStates, req.Account)
		delete(cs.loginDeadlines, req.Account)
		rsp.Success = false
		rsp.Message = fmt.Sprintf("登录失败: %s", req.Message)
		log.Printf("[central] account %s login rolled back: %s", req.Account, req.Message)
	}

	common.SendMsg(conn, common.Ct2Gw_LoginFinishRsp, &rsp)
}

func (cs *centralServer) HandleLogoutBeginReq(conn *common.ConnWrapper, req *common.LogoutBeginReq) {

	if _, ok := cs.onlinePlayers[req.PlayerId]; ok {
		log.Printf("[central] player %d marked as logging out", req.PlayerId)
	}

	rsp := common.LogoutBeginRsp{
		Success:  true,
		PlayerId: req.PlayerId,
		Message:  "登出开始",
	}
	common.SendMsg(conn, common.Ct2Gw_LogoutBeginRsp, &rsp)
	log.Printf("[central] logout begin processed for player %d", req.PlayerId)
}

func (cs *centralServer) HandleLogoutCleanupReq(conn *common.ConnWrapper, req *common.LogoutCleanupReq) {
	var (
		account    string
		ok         bool
		assignRsp  common.GatewayAssignRsp
		assignConn *common.ConnWrapper
		needAssign bool
	)

	player, exists := cs.onlinePlayers[req.PlayerId]
	if exists {
		account = player.Account
		if player.GatewayId == req.GatewayId {

			if state, stExists := cs.accountStates[account]; stExists && state.PlayerId == req.PlayerId && state.Status != AccountWaitingKick {
				delete(cs.accountStates, account)
			}
			delete(cs.onlinePlayers, req.PlayerId)
			ok = true
		} else {

			log.Printf("[central] ignoring stale cleanup for player %d (gateway %s != current %s)",
				req.PlayerId, req.GatewayId, player.GatewayId)
		}
	}

	delete(cs.forceKickDeadlines, req.PlayerId)

	if account != "" {
		assignRsp, assignConn, needAssign = cs.TryContinuePendingAssign(account)
	}

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

func (cs *centralServer) HandleForceKickTimeout(playerID uint64, kickedGatewayId string) {
	var (
		assignRsp  common.GatewayAssignRsp
		assignConn *common.ConnWrapper
		needAssign bool
	)

	player, exists := cs.onlinePlayers[playerID]
	if !exists {
		return
	}

	if player.GatewayId != kickedGatewayId {
		log.Printf("[central] force-kick timeout for player %d ignored (gateway changed to %s)",
			playerID, player.GatewayId)
		return
	}

	account := player.Account

	if state, ok := cs.accountStates[account]; ok && state.PlayerId == playerID && state.Status != AccountWaitingKick {
		delete(cs.accountStates, account)
	}
	delete(cs.onlinePlayers, playerID)
	delete(cs.forceKickDeadlines, playerID)
	log.Printf("[central] force-kick timeout for player %d, state cleaned up", playerID)

	assignRsp, assignConn, needAssign = cs.TryContinuePendingAssign(account)

	if needAssign {
		common.SendMsg(assignConn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}
}
