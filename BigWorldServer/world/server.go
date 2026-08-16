package main

import (
	"fmt"
	"log"
	"net"
	"sync"
	"time"

	"bigworld/common"
	pb "bigworld/common/pb"

	"google.golang.org/protobuf/proto"
)

const (
	loadPlayerTimeout = 8 * time.Second // must be < gateway's createEntityTimeout (10s)
	autosaveInterval  = 30 * time.Second
)

type worldServer struct {
	*common.ServerBase
	mu            sync.Mutex
	players       map[uint64]*playerEntity
	sceneMgr      *sceneMgr
	playerCounter uint64 // only used in the no-dbproxy fallback path
	peerRouter    *common.MessageRouter

	// Server-authoritative movement sim: fixed tick interval and the number
	// of ticks covered by the rollback window (maxMoveWindowMs).
	moveTickMs    int64
	rollbackTicks int64

	// dbproxy connection (discovered via central) and its inbound router.
	dbConn   *common.ConnWrapper
	dbRouter *common.MessageRouter

	// In-flight LoadPlayer requests, keyed by account. The gateway arms a
	// 10s createEntityTimeout, so a LoadPlayer reply must arrive within
	// loadPlayerTimeout or we abandon the create (no orphan entity).
	dbLoadPendings map[string]*dbLoadPending

	// Shutdown flush correlation: FlushAllAndWait sets a req id + channel,
	// HandleDbSavePlayerRsp closes the channel when the matching rsp arrives.
	flushReqID uint64
	flushDone  chan struct{}
}

// dbLoadPending correlates a CreateEntityReq with the async LoadPlayerRsp.
type dbLoadPending struct {
	conn    *common.ConnWrapper // gateway world-conn to reply CreateEntityRsp on
	account string
	timer   *time.Timer
}

// fillMoveRspState copies a complete simulation snapshot into a MoveRsp so the
// client can restore authoritative state and roll back deterministically.
func fillMoveRspState(rsp *common.MoveRsp, s entitySnapshot) {
	rsp.SimTick = s.Tick
	rsp.X = s.X
	rsp.Y = s.Y
	rsp.Z = s.Z
	rsp.State = s.State
	rsp.VoxelK = int32(s.VoxelK)
	rsp.Airborne = s.Airborne
	rsp.DirX = s.MoveDirX
	rsp.DirZ = s.MoveDirZ
	rsp.CurveNorm = s.CurveNorm
	rsp.StateStartMs = s.StateStartMs
	rsp.FallVelY = s.FallVelY
}

func NewWorldServer(id string) *worldServer {
	cfg := common.Config.Servers["world"]
	ss := &worldServer{
		ServerBase:     common.NewServerBase(common.ServerWorld, id),
		players:        make(map[uint64]*playerEntity),
		sceneMgr:       NewSceneMgr(id),
		peerRouter:     common.NewMessageRouter(),
		dbRouter:       common.NewMessageRouter(),
		dbLoadPendings: make(map[string]*dbLoadPending),
		moveTickMs:     int64(cfg.MovementTickMs),
	}
	if ss.moveTickMs <= 0 {
		ss.moveTickMs = 20
	}
	ss.rollbackTicks = maxMoveWindowMs / ss.moveTickMs
	if maxMoveWindowMs%ss.moveTickMs != 0 {
		ss.rollbackTicks++
	}

	common.Register(ss.peerRouter, common.Cli2Wd_WalkStartReq, ss.HandleWalkStart)
	common.Register(ss.peerRouter, common.Cli2Wd_RunStartReq, ss.HandleRunStart)
	common.Register(ss.peerRouter, common.Cli2Wd_SprintStartReq, ss.HandleSprintStart)
	common.Register(ss.peerRouter, common.Cli2Wd_JumpStartReq, ss.HandleJumpStart)
	common.Register(ss.peerRouter, common.Cli2Wd_DashStartReq, ss.HandleDashStart)
	common.Register(ss.peerRouter, common.Cli2Wd_RollStartReq, ss.HandleRollStart)
	common.Register(ss.peerRouter, common.Cli2Wd_StopStartReq, ss.HandleStopStart)
	common.Register(ss.peerRouter, common.Cli2Wd_MoveStopReq, ss.HandleMoveStop)
	common.Register(ss.peerRouter, common.Cli2Wd_MoveDirChangeReq, ss.HandleMoveDirChange)
	common.Register(ss.peerRouter, common.Cli2Wd_SkillReq, ss.HandleSkill)
	common.Register(ss.peerRouter, common.Gw2Wd_DestroyEntityReq, ss.HandleDestroyEntity)
	common.Register(ss.peerRouter, common.Gw2Wd_CreateEntityReq, ss.HandleCreateEntity)

	common.Register(ss.dbRouter, common.Db2Wd_LoadPlayerRsp, ss.HandleDbLoadPlayerRsp)
	common.Register(ss.dbRouter, common.Db2Wd_SavePlayerRsp, ss.HandleDbSavePlayerRsp)

	ss.OnMessage = ss.HandleMessage
	ss.OnCentralMessage = ss.HandleCentralMessage

	go ss.AutosaveLoop(autosaveInterval)
	go ss.MoveLoop()
	return ss
}

func (ss *worldServer) HandleMessage(conn *common.ConnWrapper, msg common.Message) {
	ss.peerRouter.Dispatch(conn, msg)
}

// HandleMoveStart is the shared entry point for the per-state start protocols:
// maps the client server_time_ms onto the fixed tick grid, validates the state
// transition at that tick, and replays recent inputs when the event arrives late.
func (ss *worldServer) HandleMoveStart(conn *common.ConnWrapper, playerID uint64, to pb.MoveState, ts int64, dirX, dirZ float64) {
	ss.mu.Lock()
	entity, ok := ss.players[playerID]
	var rsp *common.MoveRsp
	var logX, logY, logZ float64
	var logLayer int
	var logAccepted bool
	if ok {
		nowMs := time.Now().UnixMilli()
		tick, valid := ss.NormalizeInputTick(ts, nowMs)
		if !valid {
			rsp = &common.MoveRsp{Success: false, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动时间戳非法", AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
			fillMoveRspState(rsp, CaptureEntitySnapshot(entity))
		} else {
			in := moveInput{
				Seq:   entity.InputSeq,
				Tick:  tick,
				Kind:  moveInputStart,
				State: to,
				DirX:  dirX,
				DirZ:  dirZ,
			}
			accepted, msg, snap := ss.ProcessMoveInput(entity, in)
			if !accepted {
				rsp = &common.MoveRsp{Success: false, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: msg, AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
				fillMoveRspState(rsp, CaptureEntitySnapshot(entity))
			} else {
				ackMs := ss.TickToMs(tick)
				rsp = &common.MoveRsp{Success: true, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动开始", AckTimeMs: ackMs, AckTick: tick}
				fillMoveRspState(rsp, snap)
				logAccepted = true
				logX, logY, logZ = snap.X, snap.Y, snap.Z
				logLayer = snap.VoxelK
			}
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: playerID, Message: "实体不存在"}
	}
	ss.mu.Unlock()

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
	if logAccepted {
		log.Printf("[world %s] player %d move state -> %s at (%.2f, %.2f, %.2f) layer=%d",
			ss.ServerId, playerID, pb.MoveState_name[int32(to)], logX, logZ, logY, logLayer)
	}
}

func (ss *worldServer) HandleWalkStart(conn *common.ConnWrapper, req *common.WalkStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_WALK, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleRunStart(conn *common.ConnWrapper, req *common.RunStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_RUN, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleSprintStart(conn *common.ConnWrapper, req *common.SprintStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_SPRINT, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleJumpStart(conn *common.ConnWrapper, req *common.JumpStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_JUMP_UP, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleDashStart(conn *common.ConnWrapper, req *common.DashStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_DASH, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleRollStart(conn *common.ConnWrapper, req *common.RollStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_ROLL, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleStopStart(conn *common.ConnWrapper, req *common.StopStartReq) {
	switch req.StopKind {
	case pb.MoveState_MOVE_STOP_LIGHT, pb.MoveState_MOVE_STOP_MED, pb.MoveState_MOVE_STOP_HARD:
		ss.HandleMoveStart(conn, req.PlayerId, req.StopKind, req.ServerTimeMs, 0, 0)
	default:
		rsp := &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "无效的停止强度"}
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
	}
}

// HandleMoveStop forces the entity to IDLE at the client's stop tick. A late
// stop is the main rollback case: the server may already have simulated a few
// extra ticks (possibly with direction changes); the entity is rewound to the
// stop tick snapshot and the recent input history is replayed from there.
func (ss *worldServer) HandleMoveStop(conn *common.ConnWrapper, req *common.MoveStopReq) {
	ss.mu.Lock()
	entity, ok := ss.players[req.PlayerId]
	var rsp *common.MoveRsp
	if ok {
		nowMs := time.Now().UnixMilli()
		tick, valid := ss.NormalizeInputTick(req.ServerTimeMs, nowMs)
		if !valid {
			// Preserve the old safety-net behaviour: a wildly wrong stop
			// timestamp still stops the entity, effective at the current tick.
			tick = ss.MsToTick(nowMs)
		}
		in := moveInput{Seq: entity.InputSeq, Tick: tick, Kind: moveInputStop}
		accepted, msg, snap := ss.ProcessMoveInput(entity, in)
		if !accepted {
			rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: msg, AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
			fillMoveRspState(rsp, CaptureEntitySnapshot(entity))
		} else {
			ackMs := ss.TickToMs(tick)
			rsp = &common.MoveRsp{Success: true, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "停止", AckTimeMs: ackMs, AckTick: tick}
			fillMoveRspState(rsp, snap)
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
	}
	ss.mu.Unlock()

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
}

// HandleMoveDirChange updates the entity's world-space direction mid-move. The
// change is recorded as an input on the tick grid so a later rollback can replay
// it at the exact frame it belongs to.
func (ss *worldServer) HandleMoveDirChange(conn *common.ConnWrapper, req *common.MoveDirChangeReq) {
	ss.mu.Lock()
	entity, ok := ss.players[req.PlayerId]
	var rsp *common.MoveRsp
	if ok {
		nowMs := time.Now().UnixMilli()
		tick, valid := ss.NormalizeInputTick(req.ServerTimeMs, nowMs)
		if valid {
			in := moveInput{
				Seq:  entity.InputSeq,
				Tick: tick,
				Kind: moveInputDirChange,
				DirX: float64(req.Dir.X),
				DirZ: float64(req.Dir.Z),
			}
			accepted, msg, snap := ss.ProcessMoveInput(entity, in)
			if accepted {
				rsp = &common.MoveRsp{Success: true, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "方向变更", AckTimeMs: ss.TickToMs(tick), AckTick: tick}
				fillMoveRspState(rsp, snap)
			} else {
				rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: msg, AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
				fillMoveRspState(rsp, CaptureEntitySnapshot(entity))
			}
		} else {
			rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动时间戳非法", AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
			fillMoveRspState(rsp, CaptureEntitySnapshot(entity))
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
	}
	ss.mu.Unlock()

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
}

func (ss *worldServer) HandleSkill(conn *common.ConnWrapper, req *common.SkillReq) {
	ss.mu.Lock()
	_, ok := ss.players[req.PlayerId]
	ss.mu.Unlock()

	if !ok {
		rsp := common.SkillRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
		common.SendMsg(conn, common.Wd2Cli_SkillRsp, &rsp)
		return
	}

	rsp := common.SkillRsp{Success: true, PlayerId: req.PlayerId, Message: "技能释放成功: " + req.SkillId}
	common.SendMsg(conn, common.Wd2Cli_SkillRsp, &rsp)
	log.Printf("[world %s] player %d used skill %s", ss.ServerId, req.PlayerId, req.SkillId)
}

func (ss *worldServer) HandleDestroyEntity(conn *common.ConnWrapper, req *common.DestroyEntityReq) {
	var saved *playerEntity
	ss.mu.Lock()
	entity, ok := ss.players[req.PlayerId]
	if ok {
		saved = entity
		delete(ss.players, req.PlayerId)
		if entity.Scene != nil {
			delete(entity.Scene.Players, req.PlayerId)
		}
	}
	ss.mu.Unlock()

	// Persist final position to dbproxy (fire-and-forget; never blocks logout).
	if saved != nil {
		ss.SavePlayer(saved)
	}

	if !ok {
		rsp := common.DestroyEntityRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
		common.SendMsg(conn, common.Wd2Gw_DestroyEntityRsp, &rsp)
		return
	}

	rsp := common.DestroyEntityRsp{Success: true, PlayerId: req.PlayerId, Message: "实体销毁成功"}
	common.SendMsg(conn, common.Wd2Gw_DestroyEntityRsp, &rsp)
	log.Printf("[world %s] player %d entity destroyed", ss.ServerId, req.PlayerId)
}

// HandleCreateEntity kicks off an async LoadPlayer from dbproxy. If dbproxy is
// unavailable it falls back to a local random spawn so login still succeeds.
func (ss *worldServer) HandleCreateEntity(conn *common.ConnWrapper, req *common.CreateEntityReq) {
	log.Printf("[world %s] creating player: account=%s", ss.ServerId, req.Account)

	ss.mu.Lock()
	dbConn := ss.dbConn
	ss.mu.Unlock()
	if dbConn == nil {
		ss.CreateEntityLocal(conn, req.Account)
		return
	}

	// Register the pending BEFORE sending so a fast dbproxy reply cannot race past it.
	pend := &dbLoadPending{conn: conn, account: req.Account}
	pend.timer = time.AfterFunc(loadPlayerTimeout, func() {
		ss.mu.Lock()
		if cur, ok := ss.dbLoadPendings[req.Account]; ok && cur == pend {
			delete(ss.dbLoadPendings, req.Account)
		}
		ss.mu.Unlock()
		log.Printf("[world %s] LoadPlayer timeout for account=%s, gateway will roll back", ss.ServerId, req.Account)
	})
	ss.mu.Lock()
	ss.dbLoadPendings[req.Account] = pend
	ss.mu.Unlock()

	if err := ss.ForwardMsgToDb(common.Wd2Db_LoadPlayerReq, &common.LoadPlayerReq{Account: req.Account}); err != nil {
		ss.mu.Lock()
		delete(ss.dbLoadPendings, req.Account)
		ss.mu.Unlock()
		pend.timer.Stop()
		ss.CreateEntityLocal(conn, req.Account)
		return
	}
}

// HandleDbLoadPlayerRsp creates the entity at the persisted (or random, for new
// accounts) position and replies to the gateway.
func (ss *worldServer) HandleDbLoadPlayerRsp(_ *common.ConnWrapper, rsp *common.LoadPlayerRsp) {
	ss.mu.Lock()
	pend, ok := ss.dbLoadPendings[rsp.Account]
	if ok {
		if pend.timer != nil {
			pend.timer.Stop()
		}
		delete(ss.dbLoadPendings, rsp.Account)
	}
	ss.mu.Unlock()
	if !ok {
		return // timed out or stale; gateway has already rolled back
	}

	// dbproxy failed to allocate an id -> degrade to local spawn.
	if rsp.PlayerId == 0 {
		log.Printf("[world %s] LoadPlayerRsp had no id for account=%s: %s; falling back",
			ss.ServerId, rsp.Account, rsp.Message)
		ss.CreateEntityLocal(pend.conn, rsp.Account)
		return
	}

	ss.mu.Lock()
	sc := ss.sceneMgr.GetOrCreate(ss.ServerId)
	x, z, k := sc.SpawnPosition(rsp.Found, rsp.X, rsp.Z)
	entity := &playerEntity{
		PlayerId: rsp.PlayerId,
		Account:  rsp.Account,
		X:        x,
		Z:        z,
		VoxelK:   k,
		Scene:    sc,
	}
	if sc.voxelGrid != nil {
		entity.Y = sc.voxelGrid.SurfaceHeight(x, z, k)
	}
	ss.InitEntityTimeline(entity)
	sc.Players[rsp.PlayerId] = entity
	ss.players[rsp.PlayerId] = entity
	width, height := sc.Width, sc.Height
	ss.mu.Unlock()

	createRsp := common.CreateEntityRsp{
		Success:  true,
		PlayerId: rsp.PlayerId,
		Account:  rsp.Account,
		X:        x,
		Z:        z,
		Width:    width,
		Height:   height,
		Message:  "实体创建成功",
	}
	common.SendMsg(pend.conn, common.Wd2Gw_CreateEntityRsp, &createRsp)
	log.Printf("[world %s] player %d (account=%s) loaded at (%.1f, %.1f) found=%v",
		ss.ServerId, rsp.PlayerId, rsp.Account, x, z, rsp.Found)
}

// CreateEntityLocal spawns a player without persistence: local counter-based
// player_id and random position. Used when dbproxy is unavailable so login
// still succeeds (degraded mode; position won't be saved this session).
func (ss *worldServer) CreateEntityLocal(conn *common.ConnWrapper, account string) {
	ss.mu.Lock()
	ss.playerCounter++
	playerID := uint64(common.Config.Servers["world"].ID)*10000 + ss.playerCounter

	sc := ss.sceneMgr.GetOrCreate(ss.ServerId)
	x, z, k := sc.SpawnPosition(false, 0, 0)
	entity := &playerEntity{
		PlayerId: playerID,
		Account:  account,
		X:        x,
		Z:        z,
		VoxelK:   k,
		Scene:    sc,
	}
	if sc.voxelGrid != nil {
		entity.Y = sc.voxelGrid.SurfaceHeight(x, z, k)
	}
	ss.InitEntityTimeline(entity)
	sc.Players[playerID] = entity
	ss.players[playerID] = entity
	width, height := sc.Width, sc.Height
	ss.mu.Unlock()

	rsp := common.CreateEntityRsp{
		Success:  true,
		PlayerId: playerID,
		Account:  account,
		X:        entity.X,
		Z:        entity.Z,
		Width:    width,
		Height:   height,
		Message:  "实体创建成功(本地降级)",
	}
	common.SendMsg(conn, common.Wd2Gw_CreateEntityRsp, &rsp)
	log.Printf("[world %s] player %d (account=%s) created locally at (%.1f, %.1f)",
		ss.ServerId, playerID, account, entity.X, entity.Z)
}

func (ss *worldServer) HandleDbSavePlayerRsp(_ *common.ConnWrapper, rsp *common.SavePlayerRsp) {
	if rsp.ReqId == 0 {
		// Fire-and-forget path (logout save, autosave): only log failures.
		if !rsp.Success {
			log.Printf("[world %s] dbproxy save error: %s", ss.ServerId, rsp.Message)
		}
		return
	}
	// Correlated path: this is the reply to the shutdown flush. Wake up
	// FlushAllAndWait so world does not stop before the save lands.
	ss.mu.Lock()
	if ss.flushDone != nil && rsp.ReqId == ss.flushReqID {
		close(ss.flushDone)
		ss.flushDone = nil
	}
	ss.mu.Unlock()
}

func (ss *worldServer) HandleCentralMessage(msg common.Message) {
	switch msg.Type {
	case common.Ct2Srv_RegisterRsp:
		var rsp common.RegisterRsp
		if err := proto.Unmarshal(msg.Data, &rsp); err != nil {
			log.Printf("[world %s] bad RegisterRsp: %v", ss.ServerId, err)
			return
		}
		if rsp.Success {
			log.Printf("[world %s] registered: %s", ss.ServerId, rsp.Message)
			// Pull the dbproxy list in case it registered before us.
			ss.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerDbProxy})
		}
	case common.Ct2Srv_ServerListRsp:
		var rsp common.ServerListRsp
		if err := proto.Unmarshal(msg.Data, &rsp); err != nil {
			log.Printf("[world %s] bad ServerListRsp: %v", ss.ServerId, err)
			return
		}
		for _, e := range rsp.Servers {
			ss.ConnectToDb(e.ServerId, e.ListenAddr)
		}
	case common.Ct2Srv_NewDbProxyNotify:
		var entry common.ServerEntry
		if err := proto.Unmarshal(msg.Data, &entry); err != nil {
			log.Printf("[world %s] bad NewDbProxyNotify: %v", ss.ServerId, err)
			return
		}
		ss.ConnectToDb(entry.ServerId, entry.ListenAddr)
	case common.Ct2Srv_ShutdownNotify:
		var notify common.ShutdownNotify
		if err := proto.Unmarshal(msg.Data, &notify); err != nil {
			log.Printf("[world %s] bad ShutdownNotify: %v", ss.ServerId, err)
			return
		}
		ss.HandleShutdown(&notify)
	case common.Ct2Srv_HeartbeatRsp:
	}
}

// HandleShutdown flushes every online player to dbproxy and waits for the save
// to be acknowledged (so the last ~30s of movement is not lost), then acks
// central and stops. Runs on the central-connection read goroutine; the save
// reply arrives on the dbproxy read goroutine, so this cannot deadlock.
func (ss *worldServer) HandleShutdown(notify *common.ShutdownNotify) {
	log.Printf("[world %s] shutdown requested: %s", ss.ServerId, notify.Reason)
	if !ss.FlushAllAndWait(5 * time.Second) {
		log.Printf("[world %s] player flush incomplete before shutdown", ss.ServerId)
	}
	ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerId})
	go ss.Stop()
}

// FlushAllAndWait sends a final batch save of every online player and blocks
// until dbproxy acknowledges it (or the timeout elapses). Unlike AutosaveAll,
// this is synchronous, so world never stops before the last positions land.
func (ss *worldServer) FlushAllAndWait(timeout time.Duration) bool {
	ss.mu.Lock()
	players := make([]*common.PlayerData, 0, len(ss.players))
	for _, e := range ss.players {
		players = append(players, &common.PlayerData{PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z})
	}
	if len(players) == 0 {
		ss.mu.Unlock()
		return true
	}
	ss.flushReqID++
	id := ss.flushReqID
	done := make(chan struct{})
	ss.flushDone = done
	ss.mu.Unlock()

	req := &common.SavePlayerReq{ReqId: id, Players: players}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		return false
	}
	select {
	case <-done:
		log.Printf("[world %s] flushed %d players to dbproxy", ss.ServerId, len(players))
		return true
	case <-time.After(timeout):
		return false
	}
}

// --- dbproxy connection ---

func (ss *worldServer) ForwardMsgToDb(msgType common.MessageType, m proto.Message) error {
	data, err := common.MarshalHelper(m)
	if err != nil {
		return err
	}
	ss.mu.Lock()
	conn := ss.dbConn
	ss.mu.Unlock()
	if conn == nil {
		return fmt.Errorf("dbproxy not connected")
	}
	return conn.Send(common.Message{Type: msgType, Data: data})
}

func (ss *worldServer) ConnectToDb(dbID, dbAddr string) {
	ss.mu.Lock()
	if ss.dbConn != nil {
		ss.mu.Unlock()
		return
	}
	ss.mu.Unlock()

	conn, err := net.DialTimeout("tcp", dbAddr, 5*time.Second)
	if err != nil {
		log.Printf("[world %s] failed to connect to dbproxy %s at %s: %v", ss.ServerId, dbID, dbAddr, err)
		return
	}
	cw := common.NewConnWrapper(conn)
	ss.mu.Lock()
	ss.dbConn = cw
	ss.mu.Unlock()
	log.Printf("[world %s] connected to dbproxy %s at %s", ss.ServerId, dbID, dbAddr)
	go ss.DbReadLoop(cw)
}

func (ss *worldServer) DbReadLoop(cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[world %s] DbReadLoop panic recovered: %v", ss.ServerId, r)
		}
	}()
	for {
		msg, err := common.ReadMessage(cw.Conn)
		if err != nil {
			// Clear the conn so callers fall back to local mode and a fresh
			// notify/list can reconnect once dbproxy is back.
			ss.mu.Lock()
			if ss.dbConn == cw {
				ss.dbConn.Close()
				ss.dbConn = nil
			}
			ss.mu.Unlock()
			log.Printf("[world %s] dbproxy connection lost: %v", ss.ServerId, err)
			return
		}
		ss.dbRouter.Dispatch(cw, msg)
	}
}

// --- persistence ---

// SavePlayer persists one player's position to dbproxy (fire-and-forget).
func (ss *worldServer) SavePlayer(e *playerEntity) {
	req := &common.SavePlayerReq{
		Players: []*common.PlayerData{
			{PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z},
		},
	}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] save player %d failed: %v", ss.ServerId, e.PlayerId, err)
	}
}

func (ss *worldServer) AutosaveLoop(interval time.Duration) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for range ticker.C {
		ss.AutosaveAll()
	}
}

func (ss *worldServer) AutosaveAll() {
	ss.mu.Lock()
	players := make([]*common.PlayerData, 0, len(ss.players))
	for _, e := range ss.players {
		players = append(players, &common.PlayerData{
			PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z,
		})
	}
	ss.mu.Unlock()
	if len(players) == 0 {
		return
	}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, &common.SavePlayerReq{Players: players}); err != nil {
		log.Printf("[world %s] autosave failed: %v", ss.ServerId, err)
		return
	}
	log.Printf("[world %s] autosaved %d players", ss.ServerId, len(players))
}
