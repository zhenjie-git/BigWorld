package main

import (
	"log"
	"time"

	"bigworld/common"
)

func (cs *centralServer) HandlePeerHello(conn *common.ConnWrapper, req *common.HelloReq) *common.HelloRsp {
	if req.ServerId == "" || req.ListenAddr == "" {
		return &common.HelloRsp{Success: false, Message: "empty server id or listen addr"}
	}

	log.Printf("[central] hello request: type=%s id=%s addr=%s", req.ServerType, req.ServerId, req.ListenAddr)

	if old, ok := cs.servers[req.ServerId]; ok && old.conn != nil && old.conn != conn {
		delete(cs.connToID, old.conn)
		_ = old.conn.Close()
	}

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

	log.Printf("[central] server registered: %s (%s) at %s",
		req.ServerId, req.ServerType, req.ListenAddr)

	if req.ServerType == common.ServerWorld {
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
	}

	if req.ServerType == common.ServerDbProxy {
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
	}

	return &common.HelloRsp{Success: true, Message: "registered", Registered: true}
}
func (cs *centralServer) HandleHeartbeat(conn *common.ConnWrapper, req *common.HeartbeatReq) {
	if id := cs.connToID[conn]; id == "" || id != req.ServerId {
		return
	}
	if record, ok := cs.servers[req.ServerId]; ok {
		record.lastHB = time.Now().Unix()
	}

	rsp := common.HeartbeatRsp{Success: true}
	common.SendMsg(conn, common.Ct2Srv_HeartbeatRsp, &rsp)
}

func (cs *centralServer) HandleServerList(conn *common.ConnWrapper, req *common.ServerListReq) {
	var entries []*common.ServerEntry
	for _, rec := range cs.servers {
		if req.Type == 0 || rec.info.ServerType == req.Type {
			entries = append(entries, &rec.info)
		}
	}

	rsp := common.ServerListRsp{Servers: entries}
	common.SendMsg(conn, common.Ct2Srv_ServerListRsp, &rsp)
}

func (cs *centralServer) HandleServerDisconnect(conn *common.ConnWrapper) {
	id, ok := cs.connToID[conn]
	if !ok {
		return
	}
	delete(cs.connToID, conn)
	if rec, ok := cs.servers[id]; ok && rec.conn == conn {
		cs.CleanupServer(id)
	}
}
