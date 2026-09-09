package main

import (
	"time"

	"bigworld/common"
)

type serverRecord struct {
	info   common.ServerEntry
	conn   *common.ConnWrapper
	lastHB int64
}

type AccountStatus int

const (
	AccountLoggingIn AccountStatus = iota
	AccountOnline
	AccountWaitingKick
)

type accountState struct {
	Account   string
	GatewayId string
	WorldId   string
	PlayerId  uint64
	Status    AccountStatus

	PendingReqId     uint64
	PendingLoginConn *common.ConnWrapper
}

type onlinePlayer struct {
	PlayerId  uint64
	Account   string
	WorldId   string
	GatewayId string
}

type forceKickDeadline struct {
	gatewayId string
	deadline  time.Time
}

type centralServer struct {
	*common.ServerBase
	servers       map[string]*serverRecord
	connToID      map[*common.ConnWrapper]string
	onlinePlayers map[uint64]*onlinePlayer
	accountStates map[string]*accountState

	loginDeadlines map[string]time.Time

	forceKickDeadlines map[uint64]forceKickDeadline

	shuttingDown     bool
	shutdownStage    int
	shutdownPending  map[string]bool
	shutdownDeadline time.Time
}

func NewCentralServer(id string) *centralServer {
	cs := &centralServer{
		ServerBase:         common.NewServerBase(common.ServerCentral, id),
		servers:            make(map[string]*serverRecord),
		connToID:           make(map[*common.ConnWrapper]string),
		onlinePlayers:      make(map[uint64]*onlinePlayer),
		accountStates:      make(map[string]*accountState),
		loginDeadlines:     make(map[string]time.Time),
		forceKickDeadlines: make(map[uint64]forceKickDeadline),
		shutdownPending:    make(map[string]bool),
	}

	for _, src := range common.ServerSrcs {
		r := cs.Router(src)
		common.Register(r, common.Srv2Ct_RegisterReq, cs.HandleRegister)
		common.Register(r, common.Srv2Ct_HeartbeatReq, cs.HandleHeartbeat)
		common.Register(r, common.Srv2Ct_ServerListReq, cs.HandleServerList)
		common.Register(r, common.Srv2Ct_ShutdownReq, cs.HandleShutdown)
		common.Register(r, common.Srv2Ct_ShutdownAck, cs.HandleShutdownAck)
	}

	common.Register(cs.Router(common.SrcLogin), common.Lg2Ct_GatewayAssignReq, cs.HandleGatewayAssign)
	gr := cs.Router(common.SrcGateway)
	common.Register(gr, common.Gw2Ct_LoginPrepareReq, cs.HandleLoginPrepareReq)
	common.Register(gr, common.Gw2Ct_LoginFinishReq, cs.HandleLoginFinishReq)
	common.Register(gr, common.Gw2Ct_LogoutBeginReq, cs.HandleLogoutBeginReq)
	common.Register(gr, common.Gw2Ct_LogoutCleanupReq, cs.HandleLogoutCleanupReq)

	cs.Loop.OnTick = cs.OnTick
	cs.Loop.TickEvery = time.Second
	return cs
}

func (cs *centralServer) OnTick() {
	now := time.Now()

	for account, deadline := range cs.loginDeadlines {
		if now.After(deadline) {
			cs.HandleLoginTimeout(account)
		}
	}

	for playerID, fk := range cs.forceKickDeadlines {
		if now.After(fk.deadline) {
			cs.HandleForceKickTimeout(playerID, fk.gatewayId)
		}
	}

	cs.ReapStale(25 * time.Second)

	if cs.shuttingDown && len(cs.shutdownPending) > 0 && now.After(cs.shutdownDeadline) {
		cs.AdvanceShutdownStage(len(cs.shutdownPending))
	}
}
