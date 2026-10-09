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
	heartbeatTimeout    = 10 * time.Second
	logoutTimeout       = 10 * time.Second
	suspectTimeout      = 30 * time.Second
)

type sessionState int

const (
	sessionPreparing sessionState = iota
	sessionCreating
	sessionFinishing
	sessionResuming
	sessionResumingWorld
	sessionActive
	sessionSuspect
	sessionLoggingOut
	sessionDestroying
)

type loginSnapshot struct {
	SceneID int64
	X, Z    float64
	Width   float64
	Height  float64
}

type clientSession struct {
	Conn      *common.ConnWrapper
	Account   string
	PlayerID  uint64
	SessionID string
	ReqID     uint64

	WorldID   string
	WorldAddr string
	SceneID   int64

	Login *loginSnapshot

	State         sessionState
	Deadline      time.Time
	LastHeartbeat time.Time

	SuspectDeadline time.Time
	LogoutDeadline  time.Time
	DestroyDeadline time.Time

	LogoutCleanupStarted bool
	CleanupSent          bool
}

type gatewayServer struct {
	*common.ServerBase
	reqCounter uint64

	byConn    map[*common.ConnWrapper]*clientSession
	byAccount map[string]*clientSession
	byPlayer  map[uint64]*clientSession

	worldConns      map[string]*common.ConnWrapper
	worldAddrs      map[string]string
	worldConnecting map[string]bool
	worldMu         sync.Mutex
}

func NewGatewayServer(id string) *gatewayServer {
	gs := &gatewayServer{
		ServerBase:      common.NewServerBase(common.ServerGateway, id),
		byConn:          make(map[*common.ConnWrapper]*clientSession),
		byAccount:       make(map[string]*clientSession),
		byPlayer:        make(map[uint64]*clientSession),
		worldConns:      make(map[string]*common.ConnWrapper),
		worldAddrs:      make(map[string]string),
		worldConnecting: make(map[string]bool),
	}

	common.Register(gs.Router(common.SrcClient), common.Cli2Gw_LoginReq, gs.HandleClientLoginReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_WalkStartReq, gs.HandleClientWalkStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_RunStartReq, gs.HandleClientRunStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_SprintStartReq, gs.HandleClientSprintStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_JumpStartReq, gs.HandleClientJumpStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_DashStartReq, gs.HandleClientDashStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_RollStartReq, gs.HandleClientRollStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_StopStartReq, gs.HandleClientStopStartReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_MoveHaltReq, gs.HandleClientMoveHaltReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_MoveDirChangeReq, gs.HandleClientMoveDirChangeReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Wd_SkillReq, gs.HandleClientSkillReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Gw_LogoutReq, gs.HandleClientLogoutReq)
	common.Register(gs.Router(common.SrcClient), common.Cli2Gw_HeartbeatReq, gs.HandleClientHeartbeatReq)

	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LoginPrepareRsp, gs.HandleCentralLoginPrepareRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LoginFinishRsp, gs.HandleCentralLoginFinishRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_ForceKickNotify, gs.HandleCentralForceKickNotify)
	common.Register(gs.Router(common.SrcCentral), common.Srv2Srv_HelloRsp, gs.HandleCentralHelloRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_ServerListRsp, gs.HandleCentralServerListRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_NewWorldNotify, gs.HandleCentralNewWorldNotify)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_TransferCommitNotify, gs.HandleCentralTransferCommitNotify)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LogoutBeginRsp, gs.HandleCentralLogoutBeginRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Gw_LogoutCleanupRsp, gs.HandleCentralLogoutCleanupRsp)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_HeartbeatRsp, gs.NoopHeartbeat)
	common.Register(gs.Router(common.SrcCentral), common.Ct2Srv_ShutdownNotify, gs.HandleCentralShutdownNotify)

	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_MoveRsp, gs.HandleWorldMoveRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_SkillRsp, gs.HandleWorldSkillRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Gw_CreateEntityRsp, gs.HandleWorldCreateEntityRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Gw_DestroyEntityRsp, gs.HandleWorldDestroyEntityRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Gw_ResumeEntityRsp, gs.HandleWorldResumeEntityRsp)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_EnterSceneNotify, gs.HandleWorldEnterSceneNotify)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_EntityEnterNotify, gs.HandleWorldEntityEnterNotify)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_EntityLeaveNotify, gs.HandleWorldEntityLeaveNotify)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_EntityMoveEventNotify, gs.HandleWorldEntityMoveEventNotify)
	common.Register(gs.Router(common.SrcWorld), common.Wd2Cli_EntitySnapshotNotify, gs.HandleWorldEntitySnapshotNotify)

	gs.OnDisconnect = gs.HandleClientDisconnect
	gs.Loop.OnTick = gs.OnTick
	gs.Loop.TickEvery = 2 * time.Second
	return gs
}

func (gs *gatewayServer) NextReqID() uint64 {
	gs.reqCounter++
	return gs.reqCounter
}

func (gs *gatewayServer) GetByConn(conn *common.ConnWrapper) *clientSession {
	if conn == nil {
		return nil
	}
	return gs.byConn[conn]
}

func (gs *gatewayServer) GetByAccount(account string) *clientSession {
	if account == "" {
		return nil
	}
	return gs.byAccount[account]
}

func (gs *gatewayServer) GetByPlayer(playerID uint64) *clientSession {
	if playerID == 0 {
		return nil
	}
	return gs.byPlayer[playerID]
}

func (gs *gatewayServer) AttachConn(session *clientSession, conn *common.ConnWrapper) {
	if session == nil {
		return
	}
	session.Conn = conn
	if conn != nil {
		gs.byConn[conn] = session
	}
}

func (gs *gatewayServer) DetachConn(session *clientSession) {
	if session == nil {
		return
	}
	if session.Conn != nil && gs.byConn[session.Conn] == session {
		delete(gs.byConn, session.Conn)
	}
	session.Conn = nil
}

func (gs *gatewayServer) SetAccount(session *clientSession, account string) {
	if session == nil || account == "" {
		return
	}
	session.Account = account
	gs.byAccount[account] = session
}

func (gs *gatewayServer) DetachAccount(session *clientSession) {
	if session == nil || session.Account == "" {
		return
	}
	if gs.byAccount[session.Account] == session {
		delete(gs.byAccount, session.Account)
	}
}

func (gs *gatewayServer) AttachPlayer(session *clientSession) {
	if session == nil || session.PlayerID == 0 {
		return
	}
	gs.byPlayer[session.PlayerID] = session
}

func (gs *gatewayServer) DetachPlayer(session *clientSession) {
	if session == nil || session.PlayerID == 0 {
		return
	}
	if gs.byPlayer[session.PlayerID] == session {
		delete(gs.byPlayer, session.PlayerID)
	}
}

func (gs *gatewayServer) RemoveSession(session *clientSession) {
	if session == nil {
		return
	}
	gs.DetachConn(session)
	gs.DetachAccount(session)
	gs.DetachPlayer(session)
}

func (gs *gatewayServer) FailClientLogin(conn *common.ConnWrapper, message string) {
	if conn == nil {
		return
	}
	rsp := common.LoginRsp{Success: false, Message: message}
	common.SendMsg(conn, common.Gw2Cli_LoginRsp, &rsp)
	conn.CloseAfterSend()
}

func (gs *gatewayServer) FailSession(session *clientSession, message string) {
	if session == nil {
		return
	}
	conn := session.Conn
	gs.RemoveSession(session)
	if conn != nil {
		gs.FailClientLogin(conn, message)
	}
}

func (gs *gatewayServer) SendLoginFinishFailure(account, sessionID, message string) {
	req := common.LoginFinishReq{
		Success:   false,
		Account:   account,
		GatewayId: gs.ServerID,
		SessionId: sessionID,
		Message:   message,
	}
	gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &req)
}

func (gs *gatewayServer) SendLogoutCleanup(session *clientSession) {
	if session == nil {
		return
	}
	req := common.LogoutCleanupReq{
		PlayerId:  session.PlayerID,
		GatewayId: gs.ServerID,
		SessionId: session.SessionID,
	}
	gs.SendToCentralMsg(common.Gw2Ct_LogoutCleanupReq, &req)
}

func (gs *gatewayServer) EnterDestroy(session *clientSession, worldID string) {
	if session == nil || session.PlayerID == 0 {
		gs.RemoveSession(session)
		return
	}
	if worldID != "" {
		session.WorldID = worldID
	}
	session.State = sessionDestroying
	session.DestroyDeadline = time.Now().Add(destroyTimeout)

	req := common.DestroyEntityReq{
		PlayerId:  session.PlayerID,
		GatewayId: gs.ServerID,
		SessionId: session.SessionID,
	}
	if err := gs.ForwardMsgToWorld(session.WorldID, common.Gw2Wd_DestroyEntityReq, &req); err != nil {
		log.Printf("[gateway] destroy send failed for player %d session=%s: %v", session.PlayerID, session.SessionID, err)
		gs.SendLogoutCleanup(session)
		session.CleanupSent = true
		session.DestroyDeadline = time.Now().Add(logoutTimeout)
	}
}

func (gs *gatewayServer) StartLogoutCleanup(session *clientSession) {
	if session == nil || session.LogoutCleanupStarted {
		return
	}
	session.LogoutCleanupStarted = true
	session.Deadline = time.Now().Add(logoutTimeout)
	if session.WorldID == "" {
		gs.SendLogoutCleanup(session)
		return
	}
	gs.EnterDestroy(session, session.WorldID)
}

func (gs *gatewayServer) ForceRemoveSession(session *clientSession) {
	if session == nil {
		return
	}
	conn := session.Conn
	gs.RemoveSession(session)
	if conn != nil {
		gs.FailClientLogin(conn, "session closed")
	}
}

func (gs *gatewayServer) OnTick() {
	now := time.Now()
	for _, session := range gs.byAccount {
		switch session.State {
		case sessionPreparing:
			if now.After(session.Deadline) {
				gs.HandleLoginPrepareTimeout(session)
			}
		case sessionCreating, sessionFinishing:
			if now.After(session.Deadline) {
				gs.HandleCreateEntityTimeout(session)
			}
		case sessionResuming, sessionResumingWorld:
			if now.After(session.Deadline) {
				gs.HandleResumeTimeout(session)
			}
		case sessionActive:
			if now.Sub(session.LastHeartbeat) > heartbeatTimeout {
				gs.MarkSuspect(session)
				log.Printf("[gateway] session SUSPECT: account=%s player=%d session=%s",
					session.Account, session.PlayerID, session.SessionID)
			}
		case sessionSuspect:
			if now.After(session.SuspectDeadline) {
				gs.HandleSuspectTimeout(session)
			}
		case sessionLoggingOut:
			if !session.LogoutCleanupStarted {
				if !session.LogoutDeadline.IsZero() && now.After(session.LogoutDeadline) {
					gs.StartLogoutCleanup(session)
				}
			} else if !session.Deadline.IsZero() && now.After(session.Deadline) {
				gs.ForceRemoveSession(session)
			}
		case sessionDestroying:
			if !session.DestroyDeadline.IsZero() && now.After(session.DestroyDeadline) {
				gs.HandleDestroyTimeout(session)
			}
		}
	}
}
func (gs *gatewayServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (gs *gatewayServer) HandleClientLoginReq(conn *common.ConnWrapper, req *common.LoginReq) {
	token := req.Account
	if token == "" {
		gs.FailClientLogin(conn, "empty token")
		return
	}

	account, err := common.VerifyToken(token)
	if err != nil {
		log.Printf("[gateway] token verify failed: %v", err)
		gs.FailClientLogin(conn, "invalid or reused token")
		return
	}
	log.Printf("[gateway] token verified locally: account=%s", account)

	if old := gs.GetByAccount(account); old != nil {
		if old.State != sessionSuspect {
			gs.FailClientLogin(conn, "account already logging in")
			return
		}
		old.State = sessionResuming
		old.Deadline = time.Now().Add(createEntityTimeout)
		old.LastHeartbeat = time.Now()
		gs.AttachConn(old, conn)
		finishReq := common.LoginFinishReq{
			PlayerId:  old.PlayerID,
			Success:   true,
			Account:   account,
			WorldId:   old.WorldID,
			GatewayId: gs.ServerID,
			SessionId: old.SessionID,
		}
		if err := gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq); err != nil {
			gs.FailSession(old, "login service unavailable")
			return
		}
		log.Printf("[gateway] requesting central resume ack: account=%s player=%d session=%s",
			account, old.PlayerID, old.SessionID)
		return
	}
	reqID := gs.NextReqID()
	session := &clientSession{
		Account:   account,
		SessionID: common.NewSessionID(),
		ReqID:     reqID,
		State:     sessionPreparing,
		Deadline:  time.Now().Add(loginPrepareTimeout),
	}
	gs.AttachConn(session, conn)
	gs.SetAccount(session, account)

	prepareReq := common.LoginPrepareReq{ReqId: reqID, Account: account, SessionId: session.SessionID}
	if err := gs.SendToCentralMsg(common.Gw2Ct_LoginPrepareReq, &prepareReq); err != nil {
		gs.FailSession(session, "login service unavailable")
		return
	}
	log.Printf("[gateway] sent LoginPrepareReq reqID=%d session=%s to central for account=%s", reqID, session.SessionID, account)
}

func (gs *gatewayServer) ForwardClientMove(conn *common.ConnWrapper, msgType common.MessageType, setPlayerID func(uid uint64), m proto.Message) {
	session := gs.GetByConn(conn)
	if session == nil || session.State != sessionActive {
		return
	}
	setPlayerID(session.PlayerID)
	if err := gs.ForwardMsgToWorld(session.WorldID, msgType, m); err != nil {
		common.SendMsg(session.Conn, common.Wd2Cli_MoveRsp, &common.MoveRsp{Success: false, PlayerId: session.PlayerID, Message: "world unavailable"})
	}
}

func (gs *gatewayServer) HandleClientWalkStartReq(conn *common.ConnWrapper, req *common.WalkStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_WalkStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientRunStartReq(conn *common.ConnWrapper, req *common.RunStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_RunStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientSprintStartReq(conn *common.ConnWrapper, req *common.SprintStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_SprintStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientJumpStartReq(conn *common.ConnWrapper, req *common.JumpStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_JumpStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientDashStartReq(conn *common.ConnWrapper, req *common.DashStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_DashStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientRollStartReq(conn *common.ConnWrapper, req *common.RollStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_RollStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientStopStartReq(conn *common.ConnWrapper, req *common.StopStartReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_StopStartReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientMoveHaltReq(conn *common.ConnWrapper, req *common.MoveHaltReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_MoveHaltReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientMoveDirChangeReq(conn *common.ConnWrapper, req *common.MoveDirChangeReq) {
	gs.ForwardClientMove(conn, common.Cli2Wd_MoveDirChangeReq, func(uid uint64) { req.PlayerId = uid }, req)
}

func (gs *gatewayServer) HandleClientSkillReq(conn *common.ConnWrapper, req *common.SkillReq) {
	session := gs.GetByConn(conn)
	if session == nil || session.State != sessionActive {
		return
	}
	req.PlayerId = session.PlayerID
	if err := gs.ForwardMsgToWorld(session.WorldID, common.Cli2Wd_SkillReq, req); err != nil {
		common.SendMsg(session.Conn, common.Wd2Cli_SkillRsp, &common.SkillRsp{Success: false, PlayerId: req.PlayerId, Message: "world unavailable"})
	}
}

func (gs *gatewayServer) HandleClientLogoutReq(conn *common.ConnWrapper, _ *common.LogoutReq) {
	session := gs.GetByConn(conn)
	if session == nil || session.State != sessionActive {
		return
	}

	session.State = sessionLoggingOut
	session.LogoutDeadline = time.Now().Add(logoutTimeout)
	session.LogoutCleanupStarted = false

	req := common.LogoutBeginReq{
		PlayerId:  session.PlayerID,
		GatewayId: gs.ServerID,
		SessionId: session.SessionID,
	}
	if err := gs.SendToCentralMsg(common.Gw2Ct_LogoutBeginReq, &req); err != nil {
		gs.StartLogoutCleanup(session)
	}
}

func (gs *gatewayServer) HandleLoginPrepareTimeout(session *clientSession) {
	if session == nil || session.State != sessionPreparing {
		return
	}
	gs.SendLoginFinishFailure(session.Account, session.SessionID, "login prepare timeout")
	gs.FailSession(session, "login prepare timeout")
}

func (gs *gatewayServer) HandleResumeTimeout(session *clientSession) {
	if session == nil {
		return
	}
	if session.State == sessionResumingWorld {
		gs.SendLogoutCleanup(session)
	}
	gs.FailSession(session, "resume timeout")
}
func (gs *gatewayServer) HandleCreateEntityTimeout(session *clientSession) {
	if session == nil || (session.State != sessionCreating && session.State != sessionFinishing) {
		return
	}
	gs.SendLoginFinishFailure(session.Account, session.SessionID, "create entity timeout")
	gs.CancelLoginSession(session, "create entity timeout")
}

func (gs *gatewayServer) CancelLoginSession(session *clientSession, message string) {
	if session == nil {
		return
	}
	conn := session.Conn
	gs.DetachConn(session)
	if conn != nil {
		gs.FailClientLogin(conn, message)
	}
	if session.PlayerID != 0 && session.WorldID != "" && (gs.byPlayer[session.PlayerID] == nil || gs.byPlayer[session.PlayerID] == session) {
		gs.AttachPlayer(session)
		gs.EnterDestroy(session, session.WorldID)
		return
	}
	gs.RemoveSession(session)
}

func (gs *gatewayServer) HandleWorldMoveRsp(_ *common.ConnWrapper, rsp *common.MoveRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil || session.State != sessionActive || session.Conn == nil {
		return
	}

	common.SendMsg(session.Conn, common.Wd2Cli_MoveRsp, rsp)
}

func (gs *gatewayServer) HandleWorldSkillRsp(_ *common.ConnWrapper, rsp *common.SkillRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil || session.State != sessionActive || session.Conn == nil {
		return
	}
	common.SendMsg(session.Conn, common.Wd2Cli_SkillRsp, rsp)
}

func (gs *gatewayServer) forwardEntityMsg(targetPlayerID uint64, msgType common.MessageType, m proto.Message) {
	session := gs.GetByPlayer(targetPlayerID)
	if session == nil || session.State != sessionActive || session.Conn == nil {
		return
	}
	common.SendMsg(session.Conn, msgType, m)
}

func (gs *gatewayServer) HandleWorldEntityEnterNotify(_ *common.ConnWrapper, notify *common.EntityEnterNotify) {
	gs.forwardEntityMsg(notify.TargetPlayerId, common.Wd2Cli_EntityEnterNotify, notify)
}

func (gs *gatewayServer) HandleWorldEntityLeaveNotify(_ *common.ConnWrapper, notify *common.EntityLeaveNotify) {
	gs.forwardEntityMsg(notify.TargetPlayerId, common.Wd2Cli_EntityLeaveNotify, notify)
}

func (gs *gatewayServer) HandleWorldEntityMoveEventNotify(_ *common.ConnWrapper, notify *common.EntityMoveEventNotify) {
	gs.forwardEntityMsg(notify.TargetPlayerId, common.Wd2Cli_EntityMoveEventNotify, notify)
}

func (gs *gatewayServer) HandleWorldEntitySnapshotNotify(_ *common.ConnWrapper, notify *common.EntitySnapshotNotify) {
	gs.forwardEntityMsg(notify.TargetPlayerId, common.Wd2Cli_EntitySnapshotNotify, notify)
}

func (gs *gatewayServer) HandleWorldCreateEntityRsp(_ *common.ConnWrapper, rsp *common.CreateEntityRsp) {
	session := gs.GetByAccount(rsp.Account)
	if session == nil {
		return
	}
	if rsp.SessionId != "" && session.SessionID != "" && rsp.SessionId != session.SessionID {
		log.Printf("[gateway] ignoring stale CreateEntityRsp for account=%s session=%s", rsp.Account, rsp.SessionId)
		return
	}
	if !rsp.Success {
		gs.SendLoginFinishFailure(rsp.Account, session.SessionID, rsp.Message)
		gs.FailSession(session, rsp.Message)
		return
	}

	session.PlayerID = rsp.PlayerId
	session.SceneID = rsp.SceneId
	session.Login = &loginSnapshot{
		SceneID: rsp.SceneId,
		X:       rsp.X,
		Z:       rsp.Z,
		Width:   rsp.Width,
		Height:  rsp.Height,
	}
	session.State = sessionFinishing
	session.Deadline = time.Now().Add(createEntityTimeout)

	finishReq := common.LoginFinishReq{
		PlayerId:  rsp.PlayerId,
		Success:   true,
		Account:   rsp.Account,
		WorldId:   session.WorldID,
		SceneId:   rsp.SceneId,
		GatewayId: gs.ServerID,
		SessionId: session.SessionID,
	}
	if err := gs.SendToCentralMsg(common.Gw2Ct_LoginFinishReq, &finishReq); err != nil {
		gs.CancelLoginSession(session, "login service unavailable")
	}
}

func (gs *gatewayServer) HandleWorldResumeEntityRsp(_ *common.ConnWrapper, rsp *common.ResumeEntityRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil || session.State != sessionResumingWorld {
		return
	}
	if rsp.SessionId != "" && session.SessionID != "" && rsp.SessionId != session.SessionID {
		return
	}
	if !rsp.Success {
		gs.SendLogoutCleanup(session)
		gs.FailSession(session, rsp.Message)
		return
	}

	session.State = sessionActive
	session.LastHeartbeat = time.Now()
	session.Deadline = time.Time{}
	session.SceneID = rsp.SceneId
	gs.AttachPlayer(session)

	loginRsp := common.LoginRsp{
		Success:   true,
		Message:   rsp.Message,
		WorldAddr: session.WorldAddr,
		WorldId:   session.WorldID,
		PlayerId:  rsp.PlayerId,
		X:         rsp.X,
		Z:         rsp.Z,
		Width:     rsp.Width,
		Height:    rsp.Height,
		SceneId:   rsp.SceneId,
	}
	common.SendMsg(session.Conn, common.Gw2Cli_LoginRsp, &loginRsp)
}
func (gs *gatewayServer) HandleWorldEnterSceneNotify(_ *common.ConnWrapper, notify *common.EnterSceneNotify) {
	session := gs.GetByPlayer(notify.PlayerId)
	if session == nil || session.Conn == nil || session.State != sessionActive {
		return
	}
	session.SceneID = notify.SceneId
	common.SendMsg(session.Conn, common.Wd2Cli_EnterSceneNotify, notify)
}

func (gs *gatewayServer) HandleWorldDestroyEntityRsp(_ *common.ConnWrapper, rsp *common.DestroyEntityRsp) {
	session := gs.GetByPlayer(rsp.PlayerId)
	if session == nil || session.State != sessionDestroying {
		return
	}
	if rsp.SessionId != "" && session.SessionID != "" && rsp.SessionId != session.SessionID {
		log.Printf("[gateway] ignoring mismatched DestroyEntityRsp for player %d session=%s != %s",
			rsp.PlayerId, rsp.SessionId, session.SessionID)
		return
	}
	gs.SendLogoutCleanup(session)
	session.CleanupSent = true
	session.DestroyDeadline = time.Now().Add(logoutTimeout)
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
	if worldID == "" {
		return
	}
	if worldAddr != "" {
		gs.worldMu.Lock()
		gs.worldAddrs[worldID] = worldAddr
		gs.worldMu.Unlock()
	}
	if _, ok := gs.worldConns[worldID]; ok {
		return
	}
	gs.worldMu.Lock()
	if gs.worldConnecting[worldID] {
		gs.worldMu.Unlock()
		return
	}
	gs.worldConnecting[worldID] = true
	gs.worldMu.Unlock()
	go gs.WorldConnectLoop(worldID)
}

func (gs *gatewayServer) WorldConnectLoop(worldID string) {
	backoff := time.Second
	for {
		select {
		case <-gs.Done():
			gs.SetWorldConnecting(worldID, false)
			return
		default:
		}

		addr := gs.WorldEndpoint(worldID)
		if addr == "" {
			gs.SetWorldConnecting(worldID, false)
			return
		}

		conn, err := net.DialTimeout("tcp", addr, 5*time.Second)
		if err != nil {
			log.Printf("[gateway] failed to connect to world %s at %s: %v, retry in %v", worldID, addr, err, backoff)
			select {
			case <-gs.Done():
				return
			case <-time.After(backoff):
			}
			if backoff < 30*time.Second {
				backoff *= 2
			}
			continue
		}

		accepted := make(chan bool, 1)
		posted := gs.Loop.Post(common.Event{Kind: common.EventDefer, Fn: func() {
			current := gs.WorldEndpoint(worldID)
			if _, exists := gs.worldConns[worldID]; exists {
				_ = conn.Close()
				gs.SetWorldConnecting(worldID, false)
				accepted <- true
				return
			}
			if current != "" && current != addr {
				_ = conn.Close()
				accepted <- false
				return
			}
			cw := common.NewConnWrapper(conn)
			cw.PeerType = common.ServerWorld
			gs.worldConns[worldID] = cw
			gs.SetWorldConnecting(worldID, false)
			log.Printf("[gateway] connected to world %s at %s", worldID, addr)
			gs.SendHello(cw)
			go gs.WorldReadLoop(worldID, cw)
			accepted <- true
		}})
		if !posted {
			_ = conn.Close()
			gs.SetWorldConnecting(worldID, false)
			return
		}
		if <-accepted {
			return
		}
	}
}

func (gs *gatewayServer) WorldReadLoop(worldID string, cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[gateway %s] WorldReadLoop panic recovered: %v", gs.ServerID, r)
		}
	}()
	for {
		msg, err := common.ReadMessage(cw.Conn)
		if err != nil {
			gs.ScheduleWorldReconnect(worldID, cw, err)
			return
		}
		if !gs.Loop.Post(common.Event{Kind: common.EventMessage, Conn: cw, Msg: msg}) {
			cw.Close()
			gs.ScheduleWorldReconnect(worldID, cw, nil)
			return
		}
	}
}

func (gs *gatewayServer) ScheduleWorldReconnect(worldID string, cw *common.ConnWrapper, err error) {
	gs.Loop.Defer(func() {
		if gs.worldConns[worldID] == cw {
			delete(gs.worldConns, worldID)
			if err != nil {
				log.Printf("[gateway] world connection lost: %s: %v", worldID, err)
			} else {
				log.Printf("[gateway] world connection lost: %s", worldID)
			}
		}
		gs.DropSessionsOnWorldLost(worldID)
		gs.SetWorldConnecting(worldID, false)
		gs.ConnectToWorld(worldID, "")
	})
}

func (gs *gatewayServer) DropSessionsOnWorldLost(worldID string) {
	var dropped []*clientSession
	for _, session := range gs.byAccount {
		if session.WorldID != worldID {
			continue
		}
		switch session.State {
		case sessionActive, sessionResuming, sessionResumingWorld, sessionCreating, sessionFinishing:
			dropped = append(dropped, session)
		}
	}
	for _, session := range dropped {
		conn := session.Conn
		log.Printf("[gateway] dropping session after world loss: account=%s player=%d world=%s",
			session.Account, session.PlayerID, worldID)
		gs.RemoveSession(session)
		if conn != nil {
			_ = conn.Close()
		}
	}
}

func (gs *gatewayServer) SetWorldConnecting(worldID string, value bool) {
	gs.worldMu.Lock()
	gs.worldConnecting[worldID] = value
	gs.worldMu.Unlock()
}

func (gs *gatewayServer) WorldEndpoint(worldID string) string {
	gs.worldMu.Lock()
	defer gs.worldMu.Unlock()
	return gs.worldAddrs[worldID]
}

func (gs *gatewayServer) HandleClientHeartbeatReq(conn *common.ConnWrapper, req *common.ClientHeartbeatReq) {
	session := gs.GetByConn(conn)
	if session == nil || session.State != sessionActive {
		return
	}
	session.LastHeartbeat = time.Now()
	rsp := common.ClientHeartbeatRsp{ServerTimeMs: time.Now().UnixMilli(), ClientTimeMs: req.ClientTimeMs}
	common.SendMsg(conn, common.Gw2Cli_HeartbeatRsp, &rsp)
}

func (gs *gatewayServer) MarkSuspect(session *clientSession) {
	if session == nil || session.State != sessionActive {
		return
	}
	session.State = sessionSuspect
	session.SuspectDeadline = time.Now().Add(suspectTimeout)
	conn := session.Conn
	gs.DetachConn(session)
	gs.AttachPlayer(session)
	if conn != nil {
		_ = conn.Close()
	}
}

func (gs *gatewayServer) HandleClientDisconnect(conn *common.ConnWrapper) {
	session := gs.GetByConn(conn)
	if session == nil {
		return
	}
	switch session.State {
	case sessionActive:
		gs.MarkSuspect(session)
		log.Printf("[gateway] client disconnect: account=%s player=%d session=%s -> SUSPECT",
			session.Account, session.PlayerID, session.SessionID)
	case sessionLoggingOut:
		log.Printf("[gateway] client disconnected during logout: player=%d session=%s", session.PlayerID, session.SessionID)
		gs.DetachConn(session)
	case sessionPreparing:
		gs.SendLoginFinishFailure(session.Account, session.SessionID, "client disconnected during login")
		gs.RemoveSession(session)
	case sessionCreating, sessionFinishing:
		gs.SendLoginFinishFailure(session.Account, session.SessionID, "client disconnected during login")
		gs.CancelLoginSession(session, "client disconnected during login")
	case sessionResuming:
		gs.SendLoginFinishFailure(session.Account, session.SessionID, "client disconnected during login")
		gs.RemoveSession(session)
	case sessionResumingWorld:
		gs.SendLogoutCleanup(session)
		gs.RemoveSession(session)
	default:
		gs.RemoveSession(session)
	}
}

func (gs *gatewayServer) HandleSuspectTimeout(session *clientSession) {
	if session == nil || session.State != sessionSuspect {
		return
	}
	gs.AttachPlayer(session)
	gs.EnterDestroy(session, session.WorldID)
}

func (gs *gatewayServer) HandleDestroyTimeout(session *clientSession) {
	if session == nil || session.State != sessionDestroying {
		return
	}
	if !session.CleanupSent {
		session.CleanupSent = true
		gs.SendLogoutCleanup(session)
		session.DestroyDeadline = time.Now().Add(logoutTimeout)
		return
	}
	gs.ForceRemoveSession(session)
}
