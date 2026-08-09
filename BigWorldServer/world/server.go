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

	// Server-authoritative movement sim: tick interval and last-tick clock.
	moveTickMs int64
	lastTickMs int64

	// dbproxy connection (discovered via central) and its inbound router.
	dbConn   *common.ConnWrapper
	dbRouter *common.MessageRouter

	// In-flight LoadPlayer requests, keyed by account. The gateway arms a
	// 10s createEntityTimeout, so a LoadPlayer reply must arrive within
	// loadPlayerTimeout or we abandon the create (no orphan entity).
	dbLoadPendings map[string]*dbLoadPending

	// Shutdown flush correlation: flushAllAndWait sets a req id + channel,
	// handleDbSavePlayerRsp closes the channel when the matching rsp arrives.
	flushReqID uint64
	flushDone  chan struct{}
}

// dbLoadPending correlates a CreateEntityReq with the async LoadPlayerRsp.
type dbLoadPending struct {
	conn    *common.ConnWrapper // gateway world-conn to reply CreateEntityRsp on
	account string
	timer   *time.Timer
}

func newWorldServer(id string) *worldServer {
	cfg := common.Config.Servers["world"]
	ss := &worldServer{
		ServerBase:     common.NewServerBase(common.ServerWorld, id),
		players:        make(map[uint64]*playerEntity),
		sceneMgr:       newSceneMgr(id),
		peerRouter:     common.NewMessageRouter(),
		dbRouter:       common.NewMessageRouter(),
		dbLoadPendings: make(map[string]*dbLoadPending),
		moveTickMs:     int64(cfg.MovementTickMs),
	}
	if ss.moveTickMs <= 0 {
		ss.moveTickMs = 20
	}
	ss.lastTickMs = time.Now().UnixMilli()

	common.Register(ss.peerRouter, common.Cli2Wd_WalkStartReq, ss.handleWalkStart)
	common.Register(ss.peerRouter, common.Cli2Wd_RunStartReq, ss.handleRunStart)
	common.Register(ss.peerRouter, common.Cli2Wd_SprintStartReq, ss.handleSprintStart)
	common.Register(ss.peerRouter, common.Cli2Wd_JumpStartReq, ss.handleJumpStart)
	common.Register(ss.peerRouter, common.Cli2Wd_DashStartReq, ss.handleDashStart)
	common.Register(ss.peerRouter, common.Cli2Wd_RollStartReq, ss.handleRollStart)
	common.Register(ss.peerRouter, common.Cli2Wd_StopStartReq, ss.handleStopStart)
	common.Register(ss.peerRouter, common.Cli2Wd_MoveStopReq, ss.handleMoveStop)
	common.Register(ss.peerRouter, common.Cli2Wd_MoveDirChangeReq, ss.handleMoveDirChange)
	common.Register(ss.peerRouter, common.Cli2Wd_SkillReq, ss.handleSkill)
	common.Register(ss.peerRouter, common.Gw2Wd_DestroyEntityReq, ss.handleDestroyEntity)
	common.Register(ss.peerRouter, common.Gw2Wd_CreateEntityReq, ss.handleCreateEntity)

	common.Register(ss.dbRouter, common.Db2Wd_LoadPlayerRsp, ss.handleDbLoadPlayerRsp)
	common.Register(ss.dbRouter, common.Db2Wd_SavePlayerRsp, ss.handleDbSavePlayerRsp)

	ss.OnMessage = ss.handleMessage
	ss.OnCentralMessage = ss.handleCentralMessage

	go ss.autosaveLoop(autosaveInterval)
	go ss.moveLoop()
	return ss
}

func (ss *worldServer) handleMessage(conn *common.ConnWrapper, msg common.Message) {
	ss.peerRouter.Dispatch(conn, msg)
}

// handleMoveStart is the shared entry point for the per-state start protocols:
// validates the transition and the client server_time_ms, then arms the
// server-side state class (setMoveState resets StateStartMs/CurveNorm).
func (ss *worldServer) handleMoveStart(conn *common.ConnWrapper, playerID uint64, to pb.MoveState, ts int64, dirX, dirZ float64) {
	ss.mu.Lock()
	entity, ok := ss.players[playerID]
	var rsp *common.MoveRsp
	if ok {
		now := time.Now().UnixMilli()
		if !canTransition(entity.State, to) {
			rsp = &common.MoveRsp{Success: false, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "非法状态转换", AckTimeMs: entity.LastMoveTimeMs}
		} else if t, reject := moveStartTimeMs(entity, ts, now); reject {
			rsp = &common.MoveRsp{Success: false, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动时间戳非法", AckTimeMs: entity.LastMoveTimeMs}
		} else {
			if dirX != 0 || dirZ != 0 {
				setMoveDir(entity, dirX, dirZ)
			}
			if entity.State != to {
				setMoveState(entity, to, t)
			}
			entity.LastMoveTimeMs = t
			rsp = &common.MoveRsp{Success: true, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动开始", AckTimeMs: t}
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: playerID, Message: "实体不存在"}
	}
	ss.mu.Unlock()

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
	if ok {
		log.Printf("[world %s] player %d move state -> %s at (%.2f, %.2f, %.2f) layer=%d",
			ss.ServerId, playerID, pb.MoveState_name[int32(to)], entity.X, entity.Z, entity.Y, entity.VoxelK)
	}
}

// moveStartTimeMs sanitizes the client's server_time_ms: <=0 falls back to now;
// a timestamp far in the future is rejected (clock estimate way off).
func moveStartTimeMs(e *playerEntity, ts, nowMs int64) (int64, bool) {
	if ts <= 0 {
		return nowMs, false
	}
	if ts > nowMs+maxMoveWindowMs {
		return 0, true
	}
	return ts, false
}

func (ss *worldServer) handleWalkStart(conn *common.ConnWrapper, req *common.WalkStartReq) {
	ss.handleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_WALK, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) handleRunStart(conn *common.ConnWrapper, req *common.RunStartReq) {
	ss.handleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_RUN, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) handleSprintStart(conn *common.ConnWrapper, req *common.SprintStartReq) {
	ss.handleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_SPRINT, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) handleJumpStart(conn *common.ConnWrapper, req *common.JumpStartReq) {
	ss.handleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_JUMP_UP, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) handleDashStart(conn *common.ConnWrapper, req *common.DashStartReq) {
	ss.handleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_DASH, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) handleRollStart(conn *common.ConnWrapper, req *common.RollStartReq) {
	ss.handleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_ROLL, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) handleStopStart(conn *common.ConnWrapper, req *common.StopStartReq) {
	switch req.StopKind {
	case pb.MoveState_MOVE_STOP_LIGHT, pb.MoveState_MOVE_STOP_MED, pb.MoveState_MOVE_STOP_HARD:
		ss.handleMoveStart(conn, req.PlayerId, req.StopKind, req.ServerTimeMs, 0, 0)
	default:
		rsp := &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "无效的停止强度"}
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
	}
}

// handleMoveStop forces the entity to IDLE regardless of the current sim state:
// the client sends it when it enters a stationary state, and the server may be
// a tick behind its own landing transition.
func (ss *worldServer) handleMoveStop(conn *common.ConnWrapper, req *common.MoveStopReq) {
	ss.mu.Lock()
	entity, ok := ss.players[req.PlayerId]
	var rsp *common.MoveRsp
	if ok {
		now := time.Now().UnixMilli()
		t := req.ServerTimeMs
		if t <= 0 || t > now+maxMoveWindowMs {
			t = now
		}
		if entity.State != pb.MoveState_MOVE_IDLE {
			setMoveState(entity, pb.MoveState_MOVE_IDLE, t)
		}
		entity.LastMoveTimeMs = t
		rsp = &common.MoveRsp{Success: true, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "停止", AckTimeMs: t}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
	}
	ss.mu.Unlock()

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
}

// handleMoveDirChange updates the entity's world-space direction mid-move. The
// curve progress is untouched; subsequent tick deltas are projected onto the new
// direction, mirroring the client rotating during walk/run.
func (ss *worldServer) handleMoveDirChange(conn *common.ConnWrapper, req *common.MoveDirChangeReq) {
	ss.mu.Lock()
	entity, ok := ss.players[req.PlayerId]
	if ok {
		now := time.Now().UnixMilli()
		if req.ServerTimeMs > 0 && req.ServerTimeMs <= now+maxMoveWindowMs {
			entity.LastMoveTimeMs = req.ServerTimeMs
		}
		setMoveDir(entity, float64(req.Dir.X), float64(req.Dir.Z))
	}
	ss.mu.Unlock()
}

// moveLoop drives the server-authoritative movement sim: every movement tick it
// advances each moving player's position via its state class update.
func (ss *worldServer) moveLoop() {
	ticker := time.NewTicker(time.Duration(ss.moveTickMs) * time.Millisecond)
	defer ticker.Stop()
	for range ticker.C {
		ss.tickMoves()
	}
}

func (ss *worldServer) handleSkill(conn *common.ConnWrapper, req *common.SkillReq) {
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

func (ss *worldServer) handleDestroyEntity(conn *common.ConnWrapper, req *common.DestroyEntityReq) {
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
		ss.savePlayer(saved)
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

// handleCreateEntity kicks off an async LoadPlayer from dbproxy. If dbproxy is
// unavailable it falls back to a local random spawn so login still succeeds.
func (ss *worldServer) handleCreateEntity(conn *common.ConnWrapper, req *common.CreateEntityReq) {
	log.Printf("[world %s] creating player: account=%s", ss.ServerId, req.Account)

	ss.mu.Lock()
	dbConn := ss.dbConn
	ss.mu.Unlock()
	if dbConn == nil {
		ss.createEntityLocal(conn, req.Account)
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

	if err := ss.forwardMsgToDb(common.Wd2Db_LoadPlayerReq, &common.LoadPlayerReq{Account: req.Account}); err != nil {
		ss.mu.Lock()
		delete(ss.dbLoadPendings, req.Account)
		ss.mu.Unlock()
		pend.timer.Stop()
		ss.createEntityLocal(conn, req.Account)
		return
	}
}

// handleDbLoadPlayerRsp creates the entity at the persisted (or random, for new
// accounts) position and replies to the gateway.
func (ss *worldServer) handleDbLoadPlayerRsp(_ *common.ConnWrapper, rsp *common.LoadPlayerRsp) {
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
		ss.createEntityLocal(pend.conn, rsp.Account)
		return
	}

	ss.mu.Lock()
	sc := ss.sceneMgr.GetOrCreate(ss.ServerId)
	x, z, k := sc.spawnPosition(rsp.Found, rsp.X, rsp.Z)
	entity := &playerEntity{
		PlayerId: rsp.PlayerId,
		Account:  rsp.Account,
		X:        x,
		Z:        z,
		VoxelK:   k,
		Scene:    sc,
	}
	if sc.voxelGrid != nil {
		entity.Y = sc.voxelGrid.surfaceHeight(x, z, k)
	}
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

// createEntityLocal spawns a player without persistence: local counter-based
// player_id and random position. Used when dbproxy is unavailable so login
// still succeeds (degraded mode; position won't be saved this session).
func (ss *worldServer) createEntityLocal(conn *common.ConnWrapper, account string) {
	ss.mu.Lock()
	ss.playerCounter++
	playerID := uint64(common.Config.Servers["world"].ID)*10000 + ss.playerCounter

	sc := ss.sceneMgr.GetOrCreate(ss.ServerId)
	x, z, k := sc.spawnPosition(false, 0, 0)
	entity := &playerEntity{
		PlayerId: playerID,
		Account:  account,
		X:        x,
		Z:        z,
		VoxelK:   k,
		Scene:    sc,
	}
	if sc.voxelGrid != nil {
		entity.Y = sc.voxelGrid.surfaceHeight(x, z, k)
	}
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

func (ss *worldServer) handleDbSavePlayerRsp(_ *common.ConnWrapper, rsp *common.SavePlayerRsp) {
	if rsp.ReqId == 0 {
		// Fire-and-forget path (logout save, autosave): only log failures.
		if !rsp.Success {
			log.Printf("[world %s] dbproxy save error: %s", ss.ServerId, rsp.Message)
		}
		return
	}
	// Correlated path: this is the reply to the shutdown flush. Wake up
	// flushAllAndWait so world does not stop before the save lands.
	ss.mu.Lock()
	if ss.flushDone != nil && rsp.ReqId == ss.flushReqID {
		close(ss.flushDone)
		ss.flushDone = nil
	}
	ss.mu.Unlock()
}

func (ss *worldServer) handleCentralMessage(msg common.Message) {
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
			ss.connectToDb(e.ServerId, e.ListenAddr)
		}
	case common.Ct2Srv_NewDbProxyNotify:
		var entry common.ServerEntry
		if err := proto.Unmarshal(msg.Data, &entry); err != nil {
			log.Printf("[world %s] bad NewDbProxyNotify: %v", ss.ServerId, err)
			return
		}
		ss.connectToDb(entry.ServerId, entry.ListenAddr)
	case common.Ct2Srv_ShutdownNotify:
		var notify common.ShutdownNotify
		if err := proto.Unmarshal(msg.Data, &notify); err != nil {
			log.Printf("[world %s] bad ShutdownNotify: %v", ss.ServerId, err)
			return
		}
		ss.handleShutdown(&notify)
	case common.Ct2Srv_HeartbeatRsp:
	}
}

// handleShutdown flushes every online player to dbproxy and waits for the save
// to be acknowledged (so the last ~30s of movement is not lost), then acks
// central and stops. Runs on the central-connection read goroutine; the save
// reply arrives on the dbproxy read goroutine, so this cannot deadlock.
func (ss *worldServer) handleShutdown(notify *common.ShutdownNotify) {
	log.Printf("[world %s] shutdown requested: %s", ss.ServerId, notify.Reason)
	if !ss.flushAllAndWait(5 * time.Second) {
		log.Printf("[world %s] player flush incomplete before shutdown", ss.ServerId)
	}
	ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerId})
	go ss.Stop()
}

// flushAllAndWait sends a final batch save of every online player and blocks
// until dbproxy acknowledges it (or the timeout elapses). Unlike autosaveAll,
// this is synchronous, so world never stops before the last positions land.
func (ss *worldServer) flushAllAndWait(timeout time.Duration) bool {
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
	if err := ss.forwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
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

func (ss *worldServer) forwardMsgToDb(msgType common.MessageType, m proto.Message) error {
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

func (ss *worldServer) connectToDb(dbID, dbAddr string) {
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
	go ss.dbReadLoop(cw)
}

func (ss *worldServer) dbReadLoop(cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[world %s] dbReadLoop panic recovered: %v", ss.ServerId, r)
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

// savePlayer persists one player's position to dbproxy (fire-and-forget).
func (ss *worldServer) savePlayer(e *playerEntity) {
	req := &common.SavePlayerReq{
		Players: []*common.PlayerData{
			{PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z},
		},
	}
	if err := ss.forwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] save player %d failed: %v", ss.ServerId, e.PlayerId, err)
	}
}

func (ss *worldServer) autosaveLoop(interval time.Duration) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for range ticker.C {
		ss.autosaveAll()
	}
}

func (ss *worldServer) autosaveAll() {
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
	if err := ss.forwardMsgToDb(common.Wd2Db_SavePlayerReq, &common.SavePlayerReq{Players: players}); err != nil {
		log.Printf("[world %s] autosave failed: %v", ss.ServerId, err)
		return
	}
	log.Printf("[world %s] autosaved %d players", ss.ServerId, len(players))
}
