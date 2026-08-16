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
	loginPrepareTimeout = 10 * time.Second
	createEntityTimeout = 10 * time.Second
	destroyTimeout      = 10 * time.Second
	heartbeatTimeout    = 15 * time.Second // HEALTHY → SUSPECT after no heartbeat
	suspectTimeout      = 30 * time.Second // SUSPECT → DEAD, full logout
)

type sessionState int

const (
	stateHealthy sessionState = iota
	stateSuspect
)

type tokenPending struct {
	conn    *common.ConnWrapper
	reqID   uint64
	account string
	timer   *time.Timer
}

type loginPending struct {
	conn      *common.ConnWrapper
	account   string
	playerID  uint64
	worldID   string
	worldAddr string
	x         float64
	z         float64
	width     float64
	height    float64
	timer     *time.Timer
}

type clientSession struct {
	PlayerId      uint64
	WorldId       string
	WorldAddr     string
	Account       string
	LastHeartbeat time.Time
	State         sessionState
}

type gatewayServer struct {
	*common.ServerBase
	mu         sync.Mutex
	reqCounter uint64
	pendings   map[uint64]*tokenPending

	loginPendings map[string]*loginPending

	sessions    map[*common.ConnWrapper]*clientSession
	playerConns map[uint64]*common.ConnWrapper
	loggingOut  map[uint64]bool

	destroyPendings map[uint64]*time.Timer

	recoverableSessions map[string]*clientSession // account → session (SUSPECT)
	suspectTimers       map[string]*time.Timer    // account → suspect deadline timer

	worldConns   map[string]*common.ConnWrapper
	worldReadLns map[string]bool

	clientRouter  *common.MessageRouter
	centralRouter *common.MessageRouter
	worldRouter   *common.MessageRouter
}

func NewGatewayServer(id string) *gatewayServer {
	gs := &gatewayServer{
		ServerBase:          common.NewServerBase(common.ServerGateway, id),
		pendings:            make(map[uint64]*tokenPending),
		loginPendings:       make(map[string]*loginPending),
		sessions:            make(map[*common.ConnWrapper]*clientSession),
		playerConns:         make(map[uint64]*common.ConnWrapper),
		loggingOut:          make(map[uint64]bool),
		destroyPendings:     make(map[uint64]*time.Timer),
		recoverableSessions: make(map[string]*clientSession),
		suspectTimers:       make(map[string]*time.Timer),
		worldConns:          make(map[string]*common.ConnWrapper),
		worldReadLns:        make(map[string]bool),
		clientRouter:        common.NewMessageRouter(),
		centralRouter:       common.NewMessageRouter(),
		worldRouter:         common.NewMessageRouter(),
	}

	// Client → Gateway
	common.Register(gs.clientRouter, common.Cli2Gw_LoginReq, gs.HandleClientLogin)
	common.Register(gs.clientRouter, common.Cli2Wd_WalkStartReq, gs.HandleClientWalkStart)
	common.Register(gs.clientRouter, common.Cli2Wd_RunStartReq, gs.HandleClientRunStart)
	common.Register(gs.clientRouter, common.Cli2Wd_SprintStartReq, gs.HandleClientSprintStart)
	common.Register(gs.clientRouter, common.Cli2Wd_JumpStartReq, gs.HandleClientJumpStart)
	common.Register(gs.clientRouter, common.Cli2Wd_DashStartReq, gs.HandleClientDashStart)
	common.Register(gs.clientRouter, common.Cli2Wd_RollStartReq, gs.HandleClientRollStart)
	common.Register(gs.clientRouter, common.Cli2Wd_StopStartReq, gs.HandleClientStopStart)
	common.Register(gs.clientRouter, common.Cli2Wd_MoveStopReq, gs.HandleClientMoveStop)
	common.Register(gs.clientRouter, common.Cli2Wd_MoveDirChangeReq, gs.HandleClientMoveDirChange)
	common.Register(gs.clientRouter, common.Cli2Wd_SkillReq, gs.HandleClientSkill)
	common.Register(gs.clientRouter, common.Cli2Gw_LogoutReq, gs.HandleClientLogout)
	common.Register(gs.clientRouter, common.Cli2Gw_HeartbeatReq, gs.HandleClientHeartbeat)

	// Central → Gateway
	common.Register(gs.centralRouter, common.Ct2Gw_LoginPrepareRsp, gs.HandleCentralLoginPrepareRsp)
	common.Register(gs.centralRouter, common.Ct2Gw_LoginFinishRsp, gs.HandleCentralLoginFinishRsp)
	common.Register(gs.centralRouter, common.Ct2Gw_ForceKickNotify, gs.HandleCentralForceKick)
	common.Register(gs.centralRouter, common.Ct2Srv_RegisterRsp, gs.HandleCentralRegisterRsp)
	common.Register(gs.centralRouter, common.Ct2Srv_ServerListRsp, gs.HandleCentralServerListRsp)
	common.Register(gs.centralRouter, common.Ct2Gw_NewWorldNotify, gs.HandleCentralNewWorld)
	common.Register(gs.centralRouter, common.Ct2Gw_LogoutBeginRsp, gs.HandleCentralLogoutBeginRsp)
	common.Register(gs.centralRouter, common.Ct2Gw_LogoutCleanupRsp, gs.HandleCentralLogoutCleanupRsp)
	common.Register(gs.centralRouter, common.Ct2Srv_HeartbeatRsp, gs.NoopHeartbeat)
	common.Register(gs.centralRouter, common.Ct2Srv_ShutdownNotify, gs.HandleCentralShutdownNotify)

	// World → Gateway
	common.Register(gs.worldRouter, common.Wd2Cli_MoveRsp, gs.HandleWorldMoveRsp)
	common.Register(gs.worldRouter, common.Wd2Cli_SkillRsp, gs.HandleWorldSkillRsp)
	common.Register(gs.worldRouter, common.Wd2Gw_CreateEntityRsp, gs.HandleWorldCreateEntityRsp)
	common.Register(gs.worldRouter, common.Wd2Gw_DestroyEntityRsp, gs.HandleWorldDestroyEntityRsp)

	gs.OnMessage = gs.HandleMessage
	gs.OnCentralMessage = gs.HandleCentralMessage
	gs.OnDisconnect = gs.HandleClientDisconnect

	go gs.HeartbeatScanner()

	return gs
}

func (gs *gatewayServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (gs *gatewayServer) HandleMessage(conn *common.ConnWrapper, msg common.Message) {
	gs.clientRouter.Dispatch(conn, msg)
}

func (gs *gatewayServer) HandleCentralMessage(msg common.Message) {
	gs.centralRouter.Dispatch(nil, msg)
}

// --- Client handlers ---

func (gs *gatewayServer) HandleClientLogin(conn *common.ConnWrapper, req *common.LoginReq) {
	token := req.Account
	if token == "" {
		rsp := common.LoginRsp{Success: false, Message: "令牌为空"}
		common.SendMsg(conn, common.Gw2Cli_LoginRsp, &rsp)
		return
	}

	account, err := common.VerifyToken(token)
	if err != nil {
		log.Printf("[gateway] token verify failed, disconnecting client: %v", err)
		conn.Close()
		return
	}
	log.Printf("[gateway] token verified locally: account=%s", account)

	// Check for reconnection — if this account has a SUSPECT session, recover it.
	gs.mu.Lock()
	if rec, ok := gs.recoverableSessions[account]; ok {
		if timer, tOk := gs.suspectTimers[account]; tOk {
			timer.Stop()
			delete(gs.suspectTimers, account)
		}
		delete(gs.recoverableSessions, account)

		rec.State = stateHealthy
		rec.LastHeartbeat = time.Now()
		gs.sessions[conn] = rec
		gs.playerConns[rec.PlayerId] = conn
		gs.mu.Unlock()

		log.Printf("[gateway] session RECOVERED: account=%s player=%d", account, rec.PlayerId)
		loginRsp := common.LoginRsp{
			Success:   true,
			Message:   "重连成功",
			WorldAddr: rec.WorldAddr,
			WorldId:   rec.WorldId,
			PlayerId:  rec.PlayerId,
		}
		common.SendMsg(conn, common.Gw2Cli_LoginRsp, &loginRsp)
		return
	}
	gs.mu.Unlock()

	// Normal login flow.
	gs.mu.Lock()
	gs.reqCounter++
	reqID := gs.reqCounter
	pending := &tokenPending{conn: conn, reqID: reqID, account: account}
	pending.timer = time.AfterFunc(loginPrepareTimeout, func() {
		gs.HandleLoginPrepareTimeout(reqID, account)
	})
	gs.pendings[reqID] = pending
	gs.mu.Unlock()

	prepareReq := common.LoginPrepareReq{ReqId: reqID, Account: account}
	gs.SendToCentralMsg(common.Gw2Ct_LoginPrepareReq, &prepareReq)
	log.Printf("[gateway] sent LoginPrepareReq reqID=%d to central for account=%s", reqID, account)
}

// forwardClientMove stamps the session's PlayerId onto a client move request and
// forwards it to the player's world; on failure it replies with a MoveRsp so the
// client snaps back.
func (gs *gatewayServer) ForwardClientMove(conn *common.ConnWrapper, msgType common.MessageType, setPlayerID func(uid uint64), m proto.Message) {
	gs.mu.Lock()
	session, ok := gs.sessions[conn]
	gs.mu.Unlock()
	if !ok {
		return
	}
	setPlayerID(session.PlayerId)
	if err := gs.ForwardMsgToWorld(session.WorldId, msgType, m); err != nil {
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, &common.MoveRsp{Success: false, PlayerId: session.PlayerId, Message: "世界服不可用"})
	}
}

func (gs *gatewayServer) HandleClientWalkStart(conn *common.ConnWrapper, req *common.WalkStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_WalkStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientRunStart(conn *common.ConnWrapper, req *common.RunStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_RunStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientSprintStart(conn *common.ConnWrapper, req *common.SprintStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_SprintStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientJumpStart(conn *common.ConnWrapper, req *common.JumpStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_JumpStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientDashStart(conn *common.ConnWrapper, req *common.DashStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_DashStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientRollStart(conn *common.ConnWrapper, req *common.RollStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_RollStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientStopStart(conn *common.ConnWrapper, req *common.StopStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_StopStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientMoveStop(conn *common.ConnWrapper, req *common.MoveStopReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_MoveStopReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientMoveDirChange(conn *common.ConnWrapper, req *common.MoveDirChangeReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_MoveDirChangeReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientSkill(conn *common.ConnWrapper, req *common.SkillReq) {
	gs.mu.Lock()
	session, ok := gs.sessions[conn]
	gs.mu.Unlock()
	if !ok {
		return
	}
	req.PlayerId = session.PlayerId
	if err := gs.ForwardMsgToWorld(session.WorldId, common.Cli2Wd_SkillReq, req); err != nil {
		common.SendMsg(conn, common.Wd2Cli_SkillRsp, &common.SkillRsp{Success: false, PlayerId: req.PlayerId, Message: "世界服不可用"})
	}
}

func (gs *gatewayServer) HandleClientLogout(conn *common.ConnWrapper, _ *common.LogoutReq) {
	gs.mu.Lock()
	session, ok := gs.sessions[conn]
	if !ok {
		gs.mu.Unlock()
		return
	}
	playerID := session.PlayerId
	gs.loggingOut[playerID] = true
	gs.mu.Unlock()

	log.Printf("[gateway] player %d logging out, notifying central", playerID)
	beginReq := common.LogoutBeginReq{PlayerId: playerID}
	gs.SendToCentralMsg(common.Gw2Ct_LogoutBeginReq, &beginReq)
}

// --- Timeout handlers ---

func (gs *gatewayServer) HandleLoginPrepareTimeout(reqID uint64, account string) {
	gs.mu.Lock()
	pending, ok := gs.pendings[reqID]
	if ok {
		delete(gs.pendings, reqID)
	}
	gs.mu.Unlock()
	if !ok {
		return
	}
	log.Printf("[gateway] LoginPrepare timeout for account=%s reqID=%d", account, reqID)
	finishReq := common.LoginFinishReq{Success: false, Account: account, Message: "登录准备超时"}
	gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq)
	pending.conn.Close()
}

func (gs *gatewayServer) HandleCreateEntityTimeout(account string) {
	gs.mu.Lock()
	pend, ok := gs.loginPendings[account]
	if ok {
		delete(gs.loginPendings, account)
	}
	gs.mu.Unlock()
	if !ok {
		return
	}
	log.Printf("[gateway] CreateEntity timeout for account %s, rolling back", account)
	finishReq := common.LoginFinishReq{Success: false, Account: account, Message: "创建实体超时"}
	gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq)
	pend.conn.Close()
}

// sendDestroyAndArmTimer 向 world 发送销毁实体请求,并装上超时定时器(若未存在)。
func (gs *gatewayServer) SendDestroyAndArmTimer(playerID uint64, worldID string) {
	destroyReq := common.DestroyEntityReq{PlayerId: playerID}
	gs.ForwardMsgToWorld(worldID, common.Gw2Wd_DestroyEntityReq, &destroyReq)

	gs.mu.Lock()
	if _, exists := gs.destroyPendings[playerID]; !exists {
		gs.destroyPendings[playerID] = time.AfterFunc(destroyTimeout, func() {
			gs.HandleDestroyTimeout(playerID)
		})
	}
	gs.mu.Unlock()
}

// sendLogoutCleanup 通知 central 清理在线表。
func (gs *gatewayServer) SendLogoutCleanup(playerID uint64) {
	cleanupReq := common.LogoutCleanupReq{PlayerId: playerID, GatewayId: gs.ServerId}
	gs.SendToCentralMsg(common.Gw2Ct_LogoutCleanupReq, &cleanupReq)
}

func (gs *gatewayServer) HandleDestroyTimeout(playerID uint64) {
	gs.mu.Lock()
	delete(gs.destroyPendings, playerID)
	gs.mu.Unlock()
	log.Printf("[gateway] destroy entity timeout for player %d, cleaning up central directly", playerID)
	gs.SendLogoutCleanup(playerID)
}

// --- World message handlers ---

func (gs *gatewayServer) HandleWorldMoveRsp(_ *common.ConnWrapper, rsp *common.MoveRsp) {
	gs.mu.Lock()
	clientConn, ok := gs.playerConns[rsp.PlayerId]
	gs.mu.Unlock()
	if ok {
		common.SendMsg(clientConn, common.Wd2Cli_MoveRsp, rsp)
	}
}

func (gs *gatewayServer) HandleWorldSkillRsp(_ *common.ConnWrapper, rsp *common.SkillRsp) {
	gs.mu.Lock()
	clientConn, ok := gs.playerConns[rsp.PlayerId]
	gs.mu.Unlock()
	if ok {
		common.SendMsg(clientConn, common.Wd2Cli_SkillRsp, rsp)
	}
}

func (gs *gatewayServer) HandleWorldCreateEntityRsp(_ *common.ConnWrapper, rsp *common.CreateEntityRsp) {
	log.Printf("[gateway] CreateEntityRsp from world: player=%d account=%s success=%v",
		rsp.PlayerId, rsp.Account, rsp.Success)

	gs.mu.Lock()
	pend, ok := gs.loginPendings[rsp.Account]
	if ok {
		if pend.timer != nil {
			pend.timer.Stop()
		}
		pend.playerID = rsp.PlayerId
		pend.x = rsp.X
		pend.z = rsp.Z
		pend.width = rsp.Width
		pend.height = rsp.Height
	}
	gs.mu.Unlock()
	if !ok {
		return
	}

	finishReq := common.LoginFinishReq{
		PlayerId:  rsp.PlayerId,
		Success:   rsp.Success,
		Account:   rsp.Account,
		WorldId:   pend.worldID,
		GatewayId: gs.ServerId,
	}
	if !rsp.Success {
		finishReq.Message = rsp.Message
	}
	gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq)
}

func (gs *gatewayServer) HandleWorldDestroyEntityRsp(_ *common.ConnWrapper, rsp *common.DestroyEntityRsp) {
	log.Printf("[gateway] player %d entity destroyed, cleaning up central online table", rsp.PlayerId)
	gs.mu.Lock()
	if timer, ok := gs.destroyPendings[rsp.PlayerId]; ok {
		timer.Stop()
		delete(gs.destroyPendings, rsp.PlayerId)
	}
	gs.mu.Unlock()
	gs.SendLogoutCleanup(rsp.PlayerId)
}

// --- World connection ---

func (gs *gatewayServer) ForwardToWorld(worldID string, msg common.Message) error {
	gs.mu.Lock()
	conn, ok := gs.worldConns[worldID]
	gs.mu.Unlock()
	if !ok {
		log.Printf("[gateway] forward failed: world %s not connected", worldID)
		return fmt.Errorf("world %s not connected", worldID)
	}
	if err := conn.Send(msg); err != nil {
		log.Printf("[gateway] forward to world %s failed: %v", worldID, err)
		return err
	}
	return nil
}

// forwardMsgToWorld marshals m and forwards it to the named world.
func (gs *gatewayServer) ForwardMsgToWorld(worldID string, msgType common.MessageType, m proto.Message) error {
	data, err := common.MarshalHelper(m)
	if err != nil {
		return err
	}
	return gs.ForwardToWorld(worldID, common.Message{Type: msgType, Data: data})
}

func (gs *gatewayServer) ConnectToWorld(worldID, worldAddr string) {
	gs.mu.Lock()
	if _, ok := gs.worldConns[worldID]; ok {
		gs.mu.Unlock()
		return
	}
	gs.mu.Unlock()

	conn, err := net.DialTimeout("tcp", worldAddr, 5*time.Second)
	if err != nil {
		log.Printf("[gateway] failed to connect to world %s at %s: %v", worldID, worldAddr, err)
		return
	}
	cw := common.NewConnWrapper(conn)
	gs.mu.Lock()
	gs.worldConns[worldID] = cw
	gs.mu.Unlock()
	log.Printf("[gateway] connected to world %s at %s", worldID, worldAddr)
	go gs.WorldReadLoop(cw)
}

func (gs *gatewayServer) WorldReadLoop(cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[gateway %s] worldReadLoop panic recovered: %v", gs.ServerId, r)
		}
	}()
	for {
		msg, err := common.ReadMessage(cw.Conn)
		if err != nil {
			return
		}
		gs.worldRouter.Dispatch(cw, msg)
	}
}

// --- Heartbeat -----------------------------------------------------------

// handleClientHeartbeat refreshes session liveness and stamps the server's
// wall-clock time (Unix ms) into the reply, echoing the client's send time so
// the client can derive the server-clock offset from the reply alone.
func (gs *gatewayServer) HandleClientHeartbeat(conn *common.ConnWrapper, req *common.ClientHeartbeatReq) {
	gs.mu.Lock()
	session, ok := gs.sessions[conn]
	if ok {
		session.LastHeartbeat = time.Now()
	}
	gs.mu.Unlock()
	if !ok {
		return
	}
	rsp := common.ClientHeartbeatRsp{ServerTimeMs: time.Now().UnixMilli(), ClientTimeMs: req.ClientTimeMs}
	common.SendMsg(conn, common.Gw2Cli_HeartbeatRsp, &rsp)
}

func (gs *gatewayServer) HeartbeatScanner() {
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for range ticker.C {
		gs.mu.Lock()
		now := time.Now()
		for conn, session := range gs.sessions {
			if session.State != stateHealthy {
				continue
			}
			if _, loggingOut := gs.loggingOut[session.PlayerId]; loggingOut {
				continue
			}
			if now.Sub(session.LastHeartbeat) > heartbeatTimeout {
				gs.MarkSuspectLocked(conn, session)
				log.Printf("[gateway] session SUSPECT: account=%s player=%d", session.Account, session.PlayerId)
			}
		}
		gs.mu.Unlock()
	}
}

// markSuspectLocked 将一个 healthy session 转入 suspect 状态:从 sessions/playerConns
// 移除(立即隔绝断连的 conn),加入 recoverableSessions 并启动重连窗口定时器。
// 必须持有 gs.mu。
func (gs *gatewayServer) MarkSuspectLocked(conn *common.ConnWrapper, session *clientSession) {
	session.State = stateSuspect
	delete(gs.sessions, conn)
	delete(gs.playerConns, session.PlayerId)
	gs.recoverableSessions[session.Account] = session
	account := session.Account
	gs.suspectTimers[account] = time.AfterFunc(suspectTimeout, func() {
		gs.HandleSuspectTimeout(account)
	})
}

// handleClientDisconnect 是 OnDisconnect 回调:客户端连接读失败/断开时,立即把 session
// 转入 suspect,不必等 heartbeatScanner 的 15s 心跳超时(断连后心跳必然停)。提前转换
// 可消除"死 conn 残活、向其 Send 失败"的空窗,同时保留 30s 重连窗口(recoverableSessions)。
func (gs *gatewayServer) HandleClientDisconnect(conn *common.ConnWrapper) {
	gs.mu.Lock()
	session, ok := gs.sessions[conn]
	if !ok {
		gs.mu.Unlock()
		return // 已被 ForceKick/logout 等清理
	}
	if _, loggingOut := gs.loggingOut[session.PlayerId]; loggingOut {
		gs.mu.Unlock()
		return // 主动登出进行中,交给 logout 流程
	}
	gs.MarkSuspectLocked(conn, session)
	account := session.Account
	gs.mu.Unlock()
	log.Printf("[gateway] client disconnect: account=%s player=%d -> SUSPECT", account, session.PlayerId)
}

func (gs *gatewayServer) HandleSuspectTimeout(account string) {
	gs.mu.Lock()
	session, ok := gs.recoverableSessions[account]
	if ok {
		delete(gs.recoverableSessions, account)
		delete(gs.suspectTimers, account)
	}
	gs.mu.Unlock()
	if !ok {
		return
	}
	log.Printf("[gateway] session DEAD: account=%s player=%d, full logout", account, session.PlayerId)

	// 直接 destroy:cleanup 由 DestroyEntityRsp 触发,central 收到后清在线表。
	// 不发 LogoutBeginReq:suspect 与顶号的 WaitingKick 都关乎 accountStates.Status,
	// 发了会覆盖 WaitingKick 导致 tryContinuePendingAssign 失败。
	gs.SendDestroyAndArmTimer(session.PlayerId, session.WorldId)
}
