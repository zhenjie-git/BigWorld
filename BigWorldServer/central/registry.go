package main

import (
	"log"
	"time"

	"bigworld/common"
)

func (cs *centralServer) handleRegister(conn *common.ConnWrapper, req *common.RegisterReq) {
	log.Printf("[central] register request: type=%s id=%s addr=%s",
		req.ServerType, req.ServerId, req.ListenAddr)

	cs.mu.Lock()
	cs.servers[req.ServerId] = &serverRecord{
		info: common.ServerEntry{
			ServerType: req.ServerType,
			ServerId:   req.ServerId,
			ListenAddr: req.ListenAddr,
		},
		conn:   conn,
		lastHB: time.Now().Unix(),
	}
	cs.connToID[conn] = req.ServerId
	cs.mu.Unlock()

	rsp := common.RegisterRsp{Success: true, Message: "registered"}
	common.SendMsg(conn, common.Ct2Srv_RegisterRsp, &rsp)

	log.Printf("[central] server registered: %s (%s) at %s",
		req.ServerId, req.ServerType, req.ListenAddr)

	// If a world server registered, notify all gateways to pre-connect.
	if req.ServerType == common.ServerWorld {
		cs.mu.RLock()
		entry := common.ServerEntry{
			ServerType: req.ServerType,
			ServerId:   req.ServerId,
			ListenAddr: req.ListenAddr,
		}
		notifyData, _ := common.MarshalHelper(&entry)
		for _, rec := range cs.servers {
			if rec.info.ServerType == common.ServerGateway {
				rec.conn.Send(common.Message{Type: common.Ct2Gw_NewWorldNotify, Data: notifyData})
			}
		}
		cs.mu.RUnlock()
	}

	// If a dbproxy registered, notify worlds and logins so they pre-connect
	// to the persistence layer (world for load/save, login for auth).
	if req.ServerType == common.ServerDbProxy {
		cs.mu.RLock()
		entry := common.ServerEntry{
			ServerType: req.ServerType,
			ServerId:   req.ServerId,
			ListenAddr: req.ListenAddr,
		}
		notifyData, _ := common.MarshalHelper(&entry)
		for _, rec := range cs.servers {
			if rec.info.ServerType == common.ServerWorld || rec.info.ServerType == common.ServerLogin {
				rec.conn.Send(common.Message{Type: common.Ct2Srv_NewDbProxyNotify, Data: notifyData})
			}
		}
		cs.mu.RUnlock()
	}
}

func (cs *centralServer) handleHeartbeat(conn *common.ConnWrapper, req *common.HeartbeatReq) {
	cs.mu.Lock()
	if record, ok := cs.servers[req.ServerId]; ok {
		record.lastHB = time.Now().Unix()
	}
	cs.mu.Unlock()

	rsp := common.HeartbeatRsp{Success: true}
	common.SendMsg(conn, common.Ct2Srv_HeartbeatRsp, &rsp)
}

func (cs *centralServer) handleServerList(conn *common.ConnWrapper, req *common.ServerListReq) {
	cs.mu.RLock()
	var entries []*common.ServerEntry
	for _, rec := range cs.servers {
		if req.Type == 0 || rec.info.ServerType == req.Type {
			entries = append(entries, &rec.info)
		}
	}
	cs.mu.RUnlock()

	rsp := common.ServerListRsp{Servers: entries}
	common.SendMsg(conn, common.Ct2Srv_ServerListRsp, &rsp)
}
