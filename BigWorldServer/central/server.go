package main

import (
	"sort"
	"time"

	"bigworld/common"
)

type serverRecord struct {
	info           common.ServerEntry
	conn           *common.ConnWrapper
	lastHB         int64
	assignedScenes map[string]bool
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
	SceneID    string
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
	SceneID   string
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
	sceneOwners      map[string][]string
	sceneRules       map[string]common.SceneRuleConfig
	defaultScene     string
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
		sceneOwners:        make(map[string][]string),
		sceneRules:         make(map[string]common.SceneRuleConfig),
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

	for _, rule := range common.Config.SceneRules {
		cs.sceneRules[rule.SceneID] = rule
		if rule.IsDefault {
			cs.defaultScene = rule.SceneID
		}
	}
	if cs.defaultScene == "" {
		defaultPriority := -1
		for _, rule := range common.Config.SceneRules {
			if rule.Priority > defaultPriority {
				defaultPriority = rule.Priority
				cs.defaultScene = rule.SceneID
			}
		}
	}

	cs.Loop.OnTick = cs.OnTick
	cs.Loop.TickEvery = time.Second
	cs.OnDisconnect = cs.HandleServerDisconnect
	cs.OnPeerHello = cs.HandlePeerHello
	return cs
}

func (cs *centralServer) SceneRule(scene string) common.SceneRuleConfig {
	r, ok := cs.sceneRules[scene]
	if !ok {
		r = common.SceneRuleConfig{SceneID: scene, SpreadGroup: scene, MinReplicas: 1}
	}
	if r.SpreadGroup == "" {
		r.SpreadGroup = scene
	}
	if r.MinReplicas < 1 {
		r.MinReplicas = 1
	}
	return r
}

func (cs *centralServer) RebalanceScenes() {
	worlds := make([]string, 0)
	for id, rec := range cs.servers {
		if rec.info.ServerType == common.ServerWorld && rec.conn != nil {
			worlds = append(worlds, id)
		}
	}
	if len(worlds) == 0 {
		cs.sceneOwners = make(map[string][]string)
		return
	}
	sort.Strings(worlds)

	sceneSet := make(map[string]bool)
	for scene := range cs.sceneRules {
		sceneSet[scene] = true
	}
	for _, p := range cs.onlinePlayers {
		if p.SceneID != "" {
			sceneSet[p.SceneID] = true
		}
	}
	scenes := make([]string, 0, len(sceneSet))
	for scene := range sceneSet {
		scenes = append(scenes, scene)
	}
	sort.Slice(scenes, func(i, j int) bool {
		pi, pj := cs.SceneRule(scenes[i]).Priority, cs.SceneRule(scenes[j]).Priority
		if pi == pj {
			return scenes[i] < scenes[j]
		}
		return pi > pj
	})

	alive := make(map[string]bool, len(worlds))
	assigned := make(map[string]map[string]bool)
	for _, id := range worlds {
		alive[id] = true
		assigned[id] = make(map[string]bool)
	}
	current := make(map[string]map[string]bool)
	addCurrent := func(scene, id string) {
		if !alive[id] {
			return
		}
		if current[scene] == nil {
			current[scene] = make(map[string]bool)
		}
		current[scene][id] = true
	}
	for _, id := range worlds {
		for scene := range cs.servers[id].assignedScenes {
			addCurrent(scene, id)
		}
	}
	for _, p := range cs.onlinePlayers {
		if p.SceneID != "" {
			addCurrent(p.SceneID, p.WorldID)
		}
	}

	groupUsed := make(map[string]map[string]bool)
	for _, scene := range scenes {
		rule := cs.SceneRule(scene)
		owners := make(map[string]bool)
		if cs.OnlineCountByScene(scene) > 0 {
			for id := range current[scene] {
				owners[id] = true
			}
		}
		for len(owners) < rule.MinReplicas {
			pick := cs.PickSpreadWorld(owners, rule, groupUsed)
			if pick == "" {
				break
			}
			owners[pick] = true
		}
		for id := range owners {
			assigned[id][scene] = true
			if groupUsed[id] == nil {
				groupUsed[id] = make(map[string]bool)
			}
			groupUsed[id][rule.SpreadGroup] = true
		}
	}

	for _, id := range worlds {
		cs.servers[id].assignedScenes = assigned[id]
	}
	cs.RebuildSceneOwners()
}

func (cs *centralServer) PickSpreadWorld(owners map[string]bool, rule common.SceneRuleConfig, groupUsed map[string]map[string]bool) string {
	best := ""
	bestScore := -1
	for id, rec := range cs.servers {
		if rec.info.ServerType != common.ServerWorld || rec.conn == nil || owners[id] {
			continue
		}
		score := cs.OnlineCountByWorld(id) * 1000
		if groupUsed[id][rule.SpreadGroup] {
			score += 100000
		}
		if best == "" || score < bestScore || (score == bestScore && id < best) {
			best = id
			bestScore = score
		}
	}
	return best
}
func (cs *centralServer) RebuildSceneOwners() {
	owners := make(map[string][]string)
	for id, rec := range cs.servers {
		if rec.info.ServerType != common.ServerWorld || rec.conn == nil {
			continue
		}
		for scene := range rec.assignedScenes {
			owners[scene] = append(owners[scene], id)
		}
	}
	cs.sceneOwners = owners
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

func (cs *centralServer) OnlineCountByScene(scene string) int {
	n := 0
	for _, p := range cs.onlinePlayers {
		if p.SceneID == scene {
			n++
		}
	}
	return n
}

func (cs *centralServer) PickSceneWorld(scene, exclude string) (string, string, bool) {
	best := ""
	bestLoad := -1
	for _, id := range cs.sceneOwners[scene] {
		if id == exclude {
			continue
		}
		rec, ok := cs.servers[id]
		if !ok || rec.conn == nil || rec.info.ServerType != common.ServerWorld {
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

func (cs *centralServer) PickWorldForLogin(preferredWorldID, preferredSceneID string) (string, string) {
	if preferredWorldID != "" {
		if rec, ok := cs.servers[preferredWorldID]; ok && rec.info.ServerType == common.ServerWorld && rec.conn != nil {
			return preferredWorldID, rec.info.ListenAddr
		}
	}
	if preferredSceneID != "" {
		if id, addr, ok := cs.PickSceneWorld(preferredSceneID, ""); ok {
			return id, addr
		}
	}
	if cs.defaultScene != "" {
		if id, addr, ok := cs.PickSceneWorld(cs.defaultScene, ""); ok {
			return id, addr
		}
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
	cs.RebalanceScenes()

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
