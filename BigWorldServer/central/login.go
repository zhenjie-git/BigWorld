package main

import (
	"fmt"
	"log"
	"time"

	"bigworld/common"
)

const loginGatewayTimeout = 30 * time.Second
const forceKickTimeout = 10 * time.Second

// assignGatewayLocked picks a gateway, creates the account login state, and starts
// the gateway timeout. Returns the response to send to the login server.
// Must be called with cs.mu held. Caller sends the returned response outside the lock.
func (cs *centralServer) assignGatewayLocked(reqID uint64, account string) common.GatewayAssignRsp {
	var gatewayID, gatewayAddr string
	for id, rec := range cs.servers {
		if rec.info.ServerType == common.ServerGateway {
			gatewayID = id
			gatewayAddr = rec.info.ListenAddr
			break
		}
	}

	if gatewayAddr == "" {
		return common.GatewayAssignRsp{
			ReqId:   reqID,
			Success: false,
			Message: "没有可用的网关服",
		}
	}

	cs.accountStates[account] = &accountState{
		Account:   account,
		GatewayId: gatewayID,
		Status:    AccountLoggingIn,
	}

	cs.loginTimers[account] = time.AfterFunc(loginGatewayTimeout, func() {
		cs.handleLoginTimeout(account)
	})

	log.Printf("[central] assigned gateway %s: account=%s status=logging-in (timer %v)",
		gatewayID, account, loginGatewayTimeout)

	return common.GatewayAssignRsp{
		ReqId:       reqID,
		Success:     true,
		GatewayAddr: gatewayAddr,
		GatewayId:   gatewayID,
		Message:     fmt.Sprintf("assigned gateway %s", gatewayID),
	}
}

func (cs *centralServer) handleGatewayAssign(conn *common.ConnWrapper, req *common.GatewayAssignReq) {
	log.Printf("[central] gateway assign request: reqID=%d server=%s account=%s",
		req.ReqId, req.ServerId, req.Account)

	if cs.isShuttingDown() {
		common.SendMsg(conn, common.Ct2Lg_GatewayAssignRsp, &common.GatewayAssignRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "服务器正在关闭",
		})
		return
	}

	if req.Account == "" {
		common.SendMsg(conn, common.Ct2Lg_GatewayAssignRsp, &common.GatewayAssignRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "账号为空",
		})
		return
	}

	// 锁内只改状态、收集要发的消息;锁外再 Send,避免 conn.Send 阻塞卡住 cs.mu。
	var (
		kickConn   *common.ConnWrapper
		kickNotify common.ForceKickNotify
		needKick   bool
		assignRsp  common.GatewayAssignRsp
		needAssign bool
	)

	cs.mu.Lock()
	if state, exists := cs.accountStates[req.Account]; exists {
		switch state.Status {
		case AccountOnline:
			// 顶号: force-kick old player, then WAIT for cleanup before assigning a new gateway.
			oldPlayerId := state.PlayerId
			oldPlayer, oldExists := cs.onlinePlayers[oldPlayerId]
			if !oldExists {
				// Stale accountState without onlinePlayers entry - clean up and proceed directly.
				delete(cs.accountStates, req.Account)
				log.Printf("[central] stale accountState for %s (player %d not found), cleaned up", req.Account, oldPlayerId)
				assignRsp = cs.assignGatewayLocked(req.ReqId, req.Account)
				needAssign = true
			} else {
				log.Printf("[central] force-kicking existing player %d (account=%s), waiting for cleanup",
					oldPlayerId, req.Account)

				if rec, ok := cs.servers[oldPlayer.GatewayId]; ok {
					kickNotify = common.ForceKickNotify{
						PlayerId: oldPlayerId,
						Account:  req.Account,
						WorldId:  oldPlayer.WorldId,
					}
					kickConn = rec.conn
					needKick = true
					oldGatewayId := oldPlayer.GatewayId
					cs.forceKickTimers[oldPlayerId] = time.AfterFunc(forceKickTimeout, func() {
						cs.handleForceKickTimeout(oldPlayerId, oldGatewayId)
					})
					// Store the pending assign in the accountState itself.
					state.Status = AccountWaitingKick
					state.PendingReqId = req.ReqId
					state.PendingLoginConn = conn
					log.Printf("[central] pending assign stored in accountState for %s (waiting for player %d cleanup)",
						req.Account, oldPlayerId)
				} else {
					// Old gateway gone - clean up directly and continue immediately.
					delete(cs.onlinePlayers, oldPlayerId)
					delete(cs.accountStates, req.Account)
					log.Printf("[central] old gateway %s not found, cleaned up player %d directly", oldPlayer.GatewayId, oldPlayerId)
					assignRsp = cs.assignGatewayLocked(req.ReqId, req.Account)
					needAssign = true
				}
			}
		case AccountLoggingIn:
			assignRsp = common.GatewayAssignRsp{
				ReqId:   req.ReqId,
				Success: false,
				Message: "账号正在登录中",
			}
			needAssign = true
		case AccountWaitingKick:
			assignRsp = common.GatewayAssignRsp{
				ReqId:   req.ReqId,
				Success: false,
				Message: "账号正在其他设备登录",
			}
			needAssign = true
		}
	} else {
		// No existing account state - assign gateway directly.
		assignRsp = cs.assignGatewayLocked(req.ReqId, req.Account)
		needAssign = true
	}
	cs.mu.Unlock()

	// 锁外发送,慢对端最多阻塞自己这条连接,不卡 central 主锁。
	if needKick {
		common.SendMsg(kickConn, common.Ct2Gw_ForceKickNotify, &kickNotify)
		log.Printf("[central] sent ForceKickNotify for player %d", kickNotify.PlayerId)
	}
	if needAssign {
		common.SendMsg(conn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}
}

// tryContinuePendingAssign is called after an old player is cleaned up during 顶号.
// If the accountState is in AccountWaitingKick, it picks up the pending request
// and assigns a new gateway. Returns the response and target connection.
// Must be called with cs.mu held. Caller sends outside the lock.
func (cs *centralServer) tryContinuePendingAssign(account string) (common.GatewayAssignRsp, *common.ConnWrapper, bool) {
	state, exists := cs.accountStates[account]
	if !exists || state.Status != AccountWaitingKick {
		return common.GatewayAssignRsp{}, nil, false
	}

	reqID := state.PendingReqId
	loginConn := state.PendingLoginConn

	// Clear old accountState - assignGatewayLocked will create a fresh one.
	delete(cs.accountStates, account)

	log.Printf("[central] continuing pending assign for account %s after cleanup", account)
	return cs.assignGatewayLocked(reqID, account), loginConn, true
}

// handleLoginTimeout cleans up the logging-in account state when the client fails
// to connect to the assigned gateway within the timeout window.
func (cs *centralServer) handleLoginTimeout(account string) {
	cs.mu.Lock()
	defer cs.mu.Unlock()

	state, exists := cs.accountStates[account]
	if !exists || state.Status != AccountLoggingIn {
		return // already handled or status changed
	}

	delete(cs.accountStates, account)
	delete(cs.loginTimers, account)

	log.Printf("[central] login gateway timeout for account=%s, state cleaned up", account)
}
