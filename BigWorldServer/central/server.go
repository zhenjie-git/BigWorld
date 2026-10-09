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
	AccountTransferring
)

type accountState struct {
	Account    string
	GatewayID  string
	WorldID    string
	SceneID    int64
	TransferID string
	PlayerID   uint64
	SessionID  string
	Status     AccountStatus

	PendingReqID     uint64
	PendingLoginConn *common.ConnWrapper
}

type onlinePlayer struct {
	PlayerID  uint64
	Account   string
	WorldID   string
	SceneID   int64
	GatewayID string
	SessionID string
}

type forceKickDeadline struct {
	gatewayID string
	sessionID string
	account   string
	deadline  time.Time
}

type centralServer struct {
	*common.ServerBase
	servers          map[string]*serverRecord
	transfers        map[string]*transferRecord
	transferByReq    map[uint64]string
	transferByPlayer map[uint64]string
	transferSeq      uint64
	connToID         map[*common.ConnWrapper]string
	onlinePlayers    map[uint64]*onlinePlayer
	accountStates    map[string]*accountState

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
		transfers:          make(map[string]*transferRecord),
		transferByReq:      make(map[uint64]string),
		transferByPlayer:   make(map[uint64]string),
		connToID:           make(map[*common.ConnWrapper]string),
		onlinePlayers:      make(map[uint64]*onlinePlayer),
		accountStates:      make(map[string]*accountState),
		loginDeadlines:     make(map[string]time.Time),
		forceKickDeadlines: make(map[uint64]forceKickDeadline),
		shutdownPending:    make(map[string]bool),
	}

	for _, src := range common.ServerSrcs {
		r := cs.Router(src)
		common.Register(r, common.Srv2Ct_HeartbeatReq, cs.HandleHeartbeatReq)
		common.Register(r, common.Srv2Ct_ServerListReq, cs.HandleServerListReq)
		common.Register(r, common.Srv2Ct_ShutdownReq, cs.HandleShutdownReq)
		common.Register(r, common.Srv2Ct_ShutdownAck, cs.HandleShutdownAck)
	}

	common.Register(cs.Router(common.SrcLogin), common.Lg2Ct_GatewayAssignReq, cs.HandleGatewayAssignReq)
	wr := cs.Router(common.SrcWorld)
	common.Register(wr, common.Wd2Ct_TransferPrepareReq, cs.HandleTransferPrepareReq)
	common.Register(wr, common.Wd2Ct_TransferIncomingAck, cs.HandleTransferIncomingAck)
	common.Register(wr, common.Wd2Ct_TransferReadyReq, cs.HandleTransferReadyReq)
	common.Register(wr, common.Wd2Ct_TransferAbortReq, cs.HandleTransferAbortReq)

	gr := cs.Router(common.SrcGateway)
	common.Register(gr, common.Gw2Ct_LoginPrepareReq, cs.HandleLoginPrepareReq)
	common.Register(gr, common.Gw2Ct_LoginFinishReq, cs.HandleLoginFinishReq)
	common.Register(gr, common.Gw2Ct_LogoutBeginReq, cs.HandleLogoutBeginReq)
	common.Register(gr, common.Gw2Ct_LogoutCleanupReq, cs.HandleLogoutCleanupReq)

	cs.Loop.OnTick = cs.OnTick
	cs.Loop.TickEvery = time.Second
	cs.OnDisconnect = cs.HandleServerDisconnect
	cs.OnPeerHello = cs.HandlePeerHello
	return cs
}

func (cs *centralServer) WorldHostingScene(sceneID int64) string {
	if sceneID == 0 {
		return ""
	}
	counts := make(map[string]int)
	for _, p := range cs.onlinePlayers {
		if p.SceneID == sceneID {
			counts[p.WorldID]++
		}
	}
	best := ""
	for worldID, count := range counts {
		if best == "" || count > counts[best] || (count == counts[best] && worldID < best) {
			best = worldID
		}
	}
	return best
}

func (cs *centralServer) PickLeastLoadedWorld() (string, string, bool) {
	best := ""
	bestLoad := -1
	for id, rec := range cs.servers {
		if rec.info.ServerType != common.ServerWorld || rec.conn == nil {
			continue
		}
		load := cs.OnlineCountByWorld(id)
		if best == "" || load < bestLoad || (load == bestLoad && id < best) {
			best = id
			bestLoad = load
		}
	}
	if best == "" {
		return "", "", false
	}
	return best, cs.servers[best].info.ListenAddr, true
}

func (cs *centralServer) PickZoneWorld(sceneID int64, selfID string) (worldID, addr string, local bool, ok bool) {
	if host := cs.WorldHostingScene(sceneID); host != "" {
		rec, alive := cs.servers[host]
		if !alive || rec.conn == nil {
			host = ""
		} else {
			return host, rec.info.ListenAddr, host == selfID, true
		}
	}
	worldID, addr, ok = cs.PickLeastLoadedWorld()
	return worldID, addr, worldID == selfID, ok
}

func (cs *centralServer) OnlineCountByWorld(worldID string) int {
	n := 0
	for _, p := range cs.onlinePlayers {
		if p.WorldID == worldID {
			n++
		}
	}
	return n
}

func (cs *centralServer) PickWorldForLogin(preferredWorldID string, preferredSceneID int64) (string, string) {
	host := cs.WorldHostingScene(preferredSceneID)
	if preferredWorldID != "" {
		if rec, ok := cs.servers[preferredWorldID]; ok && rec.info.ServerType == common.ServerWorld && rec.conn != nil {
			if host == "" || host == preferredWorldID {
				return preferredWorldID, rec.info.ListenAddr
			}
		}
	}
	if host != "" {
		if rec, ok := cs.servers[host]; ok && rec.conn != nil {
			return host, rec.info.ListenAddr
		}
	}
	if id, addr, ok := cs.PickLeastLoadedWorld(); ok {
		return id, addr
	}
	for id, rec := range cs.servers {
		if rec.info.ServerType == common.ServerWorld && rec.conn != nil {
			return id, rec.info.ListenAddr
		}
	}
	return "", ""
}

func (cs *centralServer) OnTick() {
	now := time.Now()
	cs.CheckTransferTimeouts(now)

	for account, deadline := range cs.loginDeadlines {
		if now.After(deadline) {
			cs.HandleLoginTimeout(account)
		}
	}

	for playerID, fk := range cs.forceKickDeadlines {
		if now.After(fk.deadline) {
			cs.HandleForceKickTimeout(playerID, fk.gatewayID, fk.sessionID, fk.account)
		}
	}

	cs.ReapStale(25 * time.Second)

	if cs.shuttingDown && len(cs.shutdownPending) > 0 && now.After(cs.shutdownDeadline) {
		cs.AdvanceShutdownStage(len(cs.shutdownPending))
	}
}
