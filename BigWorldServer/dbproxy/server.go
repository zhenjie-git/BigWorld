package main

import (
	"log"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

// dbProxyServer is the persistence process. It owns the MySQL connection pool
// and exposes load/save/validate operations to world and login over TCP.
// Only one instance is expected; consumers discover it via central.
type dbProxyServer struct {
	*common.ServerBase
	db         *playerDB
	peerRouter *common.MessageRouter
}

func newDbProxyServer(id string, db *playerDB) *dbProxyServer {
	s := &dbProxyServer{
		ServerBase: common.NewServerBase(common.ServerDbProxy, id),
		db:         db,
		peerRouter: common.NewMessageRouter(),
	}

	common.Register(s.peerRouter, common.Lg2Db_ValidateAccountReq, s.handleValidateAccount)
	common.Register(s.peerRouter, common.Wd2Db_LoadPlayerReq, s.handleLoadPlayer)
	common.Register(s.peerRouter, common.Wd2Db_SavePlayerReq, s.handleSavePlayer)

	s.OnMessage = s.handleMessage
	s.OnCentralMessage = s.handleCentralMessage
	return s
}

func (s *dbProxyServer) handleMessage(conn *common.ConnWrapper, msg common.Message) {
	s.peerRouter.Dispatch(conn, msg)
}

func (s *dbProxyServer) handleCentralMessage(msg common.Message) {
	switch msg.Type {
	case common.Ct2Srv_RegisterRsp:
		var rsp common.RegisterRsp
		if err := proto.Unmarshal(msg.Data, &rsp); err != nil {
			log.Printf("[dbproxy %s] bad RegisterRsp: %v", s.ServerId, err)
			return
		}
		if rsp.Success {
			log.Printf("[dbproxy %s] registered: %s", s.ServerId, rsp.Message)
		}
	case common.Ct2Srv_ShutdownNotify:
		var notify common.ShutdownNotify
		if err := proto.Unmarshal(msg.Data, &notify); err != nil {
			log.Printf("[dbproxy %s] bad ShutdownNotify: %v", s.ServerId, err)
			return
		}
		log.Printf("[dbproxy %s] shutdown requested: %s", s.ServerId, notify.Reason)
		s.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: s.ServerId})
		go s.Stop()
	case common.Ct2Srv_HeartbeatRsp:
	}
}

// handleValidateAccount answers login's credential check against the accounts table.
func (s *dbProxyServer) handleValidateAccount(conn *common.ConnWrapper, req *common.ValidateAccountReq) {
	rsp := common.ValidateAccountRsp{ReqId: req.ReqId}
	valid, err := s.db.ValidateAccount(req.Account, req.Password)
	if err != nil {
		rsp.Message = "数据库错误"
		log.Printf("[dbproxy %s] validate account=%s err: %v", s.ServerId, req.Account, err)
	} else {
		rsp.Valid = valid
		if valid {
			rsp.Message = "ok"
		} else {
			rsp.Message = "账号或密码错误"
		}
	}
	common.SendMsg(conn, common.Db2Lg_ValidateAccountRsp, &rsp)
}

// handleLoadPlayer returns a player's saved state, allocating a stable
// player_id for first-time accounts.
func (s *dbProxyServer) handleLoadPlayer(conn *common.ConnWrapper, req *common.LoadPlayerReq) {
	rsp := common.LoadPlayerRsp{Account: req.Account}
	found, pid, x, z, err := s.db.LoadPlayer(req.Account)
	if err != nil {
		rsp.Message = "数据库错误"
		log.Printf("[dbproxy %s] load player account=%s err: %v", s.ServerId, req.Account, err)
	} else {
		rsp.Found = found
		rsp.PlayerId = pid
		rsp.X = x
		rsp.Z = z
	}
	common.SendMsg(conn, common.Db2Wd_LoadPlayerRsp, &rsp)
}

// handleSavePlayer upserts player positions (single on destroy, batch on autosave).
func (s *dbProxyServer) handleSavePlayer(conn *common.ConnWrapper, req *common.SavePlayerReq) {
	// Echo req_id so world's shutdown flush can correlate its reply. SavePlayers
	// is synchronous (transaction commits before the reply is sent).
	rsp := common.SavePlayerRsp{ReqId: req.ReqId}
	if err := s.db.SavePlayers(req.Players); err != nil {
		rsp.Message = "数据库错误"
		log.Printf("[dbproxy %s] save %d players err: %v", s.ServerId, len(req.Players), err)
	} else {
		rsp.Success = true
		rsp.Message = "ok"
	}
	common.SendMsg(conn, common.Db2Wd_SavePlayerRsp, &rsp)
}
