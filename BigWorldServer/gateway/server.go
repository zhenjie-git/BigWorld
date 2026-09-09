package main

import (
	"fmt"
	"log"
	"net"
	"time"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

const (
	loginPrepareTimeout = 10 * time.Second
	createEntityTimeout = 10 * time.Second
	destroyTimeout      = 10 * time.Second
	heartbeatTimeout    = 15 * time.Second
	suspectTimeout      = 30 * time.Second
)

type sessionState int

const (
	stateHealthy sessionState = iota
	stateSuspect
)

type tokenPending struct {
	conn     *common.ConnWrapper
	reqID    uint64
	account  string
	deadline time.Time
}

type loginPending struct {
	conn      *common.ConnWrapper
	account   string
	playerID  uint64
	worldID   string
	worldAddr string
	sceneID   string
	x         float64
	z         float64
	width     float64
	height    float64
	deadline  time.Time
}

type clientSession struct {
	PlayerId      uint64
	WorldId       string
	WorldAddr     string
	SceneId       string
	Account       string
	LastHeartbeat time.Time
	State         sessionState
	X             float64
	Z             float64
	Width         float64
	Height        float64

	SuspectDeadline time.Time
}

type destroyPending struct {
	worldID  string
	deadline time.Time
}

type gatewayServer struct {
	*common.ServerBase
	reqCounter uint64
	pendings   map[uint64]*tokenPending

	loginPendings map[string]*loginPending

	sessions    map[*common.ConnWrapper]*clientSession
	playerConns map[uint64]*common.ConnWrapper
	loggingOut  map[uint64]bool

	destroyPendings map[uint64]destroyPending

	recoverableSessions map[string]*clientSession

	worldConns   map[string]*common.ConnWrapper
	worldReadLns map[string]bool
}

func NewGatewayServer(id string) *gatewayServer {
	gs := &gatewayServer{
		ServerBase:          common.NewServerBase(common.ServerGateway, id),
		pendings:            make(map[uint64]*tokenPending),
		loginPendings:       make(map[string]*loginPending),
		sessions:            make(map[*common.ConnWrapper]*clientSession),
		playerConns:         make(map[uint64]*common.ConnWrapper),
		loggingOut:          make(map[uint64]bool),
		destroyPendings:     make(map[uint64]destroyPending),
		recoverableSessions: make(map[string]*clientSession),
		worldConns:          make(map[string]*common.ConnWrapper),
		worldReadLns:        make(map[string]bool),
	}

	common.Register(gs.Router(common.SrcClient), common.Cli2Gw_LoginReq, gs.HandleClientLogin)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_WalkStartReq, gs.HandleClientWalkStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_RunStartReq, gs.HandleClientRunStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_SprintStartReq, gs.HandleClientSprintStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_JumpStartReq, gs.HandleClientJumpStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_DashStartReq, gs.HandleClientDashStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_RollStartReq, gs.HandleClientRollStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_StopStartReq, gs.HandleClientStopStart)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_MoveStopReq, gs.HandleClientMoveStop)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_MoveDirChangeReq, gs.HandleClientMoveDirChange)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_SkillReq, gs.HandleClientSkill)
	common.Register(gs.Router(common.SrcClient), common.Cli2Gw_LogoutReq, gs.HandleClientLogout)
	common.Register(gs.Router(common.SrcClient), common.Cli2Gw_HeartbeatReq, gs.HandleClientHeartbeat)

	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LoginPrepareRsp, gs.HandleCentralLoginPrepareRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LoginFinishRsp, gs.HandleCentralLoginFinishRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_ForceKickNotify, gs.HandleCentralForceKick)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_RegisterRsp, gs.HandleCentralRegisterRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_ServerListRsp, gs.HandleCentralServerListRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_NewWorldNotify, gs.HandleCentralNewWorld)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LogoutBeginRsp, gs.HandleCentralLogoutBeginRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LogoutCleanupRsp, gs.HandleCentralLogoutCleanupRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_HeartbeatRsp, gs.NoopHeartbeat)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_ShutdownNotify, gs.HandleCentralShutdownNotify)

	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_MoveRsp, gs.HandleWorldMoveRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_SkillRsp, gs.HandleWorldSkillRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Gw_CreateEntityRsp, gs.HandleWorldCreateEntityRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Gw_DestroyEntityRsp, gs.HandleWorldDestroyEntityRsp)

	gs.OnDisconnect = gs.HandleClientDisconnect
	gs.Loop.OnTick = gs.OnTick
	gs.Loop.TickEvery = 5 * time.Second

	return gs
}

func (gs *gatewayServer) OnTick() {
	now := time.Now()

	for reqID, pending := range gs.pendings {
		if now.After(pending.deadline) {
			gs.HandleLoginPrepareTimeout(reqID, pending.account)
		}
	}
	for account, pend := range gs.loginPendings {
		if now.After(pend.deadline) {
			gs.HandleCreateEntityTimeout(account)
		}
	}
	for playerID, dp := range gs.destroyPendings {
		if now.After(dp.deadline) {
			gs.HandleDestroyTimeout(playerID)
		}
	}
	for account, session := range gs.recoverableSessions {
		if now.After(session.SuspectDeadline) {
			gs.HandleSuspectTimeout(account)
		}
	}

	for conn, session := range gs.sessions {
		if session.State != stateHealthy {
			continue
		}
		if _, loggingOut := gs.loggingOut[session.PlayerId]; loggingOut {
			continue
		}
		if now.Sub(session.LastHeartbeat) > heartbeatTimeout {
			gs.MarkSuspect(conn, session)
			log.Printf("[gateway] session SUSPECT: account=%s player=%d", session.Account, session.PlayerId)
		}
	}
}

func (gs *gatewayServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

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

	if rec, ok := gs.recoverableSessions[account]; ok {
		delete(gs.recoverableSessions, account)

		rec.State = stateHealthy
		rec.LastHeartbeat = time.Now()
		gs.sessions[conn] = rec
		gs.playerConns[rec.PlayerId] = conn

		log.Printf("[gateway] session RECOVERED: account=%s player=%d", account, rec.PlayerId)
		loginRsp := common.LoginRsp{
			Success:   true,
			Message:   "重连成功",
			WorldAddr: rec.WorldAddr,
			WorldId:   rec.WorldId,
			PlayerId:  rec.PlayerId,
			X:         rec.X,
			Z:         rec.Z,
			Width:     rec.Width,
			Height:    rec.Height,
			SceneId:   rec.SceneId,
		}
		common.SendMsg(conn, common.Gw2Cli_LoginRsp, &loginRsp)

		finishReq := common.LoginFinishReq{
			PlayerId:  rec.PlayerId,
			Success:   true,
			Account:   account,
			WorldId:   rec.WorldId,
			GatewayId: gs.ServerId,
		}
		gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq)

		recConn := conn
		worldID, x, z := rec.WorldId, rec.X, rec.Z
		width, height, playerID := rec.Width, rec.Height, rec.PlayerId
		sceneID := rec.SceneId
		time.AfterFunc(100*time.Millisecond, func() {
			gs.Loop.Defer(func() {
				notify := common.EnterSceneNotify{
					WorldId:  worldID,
					PlayerId: playerID,
					X:        x,
					Z:        z,
					Width:    width,
					Height:   height,
					SceneId:  sceneID,
				}
				common.SendMsg(recConn, common.Wd2Cli_EnterSceneNotify, &notify)
			})
		})
		return
	}

	gs.reqCounter++
	reqID := gs.reqCounter
	pending := &tokenPending{conn: conn, reqID: reqID, account: account,
		deadline: time.Now().Add(loginPrepareTimeout)}
	gs.pendings[reqID] = pending

	prepareReq := common.LoginPrepareReq{ReqId: reqID, Account: account}
	gs.SendToCentralMsg(common.Gw2Ct_LoginPrepareReq, &prepareReq)
	log.Printf("[gateway] sent LoginPrepareReq reqID=%d to central for account=%s", reqID, account)
}

func (gs *gatewayServer) ForwardClientMove(conn *common.ConnWrapper, msgType common.MessageType, setPlayerID func(uid uint64), m proto.Message) {
	session, ok := gs.sessions[conn]
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
	session, ok := gs.sessions[conn]
	if !ok {
		return
	}
	req.PlayerId = session.PlayerId
	if err := gs.ForwardMsgToWorld(session.WorldId, common.Cli2Wd_SkillReq, req); err != nil {
		common.SendMsg(conn, common.Wd2Cli_SkillRsp, &common.SkillRsp{Success: false, PlayerId: req.PlayerId, Message: "世界服不可用"})
	}
}

func (gs *gatewayServer) HandleClientLogout(conn *common.ConnWrapper, _ *common.LogoutReq) {
	session, ok := gs.sessions[conn]
	if !ok {
		return
	}
	playerID := session.PlayerId
	gs.loggingOut[playerID] = true

	log.Printf("[gateway] player %d logging out, notifying central", playerID)
	beginReq := common.LogoutBeginReq{PlayerId: playerID}
	gs.SendToCentralMsg(common.Gw2Ct_LogoutBeginReq, &beginReq)
}

func (gs *gatewayServer) HandleLoginPrepareTimeout(reqID uint64, account string) {
	pending, ok := gs.pendings[reqID]
	if ok {
		delete(gs.pendings, reqID)
	}
	if !ok {
		return
	}
	log.Printf("[gateway] LoginPrepare timeout for account=%s reqID=%d", account, reqID)
	finishReq := common.LoginFinishReq{Success: false, Account: account, Message: "登录准备超时"}
	gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq)
	pending.conn.Close()
}

func (gs *gatewayServer) HandleCreateEntityTimeout(account string) {
	pend, ok := gs.loginPendings[account]
	if ok {
		delete(gs.loginPendings, account)
	}
	if !ok {
		return
	}
	log.Printf("[gateway] CreateEntity timeout for account %s, rolling back", account)
	finishReq := common.LoginFinishReq{Success: false, Account: account, Message: "创建实体超时"}
	gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq)
	pend.conn.Close()
}

func (gs *gatewayServer) SendDestroyAndArmTimer(playerID uint64, worldID string) {
	destroyReq := common.DestroyEntityReq{PlayerId: playerID}
	gs.ForwardMsgToWorld(worldID, common.Gw2Wd_DestroyEntityReq, &destroyReq)

	if _, exists := gs.destroyPendings[playerID]; !exists {
		gs.destroyPendings[playerID] = destroyPending{worldID: worldID, deadline: time.Now().Add(destroyTimeout)}
	}
}

func (gs *gatewayServer) SendLogoutCleanup(playerID uint64) {
	cleanupReq := common.LogoutCleanupReq{PlayerId: playerID, GatewayId: gs.ServerId}
	gs.SendToCentralMsg(common.Gw2Ct_LogoutCleanupReq, &cleanupReq)
}

func (gs *gatewayServer) HandleDestroyTimeout(playerID uint64) {
	delete(gs.destroyPendings, playerID)
	log.Printf("[gateway] destroy entity timeout for player %d, cleaning up central directly", playerID)
	gs.SendLogoutCleanup(playerID)
}

func (gs *gatewayServer) HandleWorldMoveRsp(_ *common.ConnWrapper, rsp *common.MoveRsp) {
	clientConn, ok := gs.playerConns[rsp.PlayerId]
	if ok {
		common.SendMsg(clientConn, common.Wd2Cli_MoveRsp, rsp)
	}
}

func (gs *gatewayServer) HandleWorldSkillRsp(_ *common.ConnWrapper, rsp *common.SkillRsp) {
	clientConn, ok := gs.playerConns[rsp.PlayerId]
	if ok {
		common.SendMsg(clientConn, common.Wd2Cli_SkillRsp, rsp)
	}
}

func (gs *gatewayServer) HandleWorldCreateEntityRsp(_ *common.ConnWrapper, rsp *common.CreateEntityRsp) {
	log.Printf("[gateway] CreateEntityRsp from world: player=%d account=%s success=%v",
		rsp.PlayerId, rsp.Account, rsp.Success)

	pend, ok := gs.loginPendings[rsp.Account]
	if ok {
		pend.playerID = rsp.PlayerId
		pend.x = rsp.X
		pend.z = rsp.Z
		pend.width = rsp.Width
		pend.height = rsp.Height
		pend.sceneID = rsp.SceneId
	}
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
	delete(gs.destroyPendings, rsp.PlayerId)
	gs.SendLogoutCleanup(rsp.PlayerId)
}

func (gs *gatewayServer) ForwardToWorld(worldID string, msg common.Message) error {
	conn, ok := gs.worldConns[worldID]
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

func (gs *gatewayServer) ForwardMsgToWorld(worldID string, msgType common.MessageType, m proto.Message) error {
	data, err := common.MarshalHelper(m)
	if err != nil {
		return err
	}
	return gs.ForwardToWorld(worldID, common.Message{Type: msgType, Data: data})
}

func (gs *gatewayServer) ConnectToWorld(worldID, worldAddr string) {
	if _, ok := gs.worldConns[worldID]; ok {
		return
	}

	go func() {
		conn, err := net.DialTimeout("tcp", worldAddr, 5*time.Second)
		if err != nil {
			log.Printf("[gateway] failed to connect to world %s at %s: %v", worldID, worldAddr, err)
			return
		}
		gs.Loop.Defer(func() {
			if _, ok := gs.worldConns[worldID]; ok {
				conn.Close()
				return
			}
			cw := common.NewConnWrapper(conn)
			cw.PeerType = common.ServerWorld
			gs.worldConns[worldID] = cw
			log.Printf("[gateway] connected to world %s at %s", worldID, worldAddr)
			common.SendMsg(cw, common.Srv2Srv_IdentifyReq, &common.IdentifyReq{ServerType: common.ServerGateway})
			go gs.WorldReadLoop(cw)
		})
	}()
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
			gs.Loop.Defer(func() {
				for id, c := range gs.worldConns {
					if c == cw {
						delete(gs.worldConns, id)
						log.Printf("[gateway] world connection lost: %s", id)
					}
				}
			})
			return
		}
		if !gs.Loop.Post(common.Event{Kind: common.EventMessage, Conn: cw, Msg: msg}) {
			cw.Close()
			return
		}
	}
}

func (gs *gatewayServer) HandleClientHeartbeat(conn *common.ConnWrapper, req *common.ClientHeartbeatReq) {
	session, ok := gs.sessions[conn]
	if ok {
		session.LastHeartbeat = time.Now()
	}
	if !ok {
		return
	}
	rsp := common.ClientHeartbeatRsp{ServerTimeMs: time.Now().UnixMilli(), ClientTimeMs: req.ClientTimeMs}
	common.SendMsg(conn, common.Gw2Cli_HeartbeatRsp, &rsp)
}

func (gs *gatewayServer) MarkSuspect(conn *common.ConnWrapper, session *clientSession) {
	session.State = stateSuspect
	delete(gs.sessions, conn)
	delete(gs.playerConns, session.PlayerId)
	session.SuspectDeadline = time.Now().Add(suspectTimeout)
	gs.recoverableSessions[session.Account] = session
	gs.SendLogoutCleanup(session.PlayerId)
}

func (gs *gatewayServer) HandleClientDisconnect(conn *common.ConnWrapper) {
	session, ok := gs.sessions[conn]
	if !ok {
		return
	}
	if _, loggingOut := gs.loggingOut[session.PlayerId]; loggingOut {
		return
	}
	gs.MarkSuspect(conn, session)
	account := session.Account
	log.Printf("[gateway] client disconnect: account=%s player=%d -> SUSPECT", account, session.PlayerId)
}

func (gs *gatewayServer) HandleSuspectTimeout(account string) {
	session, ok := gs.recoverableSessions[account]
	if ok {
		delete(gs.recoverableSessions, account)
	}
	if !ok {
		return
	}
	log.Printf("[gateway] session DEAD: account=%s player=%d, full logout", account, session.PlayerId)

	gs.SendDestroyAndArmTimer(session.PlayerId, session.WorldId)
}
