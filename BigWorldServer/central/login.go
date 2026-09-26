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

func (cs *centralServer) AssignGateway(reqID uint64, account string, preferredWorldID, preferredSceneID string) common.GatewayAssignRsp {
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
		GatewayID: gatewayID,
		WorldID:   preferredWorldID,
		SceneID:   preferredSceneID,
		Status:    AccountLoggingIn,
	}

	cs.loginDeadlines[account] = time.Now().Add(loginGatewayTimeout)

	log.Printf("[central] assigned gateway %s: account=%s status=logging-in world=%s scene=%s (timer %v)",
		gatewayID, account, preferredWorldID, preferredSceneID, loginGatewayTimeout)

	return common.GatewayAssignRsp{
		ReqId:       reqID,
		Success:     true,
		GatewayAddr: gatewayAddr,
		GatewayId:   gatewayID,
		Message:     fmt.Sprintf("assigned gateway %s", gatewayID),
	}
}
func (cs *centralServer) HandleGatewayAssignReq(conn *common.ConnWrapper, req *common.GatewayAssignReq) {
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
		case AccountTransferring:
			assignRsp = common.GatewayAssignRsp{
				ReqId:   req.ReqId,
				Success: false,
				Message: "transfer in progress",
			}
			needAssign = true
		case AccountOnline:
			state.SceneID = req.SceneId
			oldPlayerID := state.PlayerID
			oldPlayer, oldExists := cs.onlinePlayers[oldPlayerID]
			if !oldExists {

				delete(cs.accountStates, req.Account)
				log.Printf("[central] stale accountState for %s (player %d not found), cleaned up", req.Account, oldPlayerID)
				assignRsp = cs.AssignGateway(req.ReqId, req.Account, "", req.SceneId)
				needAssign = true
			} else {
				log.Printf("[central] force-kicking existing player %d (account=%s), waiting for cleanup",
					oldPlayerID, req.Account)

				if rec, ok := cs.servers[oldPlayer.GatewayID]; ok {
					kickNotify = common.ForceKickNotify{
						PlayerId:  oldPlayerID,
						Account:   req.Account,
						WorldId:   oldPlayer.WorldID,
						SessionId: oldPlayer.SessionID,
					}
					kickConn = rec.conn
					needKick = true
					cs.forceKickDeadlines[oldPlayerID] = forceKickDeadline{
						gatewayID: oldPlayer.GatewayID,
						sessionID: oldPlayer.SessionID,
						account:   req.Account,
						deadline:  time.Now().Add(forceKickTimeout),
					}

					state.Status = AccountWaitingKick
					state.PendingReqID = req.ReqId
					state.PendingLoginConn = conn
					log.Printf("[central] pending assign stored in accountState for %s (waiting for player %d cleanup)",
						req.Account, oldPlayerID)
				} else {

					delete(cs.onlinePlayers, oldPlayerID)
					delete(cs.accountStates, req.Account)
					log.Printf("[central] old gateway %s not found, cleaned up player %d directly", oldPlayer.GatewayID, oldPlayerID)
					assignRsp = cs.AssignGateway(req.ReqId, req.Account, "", req.SceneId)
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

		assignRsp = cs.AssignGateway(req.ReqId, req.Account, "", req.SceneId)
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

	reqID := state.PendingReqID
	loginConn := state.PendingLoginConn
	preferredWorldID := state.WorldID
	preferredSceneID := state.SceneID

	delete(cs.accountStates, account)

	log.Printf("[central] continuing pending assign for account %s after cleanup", account)
	return cs.AssignGateway(reqID, account, preferredWorldID, preferredSceneID), loginConn, true
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
