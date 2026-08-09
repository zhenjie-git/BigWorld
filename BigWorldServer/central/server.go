package main

import (
	"sync"
	"time"

	"bigworld/common"
)

type serverRecord struct {
	info   common.ServerEntry
	conn   *common.ConnWrapper
	lastHB int64 // unix timestamp
}

// AccountStatus represents the login state of an account in central.
type AccountStatus int

const (
	AccountLoggingIn  AccountStatus = iota // login in progress (gateway assigned, waiting for world entity)
	AccountOnline                          // login complete, player entity exists
	AccountWaitingKick                     // waiting for old player to be force-kicked before continuing login
)

// accountState tracks an account's login state.
// Created at GatewayAssign, transitions to AccountOnline at LoginFinish (success).
// During AccountWaitingKick, PendingReqId and PendingLoginConn hold the pending
// gateway assign request that will be continued after the old player is cleaned up.
type accountState struct {
	Account   string
	GatewayId string
	WorldId   string // assigned at LoginPrepare
	PlayerId  uint64 // set by world, reported at LoginFinish
	Status    AccountStatus

	// Only meaningful during AccountWaitingKick.
	PendingReqId     uint64
	PendingLoginConn *common.ConnWrapper
}

// onlinePlayer tracks a player's online session. Only created on successful login.
type onlinePlayer struct {
	PlayerId  uint64
	Account   string
	WorldId   string
	GatewayId string
}

type centralServer struct {
	*common.ServerBase
	mu      sync.RWMutex
	servers map[string]*serverRecord

	// Reverse lookup: connection -> server ID
	connToID map[*common.ConnWrapper]string

	// Online player tracking (playerID -> info). Only populated on successful login.
	onlinePlayers map[uint64]*onlinePlayer

	// Account login state (account -> state). Created at GatewayAssign, cleaned up on logout/timeout.
	accountStates map[string]*accountState

	// Login gateway timeout timers: account -> timer
	loginTimers map[string]*time.Timer

	// Force-kick timeout timers: oldPlayerId -> timer
	forceKickTimers map[uint64]*time.Timer

	// Graceful shutdown state.
	shuttingDown bool
	shutdownAcks  map[string]chan struct{} // serverId -> done channel (see shutdown.go)

	// Message router for peer (server) connections.
	peerRouter *common.MessageRouter
}

func newCentralServer(id string) *centralServer {
	cs := &centralServer{
		ServerBase:      common.NewServerBase(common.ServerCentral, id),
		servers:         make(map[string]*serverRecord),
		connToID:        make(map[*common.ConnWrapper]string),
		onlinePlayers:   make(map[uint64]*onlinePlayer),
		accountStates:   make(map[string]*accountState),
		loginTimers:     make(map[string]*time.Timer),
		forceKickTimers: make(map[uint64]*time.Timer),
		shutdownAcks:    make(map[string]chan struct{}),
		peerRouter:      common.NewMessageRouter(),
	}

	common.Register(cs.peerRouter, common.Srv2Ct_RegisterReq, cs.handleRegister)
	common.Register(cs.peerRouter, common.Srv2Ct_HeartbeatReq, cs.handleHeartbeat)
	common.Register(cs.peerRouter, common.Srv2Ct_ServerListReq, cs.handleServerList)
	common.Register(cs.peerRouter, common.Srv2Ct_ShutdownReq, cs.handleShutdown)
	common.Register(cs.peerRouter, common.Srv2Ct_ShutdownAck, cs.handleShutdownAck)
	common.Register(cs.peerRouter, common.Lg2Ct_GatewayAssignReq, cs.handleGatewayAssign)
	common.Register(cs.peerRouter, common.Gw2Ct_LoginPrepareReq, cs.handleLoginPrepareReq)
	common.Register(cs.peerRouter, common.Gw2Ct_LoginFinishReq, cs.handleLoginFinishReq)
	common.Register(cs.peerRouter, common.Gw2Ct_LogoutBeginReq, cs.handleLogoutBeginReq)
	common.Register(cs.peerRouter, common.Gw2Ct_LogoutCleanupReq, cs.handleLogoutCleanupReq)

	cs.OnMessage = cs.handleMessage
	return cs
}

func (cs *centralServer) handleMessage(conn *common.ConnWrapper, msg common.Message) {
	cs.peerRouter.Dispatch(conn, msg)
}
