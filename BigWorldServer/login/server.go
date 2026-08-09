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
	conn  *common.ConnWrapper
	req   *common.LoginReq
	reqID uint64
	timer *time.Timer
}

// validatePending correlates a client LoginReq with the async
// ValidateAccountRsp from dbproxy.
type validatePending struct {
	conn  *common.ConnWrapper
	req   *common.LoginReq
	reqID uint64
	timer *time.Timer
}

type loginServer struct {
	*common.ServerBase
	mu sync.Mutex

	reqCounter       uint64
	pendings         map[uint64]*gatewayPending   // gateway-assign stage
	validatePendings map[uint64]*validatePending   // account-validation stage

	// dbproxy connection (discovered via central) and its inbound router.
	dbConn   *common.ConnWrapper
	dbRouter *common.MessageRouter

	clientRouter  *common.MessageRouter
	centralRouter *common.MessageRouter
}

func newLoginServer(id string) *loginServer {
	ls := &loginServer{
		ServerBase:       common.NewServerBase(common.ServerLogin, id),
		pendings:         make(map[uint64]*gatewayPending),
		validatePendings: make(map[uint64]*validatePending),
		dbRouter:         common.NewMessageRouter(),
		clientRouter:     common.NewMessageRouter(),
		centralRouter:    common.NewMessageRouter(),
	}

	common.Register(ls.clientRouter, common.Cli2Lg_LoginReq, ls.handleAccountLogin)

	// Central -> Login
	common.Register(ls.centralRouter, common.Ct2Lg_GatewayAssignRsp, ls.handleGatewayAssignRsp)
	common.Register(ls.centralRouter, common.Ct2Srv_RegisterRsp, ls.handleRegisterRsp)
	common.Register(ls.centralRouter, common.Ct2Srv_ServerListRsp, ls.handleCentralServerListRsp)
	common.Register(ls.centralRouter, common.Ct2Srv_NewDbProxyNotify, ls.handleCentralNewDbProxy)
	common.Register(ls.centralRouter, common.Ct2Srv_ShutdownNotify, ls.handleCentralShutdownNotify)
	common.Register(ls.centralRouter, common.Ct2Srv_HeartbeatRsp, ls.noopHeartbeat)

	// dbproxy -> Login
	common.Register(ls.dbRouter, common.Db2Lg_ValidateAccountRsp, ls.handleValidateAccountRsp)

	ls.OnMessage = ls.handleMessage
	ls.OnCentralMessage = ls.handleCentralMessage
	return ls
}

func (ls *loginServer) noopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (ls *loginServer) handleMessage(conn *common.ConnWrapper, msg common.Message) {
	ls.clientRouter.Dispatch(conn, msg)
}

func (ls *loginServer) handleCentralMessage(msg common.Message) {
	ls.centralRouter.Dispatch(nil, msg)
}

// handleAccountLogin asks dbproxy to validate the credentials. Account data
// now lives in MySQL (via dbproxy), replacing the old hardcoded table.
func (ls *loginServer) handleAccountLogin(conn *common.ConnWrapper, req *common.LoginReq) {
	log.Printf("[login] login attempt: account=%s", req.Account)

	ls.mu.Lock()
	dbConn := ls.dbConn
	ls.mu.Unlock()
	if dbConn == nil {
		rsp := common.LoginRsp{Success: false, Message: "登录服务不可用，请稍后重试"}
		common.SendMsg(conn, common.Lg2Cli_LoginRsp, &rsp)
		return
	}

	ls.mu.Lock()
	ls.reqCounter++
	reqID := ls.reqCounter
	pending := &validatePending{conn: conn, req: req, reqID: reqID}
	pending.timer = time.AfterFunc(validateTimeout, func() {
		ls.handleValidateTimeout(reqID)
	})
	ls.validatePendings[reqID] = pending
	ls.mu.Unlock()

	vReq := common.ValidateAccountReq{ReqId: reqID, Account: req.Account, Password: req.Password}
	if err := ls.forwardMsgToDb(common.Lg2Db_ValidateAccountReq, &vReq); err != nil {
		ls.mu.Lock()
		delete(ls.validatePendings, reqID)
		ls.mu.Unlock()
		pending.timer.Stop()
		rsp := common.LoginRsp{Success: false, Message: "登录服务不可用，请稍后重试"}
		common.SendMsg(conn, common.Lg2Cli_LoginRsp, &rsp)
		return
	}
	log.Printf("[login] validating account=%s via dbproxy (reqID=%d)", req.Account, reqID)
}

func (ls *loginServer) handleValidateAccountRsp(_ *common.ConnWrapper, rsp *common.ValidateAccountRsp) {
	ls.mu.Lock()
	pending, ok := ls.validatePendings[rsp.ReqId]
	if ok {
		if pending.timer != nil {
			pending.timer.Stop()
		}
		delete(ls.validatePendings, rsp.ReqId)
	}
	ls.mu.Unlock()
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

	// Credentials valid: proceed to gateway assignment (existing flow).
	ls.mu.Lock()
	ls.reqCounter++
	gwReqID := ls.reqCounter
	gwPending := &gatewayPending{conn: pending.conn, req: pending.req, reqID: gwReqID}
	gwPending.timer = time.AfterFunc(gatewayAssignTimeout, func() {
		ls.handleGatewayAssignTimeout(gwReqID)
	})
	ls.pendings[gwReqID] = gwPending
	ls.mu.Unlock()

	assignReq := common.GatewayAssignReq{ReqId: gwReqID, ServerId: ls.ServerId, Account: pending.req.Account}
	ls.SendToCentralMsg(common.Lg2Ct_GatewayAssignReq, &assignReq)
	log.Printf("[login] account=%s validated, requested gateway assignment (reqID=%d)", pending.req.Account, gwReqID)
}

func (ls *loginServer) handleValidateTimeout(reqID uint64) {
	ls.mu.Lock()
	pending, ok := ls.validatePendings[reqID]
	if ok {
		delete(ls.validatePendings, reqID)
	}
	ls.mu.Unlock()
	if !ok {
		return
	}
	log.Printf("[login] validate timeout (reqID=%d) for account=%s", reqID, pending.req.Account)
	rsp := common.LoginRsp{Success: false, Message: "登录超时，请重试"}
	common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &rsp)
}

func (ls *loginServer) handleGatewayAssignRsp(_ *common.ConnWrapper, rsp *common.GatewayAssignRsp) {
	ls.mu.Lock()
	pending, ok := ls.pendings[rsp.ReqId]
	if ok {
		delete(ls.pendings, rsp.ReqId)
	}
	ls.mu.Unlock()

	if !ok {
		return
	}
	pending.timer.Stop()

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

func (ls *loginServer) handleGatewayAssignTimeout(reqID uint64) {
	ls.mu.Lock()
	pending, ok := ls.pendings[reqID]
	if ok {
		delete(ls.pendings, reqID)
	}
	ls.mu.Unlock()

	if !ok {
		return
	}
	log.Printf("[login] gateway assign timeout (reqID=%d) for account=%s", reqID, pending.req.Account)
	rsp := common.LoginRsp{Success: false, Message: "登录超时，请重试"}
	common.SendMsg(pending.conn, common.Lg2Cli_LoginRsp, &rsp)
	// pending.conn.CloseAfterSend()
}

func (ls *loginServer) handleRegisterRsp(_ *common.ConnWrapper, rsp *common.RegisterRsp) {
	if rsp.Success {
		log.Printf("[login %s] registered: %s", ls.ServerId, rsp.Message)
		// Pull the dbproxy list in case it registered before us.
		ls.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerDbProxy})
	}
}

// --- dbproxy connection ---

func (ls *loginServer) handleCentralServerListRsp(_ *common.ConnWrapper, rsp *common.ServerListRsp) {
	for _, e := range rsp.Servers {
		ls.connectToDb(e.ServerId, e.ListenAddr)
	}
}

func (ls *loginServer) handleCentralNewDbProxy(_ *common.ConnWrapper, entry *common.ServerEntry) {
	ls.connectToDb(entry.ServerId, entry.ListenAddr)
}

// handleCentralShutdownNotify stops accepting new logins, acks central, and
// exits. Login has nothing to flush - accounts are validated against dbproxy in
// real time - so it is torn down early in the shutdown sequence.
func (ls *loginServer) handleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	log.Printf("[login %s] shutdown requested: %s", ls.ServerId, notify.Reason)
	ls.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ls.ServerId})
	go ls.Stop()
}

func (ls *loginServer) forwardMsgToDb(msgType common.MessageType, m proto.Message) error {
	data, err := common.MarshalHelper(m)
	if err != nil {
		return err
	}
	ls.mu.Lock()
	conn := ls.dbConn
	ls.mu.Unlock()
	if conn == nil {
		return fmt.Errorf("dbproxy not connected")
	}
	return conn.Send(common.Message{Type: msgType, Data: data})
}

func (ls *loginServer) connectToDb(dbID, dbAddr string) {
	ls.mu.Lock()
	if ls.dbConn != nil {
		ls.mu.Unlock()
		return
	}
	ls.mu.Unlock()

	conn, err := net.DialTimeout("tcp", dbAddr, 5*time.Second)
	if err != nil {
		log.Printf("[login %s] failed to connect to dbproxy %s at %s: %v", ls.ServerId, dbID, dbAddr, err)
		return
	}
	cw := common.NewConnWrapper(conn)
	ls.mu.Lock()
	ls.dbConn = cw
	ls.mu.Unlock()
	log.Printf("[login %s] connected to dbproxy %s at %s", ls.ServerId, dbID, dbAddr)
	go ls.dbReadLoop(cw)
}

func (ls *loginServer) dbReadLoop(cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[login %s] dbReadLoop panic recovered: %v", ls.ServerId, r)
		}
	}()
	for {
		msg, err := common.ReadMessage(cw.Conn)
		if err != nil {
			ls.mu.Lock()
			if ls.dbConn == cw {
				ls.dbConn.Close()
				ls.dbConn = nil
			}
			ls.mu.Unlock()
			log.Printf("[login %s] dbproxy connection lost: %v", ls.ServerId, err)
			return
		}
		ls.dbRouter.Dispatch(cw, msg)
	}
}
