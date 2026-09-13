package main

import (
	"fmt"
	"log"
	"net"
	"sync"
	"time"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

const (
	gatewayAssignTimeout = 10 * time.Second
	validateTimeout      = 5 * time.Second
)

type gatewayPending struct {
	conn     *common.ConnWrapper
	req      *common.LoginReq
	reqID    uint64
	deadline time.Time
}

type validatePending struct {
	conn     *common.ConnWrapper
	req      *common.LoginReq
	reqID    uint64
	deadline time.Time
}

type loginServer struct {
	*common.ServerBase

	reqCounter       uint64
	pendings         map[uint64]*gatewayPending
	validatePendings map[uint64]*validatePending

	dbConn       *common.ConnWrapper
	dbID         string
	dbAddr       string
	dbConnecting bool
	dbMu         sync.Mutex
}

func NewLoginServer(id string) *loginServer {
	ls := &loginServer{
		ServerBase:       common.NewServerBase(common.ServerLogin, id),
		pendings:         make(map[uint64]*gatewayPending),
		validatePendings: make(map[uint64]*validatePending),
	}

	common.Register(ls.Router(common.SrcClient), common.Cli2Lg_LoginReq, ls.HandleAccountLogin)

	cr := ls.Router(common.SrcCentral)
	common.Register(cr, common.Ct2Lg_GatewayAssignRsp, ls.HandleGatewayAssignRsp)
	common.Register(cr, common.Srv2Srv_HelloRsp, ls.HandleCentralHelloRsp)
	common.Register(cr, common.Ct2Srv_ServerListRsp, ls.HandleCentralServerListRsp)
	common.Register(cr, common.Ct2Srv_NewDbProxyNotify, ls.HandleCentralNewDbProxy)
	common.Register(cr, common.Ct2Srv_ShutdownNotify, ls.HandleCentralShutdownNotify)
	common.Register(cr, common.Ct2Srv_HeartbeatRsp, ls.NoopHeartbeat)

	common.Register(ls.Router(common.SrcDbProxy), common.Db2Lg_ValidateAccountRsp, ls.HandleValidateAccountRsp)

	ls.Loop.OnTick = ls.OnTick
	ls.Loop.TickEvery = time.Second
	return ls
}

func (ls *loginServer) OnTick() {
	now := time.Now()
	for reqID, pending := range ls.validatePendings {
		if now.After(pending.deadline) {
			ls.HandleValidateTimeout(reqID)
		}
	}
	for reqID, pending := range ls.pendings {
		if now.After(pending.deadline) {
			ls.HandleGatewayAssignTimeout(reqID)
		}
	}
}

func (ls *loginServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (ls *loginServer) HandleAccountLogin(conn *common.ConnWrapper, req *common.LoginReq) {
	log.Printf("[login] login attempt: account=%s", req.Account)

	if ls.dbConn == nil {
		rsp := common.LoginRsp{Success: false, Message: "登录服务不可用，请稍后重试"}
		common.SendMsg(conn, common.Lg2Cli_LoginRsp, &rsp)
		return
	}

	ls.reqCounter++
	reqID := ls.reqCounter
	pending := &validatePending{conn: conn, req: req, reqID: reqID, deadline: time.Now().Add(validateTimeout)}
	ls.validatePendings[reqID] = pending

	vReq := common.ValidateAccountReq{ReqId: reqID, Account: req.Account, Password: req.Password}
	if err := ls.ForwardMsgToDb(common.Lg2Db_ValidateAccountReq, &vReq); err != nil {
		delete(ls.validatePendings, reqID)
		rsp := common.LoginRsp{Success: false, Message: "登录服务不可用，请稍后重试"}
		common.SendMsg(conn, common.Lg2Cli_LoginRsp, &rsp)
		return
	}
	log.Printf("[login] validating account=%s via dbproxy (reqID=%d)", req.Account, reqID)
}

func (ls *loginServer) HandleValidateAccountRsp(_ *common.ConnWrapper, rsp *common.ValidateAccountRsp) {
	pending, ok := ls.validatePendings[rsp.ReqId]
	if ok {
		delete(ls.validatePendings, rsp.ReqId)
	}
	if !ok {
		return
	}

	if !rsp.Valid {
		msg := rsp.Message
		if msg == "" {
			msg = "账号或密码错误"
		}
		common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &common.LoginRsp{Success: false, Message: msg})
		return
	}

	ls.reqCounter++
	gwReqID := ls.reqCounter
	gwPending := &gatewayPending{
		conn:     pending.conn,
		req:      pending.req,
		reqID:    gwReqID,
		deadline: time.Now().Add(gatewayAssignTimeout),
	}
	ls.pendings[gwReqID] = gwPending

	assignReq := common.GatewayAssignReq{ReqId: gwReqID, ServerId: ls.ServerId, Account: pending.req.Account}
	ls.SendToCentralMsg(common.Lg2Ct_GatewayAssignReq, &assignReq)
	log.Printf("[login] account=%s validated, requested gateway assignment (reqID=%d)", pending.req.Account, gwReqID)
}

func (ls *loginServer) HandleValidateTimeout(reqID uint64) {
	pending, ok := ls.validatePendings[reqID]
	if ok {
		delete(ls.validatePendings, reqID)
	}
	if !ok {
		return
	}
	log.Printf("[login] validate timeout (reqID=%d) for account=%s", reqID, pending.req.Account)
	rsp := common.LoginRsp{Success: false, Message: "登录超时，请重试"}
	common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &rsp)
}

func (ls *loginServer) HandleGatewayAssignRsp(_ *common.ConnWrapper, rsp *common.GatewayAssignRsp) {
	pending, ok := ls.pendings[rsp.ReqId]
	if ok {
		delete(ls.pendings, rsp.ReqId)
	}

	if !ok {
		return
	}

	if !rsp.Success {
		loginRsp := common.LoginRsp{Success: false, Message: rsp.Message}
		common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &loginRsp)
		return
	}

	token, err := common.GenerateToken(pending.req.Account)
	if err != nil {
		log.Printf("[login] generate token failed: %v", err)
		common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &common.LoginRsp{Success: false, Message: "生成令牌失败"})
		return
	}
	loginRsp := common.LoginRsp{
		Success:     true,
		Message:     "登录成功",
		Token:       token,
		GatewayAddr: rsp.GatewayAddr,
	}
	common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &loginRsp)
	log.Printf("[login] login success: account=%s gateway=%s", pending.req.Account, rsp.GatewayAddr)
}

func (ls *loginServer) HandleGatewayAssignTimeout(reqID uint64) {
	pending, ok := ls.pendings[reqID]
	if ok {
		delete(ls.pendings, reqID)
	}

	if !ok {
		return
	}
	log.Printf("[login] gateway assign timeout (reqID=%d) for account=%s", reqID, pending.req.Account)
	rsp := common.LoginRsp{Success: false, Message: "登录超时，请重试"}
	common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &rsp)
}

func (ls *loginServer) HandleCentralHelloRsp(_ *common.ConnWrapper, rsp *common.HelloRsp) {
	if !rsp.Success {
		log.Printf("[login %s] hello rejected: %s", ls.ServerId, rsp.Message)
		return
	}
	if rsp.Success {
		log.Printf("[login %s] registered: %s", ls.ServerId, rsp.Message)

		ls.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerDbProxy})
	}
}

func (ls *loginServer) HandleCentralServerListRsp(_ *common.ConnWrapper, rsp *common.ServerListRsp) {
	for _, e := range rsp.Servers {
		ls.ConnectToDb(e.ServerId, e.ListenAddr)
	}
}

func (ls *loginServer) HandleCentralNewDbProxy(_ *common.ConnWrapper, entry *common.ServerEntry) {
	ls.ConnectToDb(entry.ServerId, entry.ListenAddr)
}

func (ls *loginServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	log.Printf("[login %s] shutdown requested: %s", ls.ServerId, notify.Reason)
	ls.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ls.ServerId})
	go ls.Stop()
}

func (ls *loginServer) ForwardMsgToDb(msgType common.MessageType, m proto.Message) error {
	data, err := common.MarshalHelper(m)
	if err != nil {
		return err
	}
	if ls.dbConn == nil {
		return fmt.Errorf("dbproxy not connected")
	}
	return ls.dbConn.Send(common.Message{Type: msgType, Data: data})
}

func (ls *loginServer) ConnectToDb(dbID, dbAddr string) {
	ls.dbMu.Lock()
	ls.dbID = dbID
	ls.dbAddr = dbAddr
	if ls.dbConn != nil || ls.dbConnecting {
		ls.dbMu.Unlock()
		return
	}
	ls.dbConnecting = true
	ls.dbMu.Unlock()

	go ls.DbConnectLoop()
}

func (ls *loginServer) DbConnectLoop() {
	backoff := time.Second
	for {
		select {
		case <-ls.Done():
			ls.setDBConnecting(false)
			return
		default:
		}

		ls.dbMu.Lock()
		dbID, dbAddr := ls.dbID, ls.dbAddr
		ls.dbMu.Unlock()
		if dbAddr == "" {
			ls.setDBConnecting(false)
			return
		}

		conn, err := net.DialTimeout("tcp", dbAddr, 5*time.Second)
		if err != nil {
			log.Printf("[login %s] failed to connect to dbproxy %s at %s: %v, retry in %v", ls.ServerId, dbID, dbAddr, err, backoff)
			select {
			case <-ls.Done():
				return
			case <-time.After(backoff):
			}
			if backoff < 30*time.Second {
				backoff *= 2
			}
			continue
		}

		result := make(chan bool, 1)
		posted := ls.Loop.Post(common.Event{Kind: common.EventDefer, Fn: func() {
			if ls.dbConn != nil {
				_ = conn.Close()
				ls.setDBConnecting(false)
				result <- true
				return
			}
			cw := common.NewConnWrapper(conn)
			cw.PeerType = common.ServerDbProxy
			ls.dbConn = cw
			ls.setDBConnecting(false)
			log.Printf("[login %s] connected to dbproxy %s at %s", ls.ServerId, dbID, dbAddr)
			ls.SendHello(cw)
			go ls.DbReadLoop(cw)
			result <- true
		}})
		if !posted {
			_ = conn.Close()
			ls.setDBConnecting(false)
			return
		}
		if <-result {
			return
		}
	}
}

func (ls *loginServer) setDBConnecting(value bool) {
	ls.dbMu.Lock()
	ls.dbConnecting = value
	ls.dbMu.Unlock()
}

func (ls *loginServer) DbReadLoop(cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[login %s] DbReadLoop panic recovered: %v", ls.ServerId, r)
		}
	}()
	for {
		msg, err := common.ReadMessage(cw.Conn)
		if err != nil {
			ls.Loop.Defer(func() {
				if ls.dbConn == cw {
					_ = ls.dbConn.Close()
					ls.dbConn = nil
					log.Printf("[login %s] dbproxy connection lost: %v", ls.ServerId, err)
				}
				ls.setDBConnecting(false)
				ls.ConnectToDb(ls.dbID, ls.dbAddr)
			})
			return
		}
		if !ls.Loop.Post(common.Event{Kind: common.EventMessage, Conn: cw, Msg: msg}) {
			cw.Close()
			ls.Loop.Defer(func() {
				if ls.dbConn == cw {
					ls.dbConn = nil
				}
				ls.setDBConnecting(false)
				ls.ConnectToDb(ls.dbID, ls.dbAddr)
			})
			return
		}
	}
}
