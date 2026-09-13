package main

import (
	"log"

	"bigworld/common"
)

type dbProxyServer struct {
	*common.ServerBase
	db *playerDB
}

func NewDbProxyServer(id string, db *playerDB) *dbProxyServer {
	s := &dbProxyServer{
		ServerBase: common.NewServerBase(common.ServerDbProxy, id),
		db:         db,
	}

	common.Register(s.Router(common.SrcLogin), common.Lg2Db_ValidateAccountReq, s.HandleValidateAccount)
	common.Register(s.Router(common.SrcWorld), common.Wd2Db_LoadPlayerReq, s.HandleLoadPlayer)
	common.Register(s.Router(common.SrcWorld), common.Wd2Db_SavePlayerReq, s.HandleSavePlayer)

	cr := s.Router(common.SrcCentral)
	common.Register(cr, common.Srv2Srv_HelloRsp, s.HandleCentralHelloRsp)
	common.Register(cr, common.Ct2Srv_ShutdownNotify, s.HandleCentralShutdownNotify)
	common.Register(cr, common.Ct2Srv_HeartbeatRsp, s.NoopHeartbeat)
	return s
}

func (s *dbProxyServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (s *dbProxyServer) HandleCentralHelloRsp(_ *common.ConnWrapper, rsp *common.HelloRsp) {
	if !rsp.Success {
		log.Printf("[dbproxy %s] hello rejected: %s", s.ServerId, rsp.Message)
		return
	}
	if rsp.Success {
		log.Printf("[dbproxy %s] registered: %s", s.ServerId, rsp.Message)
	}
}

func (s *dbProxyServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	log.Printf("[dbproxy %s] shutdown requested: %s", s.ServerId, notify.Reason)
	s.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: s.ServerId})
	go s.Stop()
}

func (s *dbProxyServer) HandleValidateAccount(conn *common.ConnWrapper, req *common.ValidateAccountReq) {

	go func() {
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
		s.Loop.Defer(func() {
			common.SendMsg(conn, common.Db2Lg_ValidateAccountRsp, &rsp)
		})
	}()
}

func (s *dbProxyServer) HandleLoadPlayer(conn *common.ConnWrapper, req *common.LoadPlayerReq) {
	go func() {
		rsp := common.LoadPlayerRsp{Account: req.Account}
		found, pid, x, z, scene, err := s.db.LoadPlayer(req.Account)
		if err != nil {
			rsp.Message = "数据库错误"
			log.Printf("[dbproxy %s] load player account=%s err: %v", s.ServerId, req.Account, err)
		} else {
			rsp.Found = found
			rsp.PlayerId = pid
			rsp.X = x
			rsp.Z = z
			rsp.SceneId = scene
		}
		s.Loop.Defer(func() {
			common.SendMsg(conn, common.Db2Wd_LoadPlayerRsp, &rsp)
		})
	}()
}

func (s *dbProxyServer) HandleSavePlayer(conn *common.ConnWrapper, req *common.SavePlayerReq) {

	go func() {
		rsp := common.SavePlayerRsp{ReqId: req.ReqId}
		if err := s.db.SavePlayers(req.Players); err != nil {
			rsp.Message = "数据库错误"
			log.Printf("[dbproxy %s] save %d players err: %v", s.ServerId, len(req.Players), err)
		} else {
			rsp.Success = true
			rsp.Message = "ok"
		}
		s.Loop.Defer(func() {
			common.SendMsg(conn, common.Db2Wd_SavePlayerRsp, &rsp)
		})
	}()
}
