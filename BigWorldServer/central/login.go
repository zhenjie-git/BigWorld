package main

import (
	"fmt"
	"log"
	"time"

	"bigworld/common"
)

const loginGatewayTimeout = 10 * time.Second
const loginFinishTimeout = 30 * time.Second
const forceKickTimeout = 10 * time.Second

func (cs *centralServer) AssignGatewayLocked(reqID uint64, account string, preferredWorldID string) common.GatewayAssignRsp {
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
			Message: "no gateway available",
		}
	}

	cs.accountStates[account] = &accountState{
		Account:   account,
		GatewayId: gatewayID,
		WorldId:   preferredWorldID,
		Status:    AccountLoggingIn,
	}

	cs.loginDeadlines[account] = time.Now().Add(loginGatewayTimeout)

	log.Printf("[central] assigned gateway %s: account=%s status=logging-in world=%s (timer %v)",
		gatewayID, account, preferredWorldID, loginGatewayTimeout)

	return common.GatewayAssignRsp{
		ReqId:       reqID,
		Success:     true,
		GatewayAddr: gatewayAddr,
		GatewayId:   gatewayID,
		Message:     fmt.Sprintf("assigned gateway %s", gatewayID),
	}
}
func (cs *centralServer) pickWorldLocked(preferred string) (string, string) {
	if preferred != "" {
		if rec, ok := cs.servers[preferred]; ok && rec.info.ServerType == common.ServerWorld {
			return preferred, rec.info.ListenAddr
		}
	}
	for id, rec := range cs.servers {
		if rec.info.ServerType == common.ServerWorld {
			return id, rec.info.ListenAddr
		}
	}
	return "", ""
}
func (cs *centralServer) HandleGatewayAssign(conn *common.ConnWrapper, req *common.GatewayAssignReq) {
	log.Printf("[central] gateway assign request: reqID=%d server=%s account=%s",
		req.ReqId, req.ServerId, req.Account)

	if cs.IsShuttingDown() {
		common.SendMsg(conn, common.Ct2Lg_GatewayAssignRsp, &common.GatewayAssignRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "server shutting down",
		})
		return
	}

	if req.Account == "" {
		common.SendMsg(conn, common.Ct2Lg_GatewayAssignRsp, &common.GatewayAssignRsp{
			ReqId:   req.ReqId,
			Success: false,
			Message: "璐﹀彿涓虹┖",
		})
		return
	}

	var (
		kickConn   *common.ConnWrapper
		kickNotify common.ForceKickNotify
		needKick   bool
		assignRsp  common.GatewayAssignRsp
		needAssign bool
	)

	if state, exists := cs.accountStates[req.Account]; exists {
		switch state.Status {
		case AccountOnline:

			oldPlayerId := state.PlayerId
			oldPlayer, oldExists := cs.onlinePlayers[oldPlayerId]
			if !oldExists {

				delete(cs.accountStates, req.Account)
				log.Printf("[central] stale accountState for %s (player %d not found), cleaned up", req.Account, oldPlayerId)
				assignRsp = cs.AssignGatewayLocked(req.ReqId, req.Account, "")
				needAssign = true
			} else {
				log.Printf("[central] force-kicking existing player %d (account=%s), waiting for cleanup",
					oldPlayerId, req.Account)

				if rec, ok := cs.servers[oldPlayer.GatewayId]; ok {
					kickNotify = common.ForceKickNotify{
						PlayerId:  oldPlayerId,
						Account:   req.Account,
						WorldId:   oldPlayer.WorldId,
						SessionId: oldPlayer.SessionId,
					}
					kickConn = rec.conn
					needKick = true
					cs.forceKickDeadlines[oldPlayerId] = forceKickDeadline{
						gatewayId: oldPlayer.GatewayId,
						sessionId: oldPlayer.SessionId,
						account:   req.Account,
						deadline:  time.Now().Add(forceKickTimeout),
					}

					state.Status = AccountWaitingKick
					state.PendingReqId = req.ReqId
					state.PendingLoginConn = conn
					log.Printf("[central] pending assign stored in accountState for %s (waiting for player %d cleanup)",
						req.Account, oldPlayerId)
				} else {

					delete(cs.onlinePlayers, oldPlayerId)
					delete(cs.accountStates, req.Account)
					log.Printf("[central] old gateway %s not found, cleaned up player %d directly", oldPlayer.GatewayId, oldPlayerId)
					assignRsp = cs.AssignGatewayLocked(req.ReqId, req.Account, "")
					needAssign = true
				}
			}
		case AccountLoggingIn:
			assignRsp = common.GatewayAssignRsp{
				ReqId:   req.ReqId,
				Success: false,
				Message: "account logging in",
			}
			needAssign = true
		case AccountWaitingKick:
			assignRsp = common.GatewayAssignRsp{
				ReqId:   req.ReqId,
				Success: false,
				Message: "璐﹀彿姝ｅ湪鍏朵粬璁惧鐧诲綍",
			}
			needAssign = true
		}
	} else {

		assignRsp = cs.AssignGatewayLocked(req.ReqId, req.Account, "")
		needAssign = true
	}

	if needKick {
		common.SendMsg(kickConn, common.Ct2Gw_ForceKickNotify, &kickNotify)
		log.Printf("[central] sent ForceKickNotify for player %d", kickNotify.PlayerId)
	}
	if needAssign {
		common.SendMsg(conn, common.Ct2Lg_GatewayAssignRsp, &assignRsp)
	}
}

func (cs *centralServer) TryContinuePendingAssign(account string) (common.GatewayAssignRsp, *common.ConnWrapper, bool) {
	state, exists := cs.accountStates[account]
	if !exists || state.Status != AccountWaitingKick {
		return common.GatewayAssignRsp{}, nil, false
	}

	reqID := state.PendingReqId
	loginConn := state.PendingLoginConn
	preferredWorldID := state.WorldId

	delete(cs.accountStates, account)

	log.Printf("[central] continuing pending assign for account %s after cleanup", account)
	return cs.AssignGatewayLocked(reqID, account, preferredWorldID), loginConn, true
}

func (cs *centralServer) HandleLoginTimeout(account string) {

	state, exists := cs.accountStates[account]
	if !exists || state.Status != AccountLoggingIn {
		return
	}

	delete(cs.accountStates, account)
	delete(cs.loginDeadlines, account)

	log.Printf("[central] login gateway timeout for account=%s, state cleaned up", account)
}
