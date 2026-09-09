package main

import (
	"fmt"
	"log"
	"net"
	"time"

	"bigworld/common"
	pb "bigworld/common/pb"

	"google.golang.org/protobuf/proto"
)

const (
	loadPlayerTimeout = 8 * time.Second
	autosaveInterval  = 30 * time.Second
)

type worldServer struct {
	*common.ServerBase
	players       map[uint64]*playerEntity
	sceneMgr      *sceneMgr
	playerCounter uint64

	moveTickMs    int64
	rollbackTicks int64

	dbConn *common.ConnWrapper

	dbLoadPendings map[string]*dbLoadPending

	shutdownFlushing bool
	shutdownReqID    uint64
	shutdownDeadline time.Time

	lastAutosave time.Time
}

type dbLoadPending struct {
	conn     *common.ConnWrapper
	account  string
	deadline time.Time
}

func FillMoveRspState(rsp *common.MoveRsp, s entitySnapshot) {
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

	if err := PlayerConfig().Load(cfg.PlayerConfigFile); err != nil {
		log.Printf("[world %s] WARN: player config %q failed to load: %v (using builtin defaults)",
			id, cfg.PlayerConfigFile, err)
	}
	if err := MoveTransitions().Load(cfg.StateTransitionTableFile); err != nil {
		log.Printf("[world %s] WARN: state transition table %q failed to load: %v (transition validation disabled)",
			id, cfg.StateTransitionTableFile, err)
	}
	if cfg.StateConfigFile != "" {
		if err := StateConfig().Load(cfg.StateConfigFile); err != nil {
			log.Printf("[world %s] WARN: state config %q failed to load: %v (per-state move caps disabled)",
				id, cfg.StateConfigFile, err)
		}
	}

	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_WalkStartReq, ss.HandleWalkStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_RunStartReq, ss.HandleRunStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_SprintStartReq, ss.HandleSprintStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_JumpStartReq, ss.HandleJumpStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_DashStartReq, ss.HandleDashStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_RollStartReq, ss.HandleRollStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_StopStartReq, ss.HandleStopStart)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_MoveStopReq, ss.HandleMoveStop)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_MoveDirChangeReq, ss.HandleMoveDirChange)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_SkillReq, ss.HandleSkill)
	common.Register(ss.Router(common.SrcGateway), common.Gw2Wd_DestroyEntityReq, ss.HandleDestroyEntity)
	common.Register(ss.Router(common.SrcGateway), common.Gw2Wd_CreateEntityReq, ss.HandleCreateEntity)

	common.Register(ss.Router(common.SrcDbProxy), common.Db2Wd_LoadPlayerRsp, ss.HandleDbLoadPlayerRsp)
	common.Register(ss.Router(common.SrcDbProxy), common.Db2Wd_SavePlayerRsp, ss.HandleDbSavePlayerRsp)

	cr := ss.Router(common.SrcCentral)
	common.Register(cr, common.Ct2Srv_RegisterRsp, ss.HandleCentralRegisterRsp)
	common.Register(cr, common.Ct2Srv_ServerListRsp, ss.HandleCentralServerListRsp)
	common.Register(cr, common.Ct2Srv_NewDbProxyNotify, ss.HandleCentralNewDbProxy)
	common.Register(cr, common.Ct2Srv_ShutdownNotify, ss.HandleCentralShutdownNotify)
	common.Register(cr, common.Ct2Srv_HeartbeatRsp, ss.NoopHeartbeat)

	ss.Loop.OnTick = ss.OnTick
	ss.Loop.TickEvery = time.Duration(ss.moveTickMs) * time.Millisecond
	ss.lastAutosave = time.Now()
	return ss
}

func (ss *worldServer) OnTick() {
	now := time.Now()
	nowTick := ss.MsToTick(now.UnixMilli())
	for _, e := range ss.players {
		ss.AdvanceEntityTo(e, nowTick)
	}

	for account, pend := range ss.dbLoadPendings {
		if now.After(pend.deadline) {
			delete(ss.dbLoadPendings, account)
			log.Printf("[world %s] LoadPlayer timeout for account=%s, gateway will roll back", ss.ServerId, account)
		}
	}

	if now.Sub(ss.lastAutosave) >= autosaveInterval {
		ss.lastAutosave = now
		ss.AutosaveAll()
	}

	if ss.shutdownFlushing && now.After(ss.shutdownDeadline) {
		ss.shutdownFlushing = false
		log.Printf("[world %s] player flush incomplete before shutdown", ss.ServerId)
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerId})
		go ss.Stop()
	}
}

func (ss *worldServer) HandleMoveStart(conn *common.ConnWrapper, playerID uint64, to pb.MoveState, ts int64, dirX, dirZ float64) {
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
			FillMoveRspState(rsp, CaptureEntitySnapshot(entity))
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
				FillMoveRspState(rsp, CaptureEntitySnapshot(entity))
			} else {
				ackMs := ss.TickToMs(tick)
				rsp = &common.MoveRsp{Success: true, PlayerId: playerID, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动开始", AckTimeMs: ackMs, AckTick: tick}
				FillMoveRspState(rsp, snap)
				logAccepted = true
				logX, logY, logZ = snap.X, snap.Y, snap.Z
				logLayer = snap.VoxelK
			}
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: playerID, Message: "实体不存在"}
	}

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

func (ss *worldServer) HandleMoveStop(conn *common.ConnWrapper, req *common.MoveStopReq) {
	entity, ok := ss.players[req.PlayerId]
	var rsp *common.MoveRsp
	if ok {
		nowMs := time.Now().UnixMilli()
		tick, valid := ss.NormalizeInputTick(req.ServerTimeMs, nowMs)
		if !valid {

			tick = ss.MsToTick(nowMs)
		}
		in := moveInput{Seq: entity.InputSeq, Tick: tick, Kind: moveInputStop}
		accepted, msg, snap := ss.ProcessMoveInput(entity, in)
		if !accepted {
			rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: msg, AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
			FillMoveRspState(rsp, CaptureEntitySnapshot(entity))
		} else {
			ackMs := ss.TickToMs(tick)
			rsp = &common.MoveRsp{Success: true, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "停止", AckTimeMs: ackMs, AckTick: tick}
			FillMoveRspState(rsp, snap)
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
	}

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
}

func (ss *worldServer) HandleMoveDirChange(conn *common.ConnWrapper, req *common.MoveDirChangeReq) {
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
				FillMoveRspState(rsp, snap)
			} else {
				rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: msg, AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
				FillMoveRspState(rsp, CaptureEntitySnapshot(entity))
			}
		} else {
			rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, X: entity.X, Z: entity.Z, Y: entity.Y, Message: "移动时间戳非法", AckTimeMs: entity.LastMoveTimeMs, AckTick: ss.MsToTick(entity.LastMoveTimeMs)}
			FillMoveRspState(rsp, CaptureEntitySnapshot(entity))
		}
	} else {
		rsp = &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
	}

	common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
}

func (ss *worldServer) HandleSkill(conn *common.ConnWrapper, req *common.SkillReq) {
	_, ok := ss.players[req.PlayerId]

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
	entity, ok := ss.players[req.PlayerId]
	if ok {
		saved = entity
		delete(ss.players, req.PlayerId)
		if entity.Scene != nil {
			delete(entity.Scene.Players, req.PlayerId)
		}
	}

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

func (ss *worldServer) HandleCreateEntity(conn *common.ConnWrapper, req *common.CreateEntityReq) {
	log.Printf("[world %s] creating player: account=%s", ss.ServerId, req.Account)

	dbConn := ss.dbConn
	if dbConn == nil {
		ss.CreateEntityLocal(conn, req.Account)
		return
	}

	pend := &dbLoadPending{conn: conn, account: req.Account, deadline: time.Now().Add(loadPlayerTimeout)}
	ss.dbLoadPendings[req.Account] = pend

	if err := ss.ForwardMsgToDb(common.Wd2Db_LoadPlayerReq, &common.LoadPlayerReq{Account: req.Account}); err != nil {
		delete(ss.dbLoadPendings, req.Account)
		ss.CreateEntityLocal(conn, req.Account)
		return
	}
}

func (ss *worldServer) HandleDbLoadPlayerRsp(_ *common.ConnWrapper, rsp *common.LoadPlayerRsp) {
	pend, ok := ss.dbLoadPendings[rsp.Account]
	if ok {
		delete(ss.dbLoadPendings, rsp.Account)
	}
	if !ok {
		return
	}

	if rsp.PlayerId == 0 {
		log.Printf("[world %s] LoadPlayerRsp had no id for account=%s: %s; falling back",
			ss.ServerId, rsp.Account, rsp.Message)
		ss.CreateEntityLocal(pend.conn, rsp.Account)
		return
	}

	sc := ss.sceneMgr.GetOrCreate(rsp.SceneId)
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

	createRsp := common.CreateEntityRsp{
		Success:  true,
		PlayerId: rsp.PlayerId,
		Account:  rsp.Account,
		X:        x,
		Z:        z,
		Width:    width,
		Height:   height,
		Message:  "实体创建成功",
		SceneId:  sc.SceneId,
	}
	common.SendMsg(pend.conn, common.Wd2Gw_CreateEntityRsp, &createRsp)
	log.Printf("[world %s] player %d (account=%s) loaded at (%.1f, %.1f) found=%v",
		ss.ServerId, rsp.PlayerId, rsp.Account, x, z, rsp.Found)
}

func (ss *worldServer) CreateEntityLocal(conn *common.ConnWrapper, account string) {
	ss.playerCounter++
	playerID := uint64(common.Config.Servers["world"].ID)*10000 + ss.playerCounter

	sc := ss.sceneMgr.GetOrCreate(ss.sceneMgr.defaultScene)
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

	rsp := common.CreateEntityRsp{
		Success:  true,
		PlayerId: playerID,
		Account:  account,
		X:        entity.X,
		Z:        entity.Z,
		Width:    width,
		Height:   height,
		Message:  "实体创建成功(本地降级)",
		SceneId:  sc.SceneId,
	}
	common.SendMsg(conn, common.Wd2Gw_CreateEntityRsp, &rsp)
	log.Printf("[world %s] player %d (account=%s) created locally at (%.1f, %.1f)",
		ss.ServerId, playerID, account, entity.X, entity.Z)
}

func (ss *worldServer) HandleDbSavePlayerRsp(_ *common.ConnWrapper, rsp *common.SavePlayerRsp) {
	if rsp.ReqId == 0 {

		if !rsp.Success {
			log.Printf("[world %s] dbproxy save error: %s", ss.ServerId, rsp.Message)
		}
		return
	}

	if ss.shutdownFlushing && rsp.ReqId == ss.shutdownReqID {
		ss.shutdownFlushing = false
		log.Printf("[world %s] flushed players to dbproxy, shutting down", ss.ServerId)
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerId})
		go ss.Stop()
	}
}

func (ss *worldServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (ss *worldServer) HandleCentralRegisterRsp(_ *common.ConnWrapper, rsp *common.RegisterRsp) {
	if !rsp.Success {
		return
	}
	log.Printf("[world %s] registered: %s", ss.ServerId, rsp.Message)

	ss.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerDbProxy})
}

func (ss *worldServer) HandleCentralServerListRsp(_ *common.ConnWrapper, rsp *common.ServerListRsp) {
	for _, e := range rsp.Servers {
		ss.ConnectToDb(e.ServerId, e.ListenAddr)
	}
}

func (ss *worldServer) HandleCentralNewDbProxy(_ *common.ConnWrapper, entry *common.ServerEntry) {
	ss.ConnectToDb(entry.ServerId, entry.ListenAddr)
}

func (ss *worldServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	ss.HandleShutdown(notify)
}

func (ss *worldServer) HandleShutdown(notify *common.ShutdownNotify) {
	log.Printf("[world %s] shutdown requested: %s", ss.ServerId, notify.Reason)
	if ss.shutdownFlushing {
		return
	}

	players := make([]*common.PlayerData, 0, len(ss.players))
	for _, e := range ss.players {
		players = append(players, &common.PlayerData{PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z, SceneId: entitySceneId(e)})
	}
	if len(players) == 0 {
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerId})
		go ss.Stop()
		return
	}

	ss.shutdownReqID++
	req := &common.SavePlayerReq{ReqId: ss.shutdownReqID, Players: players}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] shutdown flush send failed: %v", ss.ServerId, err)
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerId})
		go ss.Stop()
		return
	}
	ss.shutdownFlushing = true
	ss.shutdownDeadline = time.Now().Add(5 * time.Second)
	log.Printf("[world %s] flushing %d players to dbproxy", ss.ServerId, len(players))
}

func (ss *worldServer) ForwardMsgToDb(msgType common.MessageType, m proto.Message) error {
	data, err := common.MarshalHelper(m)
	if err != nil {
		return err
	}
	if ss.dbConn == nil {
		return fmt.Errorf("dbproxy not connected")
	}
	return ss.dbConn.Send(common.Message{Type: msgType, Data: data})
}

func (ss *worldServer) ConnectToDb(dbID, dbAddr string) {
	if ss.dbConn != nil {
		return
	}

	go func() {
		conn, err := net.DialTimeout("tcp", dbAddr, 5*time.Second)
		if err != nil {
			log.Printf("[world %s] failed to connect to dbproxy %s at %s: %v", ss.ServerId, dbID, dbAddr, err)
			return
		}
		ss.Loop.Defer(func() {
			if ss.dbConn != nil {
				conn.Close()
				return
			}
			cw := common.NewConnWrapper(conn)
			cw.PeerType = common.ServerDbProxy
			ss.dbConn = cw
			log.Printf("[world %s] connected to dbproxy %s at %s", ss.ServerId, dbID, dbAddr)
			common.SendMsg(cw, common.Srv2Srv_IdentifyReq, &common.IdentifyReq{ServerType: common.ServerWorld})
			go ss.DbReadLoop(cw)
		})
	}()
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

			dropped := cw
			ss.Loop.Defer(func() {
				if ss.dbConn == dropped {
					ss.dbConn.Close()
					ss.dbConn = nil
					log.Printf("[world %s] dbproxy connection lost", ss.ServerId)
				}
			})
			return
		}
		if !ss.Loop.Post(common.Event{Kind: common.EventMessage, Conn: cw, Msg: msg}) {
			cw.Close()
			return
		}
	}
}

func entitySceneId(e *playerEntity) string {
	if e.Scene == nil {
		return ""
	}
	return e.Scene.SceneId
}

func (ss *worldServer) SavePlayer(e *playerEntity) {
	req := &common.SavePlayerReq{
		Players: []*common.PlayerData{
			{PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z, SceneId: entitySceneId(e)},
		},
	}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] save player %d failed: %v", ss.ServerId, e.PlayerId, err)
	}
}

func (ss *worldServer) AutosaveAll() {
	players := make([]*common.PlayerData, 0, len(ss.players))
	for _, e := range ss.players {
		players = append(players, &common.PlayerData{
			PlayerId: e.PlayerId, Account: e.Account, X: e.X, Z: e.Z, SceneId: entitySceneId(e),
		})
	}
	if len(players) == 0 {
		return
	}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, &common.SavePlayerReq{Players: players}); err != nil {
		log.Printf("[world %s] autosave failed: %v", ss.ServerId, err)
		return
	}
	log.Printf("[world %s] autosaved %d players", ss.ServerId, len(players))
}
