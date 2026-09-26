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
	loadPlayerTimeout = 8 * time.Second
	autosaveInterval  = 30 * time.Second
)

type worldServer struct {
	*common.ServerBase
	players           map[uint64]*playerEntity
	playersByAccount  map[string]*playerEntity
	sceneMgr          *sceneMgr
	sceneTriggers     []common.SceneTriggerConfig
	playerCounter     uint64
	serverCfg         common.ServerConfig
	serverNumID       int
	peerMgr           *worldPeerMgr
	gatewayConns      map[string]*common.ConnWrapper
	outboundTransfers map[string]*outboundTransfer
	incomingTransfers map[string]*incomingTransfer
	transferReqSeq    uint64

	moveTickMs    int64
	rollbackTicks int64

	dbConn       *common.ConnWrapper
	dbID         string
	dbAddr       string
	dbConnecting bool
	dbMu         sync.Mutex

	dbLoadPendings map[string]*dbLoadPending

	shutdownFlushing bool
	shutdownReqID    uint64
	shutdownDeadline time.Time

	lastAutosave time.Time
}

type dbLoadPending struct {
	conn      *common.ConnWrapper
	account   string
	gatewayID string
	sessionID string
	deadline  time.Time
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

func NewWorldServer(id string, cfg common.ServerConfig) *worldServer {
	sceneTriggers := cfg.SceneTriggers
	if len(sceneTriggers) == 0 {
		sceneTriggers = defaultSceneTriggers()
	}
	ss := &worldServer{
		ServerBase:       common.NewServerBase(common.ServerWorld, id),
		players:          make(map[uint64]*playerEntity),
		playersByAccount: make(map[string]*playerEntity),
		sceneMgr:         NewSceneMgr(id, cfg),
		sceneTriggers:    sceneTriggers,
		dbLoadPendings:   make(map[string]*dbLoadPending),
		moveTickMs:       int64(cfg.MovementTickMs),
	}
	if ss.moveTickMs <= 0 {
		ss.moveTickMs = 20
	}
	ss.serverCfg = cfg
	ss.serverNumID = cfg.ID
	ss.ListenAddr = cfg.ListenAddr
	ss.gatewayConns = make(map[string]*common.ConnWrapper)
	ss.outboundTransfers = make(map[string]*outboundTransfer)
	ss.incomingTransfers = make(map[string]*incomingTransfer)
	ss.peerMgr = NewWorldPeerMgr(ss, cfg)
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

	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_WalkStartReq, ss.HandleWalkStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_RunStartReq, ss.HandleRunStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_SprintStartReq, ss.HandleSprintStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_JumpStartReq, ss.HandleJumpStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_DashStartReq, ss.HandleDashStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_RollStartReq, ss.HandleRollStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_StopStartReq, ss.HandleStopStartReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_MoveStopReq, ss.HandleMoveStopReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_MoveDirChangeReq, ss.HandleMoveDirChangeReq)
	common.Register(ss.Router(common.SrcGateway), common.Cli2Wd_SkillReq, ss.HandleSkillReq)
	common.Register(ss.Router(common.SrcGateway), common.Gw2Wd_DestroyEntityReq, ss.HandleDestroyEntityReq)
	common.Register(ss.Router(common.SrcGateway), common.Gw2Wd_CreateEntityReq, ss.HandleCreateEntityReq)
	common.Register(ss.Router(common.SrcGateway), common.Gw2Wd_ResumeEntityReq, ss.HandleResumeEntityReq)

	common.Register(ss.Router(common.SrcDbProxy), common.Db2Wd_LoadPlayerRsp, ss.HandleDbLoadPlayerRsp)
	common.Register(ss.Router(common.SrcDbProxy), common.Db2Wd_SavePlayerRsp, ss.HandleDbSavePlayerRsp)

	cr := ss.Router(common.SrcCentral)
	common.Register(cr, common.Srv2Srv_HelloRsp, ss.HandleCentralHelloRsp)
	common.Register(cr, common.Ct2Srv_ServerListRsp, ss.HandleCentralServerListRsp)
	common.Register(cr, common.Ct2Srv_NewDbProxyNotify, ss.HandleCentralNewDbProxyNotify)
	common.Register(cr, common.Ct2Srv_ShutdownNotify, ss.HandleCentralShutdownNotify)
	common.Register(cr, common.Ct2Srv_HeartbeatRsp, ss.NoopHeartbeat)
	common.Register(cr, common.Ct2Wd_NewWorldNotify, ss.HandleCentralNewWorldNotify)
	common.Register(cr, common.Ct2Wd_WorldOfflineNotify, ss.HandleCentralWorldOfflineNotify)
	common.Register(cr, common.Ct2Wd_TransferPrepareRsp, ss.HandleCentralTransferPrepareRsp)
	common.Register(cr, common.Ct2Wd_TransferIncomingNotify, ss.HandleCentralTransferIncomingNotify)
	common.Register(cr, common.Ct2Wd_TransferFinishNotify, ss.HandleCentralTransferFinishNotify)

	wr := ss.Router(common.SrcWorld)
	common.Register(wr, common.Wd2Wd_HeartbeatReq, ss.peerMgr.HandleHeartbeatReq)
	common.Register(wr, common.Wd2Wd_HeartbeatRsp, ss.peerMgr.HandleHeartbeatRsp)
	common.Register(wr, common.Wd2Wd_TransferEntityReq, ss.HandlePeerTransferEntityReq)
	common.Register(wr, common.Wd2Wd_TransferEntityRsp, ss.HandlePeerTransferEntityRsp)

	ss.OnPeerHello = ss.HandlePeerHello
	ss.OnDisconnect = ss.HandleConnDisconnect

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
	ss.CheckSceneTriggers(now)
	ss.ClearExpiredTransfers(now)
	ss.peerMgr.OnTick(now)
	ss.CheckTransferTimeouts(now)

	for account, pend := range ss.dbLoadPendings {
		if now.After(pend.deadline) {
			delete(ss.dbLoadPendings, account)
			log.Printf("[world %s] LoadPlayer timeout for account=%s, gateway will roll back", ss.ServerID, account)
		}
	}

	if now.Sub(ss.lastAutosave) >= autosaveInterval {
		ss.lastAutosave = now
		ss.AutosaveAll()
	}

	if ss.shutdownFlushing && now.After(ss.shutdownDeadline) {
		ss.shutdownFlushing = false
		log.Printf("[world %s] player flush incomplete before shutdown", ss.ServerID)
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerID})
		go ss.Stop()
	}
}

func (ss *worldServer) HandleMoveStart(conn *common.ConnWrapper, playerID uint64, to pb.MoveState, ts int64, dirX, dirZ float64) {
	entity, ok := ss.players[playerID]
	if ok && entity.Transferring && !ss.TryFinishTransfer(entity, ts) {
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, &common.MoveRsp{Success: false, PlayerId: playerID, Message: "正在传送"})
		return
	}
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
			ss.ServerID, playerID, pb.MoveState_name[int32(to)], logX, logZ, logY, logLayer)
	}
}

func (ss *worldServer) HandleWalkStartReq(conn *common.ConnWrapper, req *common.WalkStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_WALK, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleRunStartReq(conn *common.ConnWrapper, req *common.RunStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_RUN, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleSprintStartReq(conn *common.ConnWrapper, req *common.SprintStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_SPRINT, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleJumpStartReq(conn *common.ConnWrapper, req *common.JumpStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_JUMP_UP, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleDashStartReq(conn *common.ConnWrapper, req *common.DashStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_DASH, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleRollStartReq(conn *common.ConnWrapper, req *common.RollStartReq) {
	ss.HandleMoveStart(conn, req.PlayerId, pb.MoveState_MOVE_ROLL, req.ServerTimeMs, float64(req.Dir.X), float64(req.Dir.Z))
}

func (ss *worldServer) HandleStopStartReq(conn *common.ConnWrapper, req *common.StopStartReq) {
	switch req.StopKind {
	case pb.MoveState_MOVE_STOP_LIGHT, pb.MoveState_MOVE_STOP_MED, pb.MoveState_MOVE_STOP_HARD:
		ss.HandleMoveStart(conn, req.PlayerId, req.StopKind, req.ServerTimeMs, 0, 0)
	default:
		rsp := &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "无效的停止强度"}
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, rsp)
	}
}

func (ss *worldServer) HandleMoveStopReq(conn *common.ConnWrapper, req *common.MoveStopReq) {
	entity, ok := ss.players[req.PlayerId]
	if ok && entity.Transferring && !ss.TryFinishTransfer(entity, req.ServerTimeMs) {
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "正在传送"})
		return
	}
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

func (ss *worldServer) HandleMoveDirChangeReq(conn *common.ConnWrapper, req *common.MoveDirChangeReq) {
	entity, ok := ss.players[req.PlayerId]
	if ok && entity.Transferring && !ss.TryFinishTransfer(entity, req.ServerTimeMs) {
		common.SendMsg(conn, common.Wd2Cli_MoveRsp, &common.MoveRsp{Success: false, PlayerId: req.PlayerId, Message: "正在传送"})
		return
	}
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

func (ss *worldServer) HandleSkillReq(conn *common.ConnWrapper, req *common.SkillReq) {
	entity, ok := ss.players[req.PlayerId]
	if ok && entity.Transferring {
		rsp := common.SkillRsp{Success: false, PlayerId: req.PlayerId, Message: "正在传送"}
		common.SendMsg(conn, common.Wd2Cli_SkillRsp, &rsp)
		return
	}

	if !ok {
		rsp := common.SkillRsp{Success: false, PlayerId: req.PlayerId, Message: "实体不存在"}
		common.SendMsg(conn, common.Wd2Cli_SkillRsp, &rsp)
		return
	}

	rsp := common.SkillRsp{Success: true, PlayerId: req.PlayerId, Message: "技能释放成功: " + req.SkillId}
	common.SendMsg(conn, common.Wd2Cli_SkillRsp, &rsp)
	log.Printf("[world %s] player %d used skill %s", ss.ServerID, req.PlayerId, req.SkillId)
}

func (ss *worldServer) HandleDestroyEntityReq(conn *common.ConnWrapper, req *common.DestroyEntityReq) {
	entity, ok := ss.players[req.PlayerId]
	if !ok {
		rsp := common.DestroyEntityRsp{Success: false, PlayerId: req.PlayerId, Message: "entity not found", SessionId: req.SessionId}
		common.SendMsg(conn, common.Wd2Gw_DestroyEntityRsp, &rsp)
		return
	}

	if (entity.GatewayID != "" && req.GatewayId != "" && entity.GatewayID != req.GatewayId) ||
		(entity.SessionID != "" && req.SessionId != "" && entity.SessionID != req.SessionId) {
		log.Printf("[world %s] reject stale destroy for player %d: owner gateway=%s session=%s, request gateway=%s session=%s",
			ss.ServerID, req.PlayerId, entity.GatewayID, entity.SessionID, req.GatewayId, req.SessionId)
		rsp := common.DestroyEntityRsp{Success: false, PlayerId: req.PlayerId, Message: "stale session", SessionId: entity.SessionID}
		common.SendMsg(conn, common.Wd2Gw_DestroyEntityRsp, &rsp)
		return
	}

	saved := entity
	ss.DetachPlayer(entity)

	rsp := common.DestroyEntityRsp{Success: true, PlayerId: req.PlayerId, Message: "destroyed", SessionId: saved.SessionID}
	common.SendMsg(conn, common.Wd2Gw_DestroyEntityRsp, &rsp)
	log.Printf("[world %s] player %d session %s entity destroyed", ss.ServerID, req.PlayerId, saved.SessionID)
}
func (ss *worldServer) ResumePlayerEntity(conn *common.ConnWrapper, req *common.CreateEntityReq, entity *playerEntity) {
	oldGateway, oldSession := entity.GatewayID, entity.SessionID
	if oldGateway != req.GatewayId || oldSession != req.SessionId {
		entity.GatewayID = req.GatewayId
		entity.SessionID = req.SessionId
	}
	entity.GatewayConn = conn

	sceneID := ""
	var width, height float64
	if entity.Scene != nil {
		sceneID = entity.Scene.SceneID
		width = entity.Scene.Width
		height = entity.Scene.Height
	}

	rsp := common.CreateEntityRsp{
		Success:   true,
		PlayerId:  entity.PlayerID,
		Account:   entity.Account,
		X:         entity.X,
		Z:         entity.Z,
		Width:     width,
		Height:    height,
		Message:   "resumed",
		SceneId:   sceneID,
		SessionId: req.SessionId,
	}
	common.SendMsg(conn, common.Wd2Gw_CreateEntityRsp, &rsp)
	log.Printf("[world %s] player %d (account=%s) session=%s resumed at (%.1f, %.1f), old gateway=%s old session=%s",
		ss.ServerID, entity.PlayerID, entity.Account, req.SessionId, entity.X, entity.Z, oldGateway, oldSession)
}
func (ss *worldServer) HandleResumeEntityReq(conn *common.ConnWrapper, req *common.ResumeEntityReq) {
	if req.GatewayId == "" || req.SessionId == "" {
		rsp := common.ResumeEntityRsp{Success: false, Message: "invalid owner", PlayerId: req.PlayerId, SessionId: req.SessionId}
		common.SendMsg(conn, common.Wd2Gw_ResumeEntityRsp, &rsp)
		return
	}
	entity, ok := ss.players[req.PlayerId]
	if !ok {
		rsp := common.ResumeEntityRsp{Success: false, Message: "entity not found", PlayerId: req.PlayerId, SessionId: req.SessionId}
		common.SendMsg(conn, common.Wd2Gw_ResumeEntityRsp, &rsp)
		return
	}

	oldGateway, oldSession := entity.GatewayID, entity.SessionID
	entity.GatewayID = req.GatewayId
	entity.SessionID = req.SessionId
	entity.GatewayConn = conn

	sceneID := ""
	var width, height float64
	if entity.Scene != nil {
		sceneID = entity.Scene.SceneID
		width = entity.Scene.Width
		height = entity.Scene.Height
	}
	rsp := common.ResumeEntityRsp{
		Success:   true,
		Message:   "resumed",
		PlayerId:  entity.PlayerID,
		SessionId: req.SessionId,
		SceneId:   sceneID,
		X:         entity.X,
		Z:         entity.Z,
		Width:     width,
		Height:    height,
	}
	common.SendMsg(conn, common.Wd2Gw_ResumeEntityRsp, &rsp)
	log.Printf("[world %s] player %d session=%s resumed from gateway=%s session=%s at (%.1f, %.1f)",
		ss.ServerID, entity.PlayerID, req.SessionId, oldGateway, oldSession, entity.X, entity.Z)
}
func (ss *worldServer) HandleCreateEntityReq(conn *common.ConnWrapper, req *common.CreateEntityReq) {
	log.Printf("[world %s] creating player: account=%s gateway=%s session=%s", ss.ServerID, req.Account, req.GatewayId, req.SessionId)

	if entity, ok := ss.playersByAccount[req.Account]; ok {
		delete(ss.dbLoadPendings, req.Account)
		ss.ResumePlayerEntity(conn, req, entity)
		return
	}

	if ss.dbConn == nil {
		ss.CreateEntityLocal(conn, req.Account, req.GatewayId, req.SessionId)
		return
	}

	pend := &dbLoadPending{
		conn:      conn,
		account:   req.Account,
		gatewayID: req.GatewayId,
		sessionID: req.SessionId,
		deadline:  time.Now().Add(loadPlayerTimeout),
	}
	ss.dbLoadPendings[req.Account] = pend

	if err := ss.ForwardMsgToDb(common.Wd2Db_LoadPlayerReq, &common.LoadPlayerReq{Account: req.Account}); err != nil {
		delete(ss.dbLoadPendings, req.Account)
		ss.CreateEntityLocal(conn, req.Account, req.GatewayId, req.SessionId)
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
			ss.ServerID, rsp.Account, rsp.Message)
		ss.CreateEntityLocal(pend.conn, rsp.Account, pend.gatewayID, pend.sessionID)
		return
	}

	sc := ss.sceneMgr.GetOrCreate(rsp.SceneId)
	resume := rsp.Found && rsp.SceneId == sc.SceneID
	x, z, k := sc.SpawnPosition(resume, rsp.X, rsp.Z)
	entity := &playerEntity{
		PlayerID:    rsp.PlayerId,
		Account:     rsp.Account,
		GatewayID:   pend.gatewayID,
		SessionID:   pend.sessionID,
		GatewayConn: pend.conn,
		X:           x,
		Z:           z,
		VoxelK:      k,
		Scene:       sc,
	}
	if sc.voxelGrid != nil {
		entity.Y = sc.voxelGrid.SurfaceHeight(x, z, k)
	}
	ss.InitEntityTimeline(entity)
	ss.ReplacePlayerEntity(entity)

	width, height := sc.Width, sc.Height
	createRsp := common.CreateEntityRsp{
		Success:   true,
		PlayerId:  rsp.PlayerId,
		Account:   rsp.Account,
		X:         x,
		Z:         z,
		Width:     width,
		Height:    height,
		Message:   "created",
		SceneId:   sc.SceneID,
		SessionId: pend.sessionID,
	}
	common.SendMsg(pend.conn, common.Wd2Gw_CreateEntityRsp, &createRsp)
	log.Printf("[world %s] player %d (account=%s) session=%s loaded at (%.1f, %.1f) found=%v",
		ss.ServerID, rsp.PlayerId, rsp.Account, pend.sessionID, x, z, rsp.Found)
}

func (ss *worldServer) CreateEntityLocal(conn *common.ConnWrapper, account, gatewayID, sessionID string) {
	ss.playerCounter++
	playerID := uint64(ss.serverNumID)*10000 + ss.playerCounter

	sc := ss.sceneMgr.GetOrCreate(ss.sceneMgr.defaultScene)
	x, z, k := sc.SpawnPosition(false, 0, 0)
	entity := &playerEntity{
		PlayerID:    playerID,
		Account:     account,
		GatewayID:   gatewayID,
		SessionID:   sessionID,
		GatewayConn: conn,
		X:           x,
		Z:           z,
		VoxelK:      k,
		Scene:       sc,
	}
	if sc.voxelGrid != nil {
		entity.Y = sc.voxelGrid.SurfaceHeight(x, z, k)
	}
	ss.InitEntityTimeline(entity)
	ss.ReplacePlayerEntity(entity)

	width, height := sc.Width, sc.Height
	rsp := common.CreateEntityRsp{
		Success:   true,
		PlayerId:  playerID,
		Account:   account,
		X:         entity.X,
		Z:         entity.Z,
		Width:     width,
		Height:    height,
		Message:   "created local fallback",
		SceneId:   sc.SceneID,
		SessionId: sessionID,
	}
	common.SendMsg(conn, common.Wd2Gw_CreateEntityRsp, &rsp)
	log.Printf("[world %s] player %d (account=%s) session=%s created locally at (%.1f, %.1f)",
		ss.ServerID, playerID, account, sessionID, entity.X, entity.Z)
}

func (ss *worldServer) DetachPlayer(entity *playerEntity) {
	if entity == nil {
		return
	}
	delete(ss.players, entity.PlayerID)
	if entity.Account != "" && ss.playersByAccount[entity.Account] == entity {
		delete(ss.playersByAccount, entity.Account)
	}
	if entity.Scene != nil {
		delete(entity.Scene.Players, entity.PlayerID)
	}
	ss.SavePlayer(entity)
}

func (ss *worldServer) ReplacePlayerEntity(entity *playerEntity) {
	if old, ok := ss.players[entity.PlayerID]; ok && old != entity {
		ss.DetachPlayer(old)
	}
	if old, ok := ss.playersByAccount[entity.Account]; ok && old != entity {
		ss.DetachPlayer(old)
	}
	entity.Scene.Players[entity.PlayerID] = entity
	ss.players[entity.PlayerID] = entity
	ss.playersByAccount[entity.Account] = entity
}
func (ss *worldServer) HandleDbSavePlayerRsp(_ *common.ConnWrapper, rsp *common.SavePlayerRsp) {
	if rsp.ReqId == 0 {

		if !rsp.Success {
			log.Printf("[world %s] dbproxy save error: %s", ss.ServerID, rsp.Message)
		}
		return
	}

	if ss.shutdownFlushing && rsp.ReqId == ss.shutdownReqID {
		ss.shutdownFlushing = false
		log.Printf("[world %s] flushed players to dbproxy, shutting down", ss.ServerID)
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerID})
		go ss.Stop()
	}
}

func (ss *worldServer) NoopHeartbeat(_ *common.ConnWrapper, _ *common.HeartbeatRsp) {}

func (ss *worldServer) HandleCentralHelloRsp(_ *common.ConnWrapper, rsp *common.HelloRsp) {
	if !rsp.Success {
		log.Printf("[world %s] hello rejected: %s", ss.ServerID, rsp.Message)
		return
	}
	log.Printf("[world %s] registered: %s", ss.ServerID, rsp.Message)

	ss.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerDbProxy})
	ss.SendToCentralMsg(common.Srv2Ct_ServerListReq, &common.ServerListReq{Type: common.ServerWorld})
}

func (ss *worldServer) HandleCentralServerListRsp(_ *common.ConnWrapper, rsp *common.ServerListRsp) {
	for _, e := range rsp.Servers {
		switch e.ServerType {
		case common.ServerDbProxy:
			ss.ConnectToDb(e.ServerId, e.ListenAddr)
		case common.ServerWorld:
			ss.peerMgr.AddRoute(e.ServerId, e.ListenAddr)
		}
	}
}

func (ss *worldServer) HandleCentralNewDbProxyNotify(_ *common.ConnWrapper, entry *common.ServerEntry) {
	ss.ConnectToDb(entry.ServerId, entry.ListenAddr)
}

func (ss *worldServer) HandleCentralNewWorldNotify(_ *common.ConnWrapper, entry *common.ServerEntry) {
	if entry.ServerType == common.ServerWorld && entry.ServerId != ss.ServerID {
		ss.peerMgr.AddRoute(entry.ServerId, entry.ListenAddr)
	}
}

func (ss *worldServer) HandleCentralWorldOfflineNotify(_ *common.ConnWrapper, entry *common.ServerEntry) {
	if entry.ServerType == common.ServerWorld && entry.ServerId != ss.ServerID {
		ss.peerMgr.RemoveRoute(entry.ServerId)
	}
}

func (ss *worldServer) HandlePeerHello(conn *common.ConnWrapper, req *common.HelloReq) *common.HelloRsp {
	switch req.ServerType {
	case common.ServerWorld:
		return ss.peerMgr.HandlePeerHello(conn, req)
	case common.ServerGateway:
		ss.gatewayConns[req.ServerId] = conn
		return &common.HelloRsp{Success: true, Message: "gateway registered", Registered: true}
	default:
		return &common.HelloRsp{Success: false, Message: "unsupported peer type"}
	}
}

func (ss *worldServer) HandleConnDisconnect(conn *common.ConnWrapper) {
	switch conn.PeerType {
	case common.ServerWorld:
		ss.peerMgr.HandleDisconnect(conn)
	case common.ServerGateway:
		for id, c := range ss.gatewayConns {
			if c == conn {
				delete(ss.gatewayConns, id)
			}
		}
	}
}

func (ss *worldServer) HandleCentralShutdownNotify(_ *common.ConnWrapper, notify *common.ShutdownNotify) {
	ss.HandleShutdown(notify)
}

func (ss *worldServer) HandleShutdown(notify *common.ShutdownNotify) {
	log.Printf("[world %s] shutdown requested: %s", ss.ServerID, notify.Reason)
	if ss.shutdownFlushing {
		return
	}

	players := make([]*common.PlayerData, 0, len(ss.players))
	for _, e := range ss.players {
		if e.Transferring || e.IncomingTransfer {
			continue
		}
		players = append(players, &common.PlayerData{PlayerId: e.PlayerID, Account: e.Account, X: e.X, Z: e.Z, SceneId: entitySceneID(e)})
	}
	if len(players) == 0 {
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerID})
		go ss.Stop()
		return
	}

	ss.shutdownReqID++
	req := &common.SavePlayerReq{ReqId: ss.shutdownReqID, Players: players}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] shutdown flush send failed: %v", ss.ServerID, err)
		ss.SendToCentralMsg(common.Srv2Ct_ShutdownAck, &common.ShutdownAck{ServerId: ss.ServerID})
		go ss.Stop()
		return
	}
	ss.shutdownFlushing = true
	ss.shutdownDeadline = time.Now().Add(5 * time.Second)
	log.Printf("[world %s] flushing %d players to dbproxy", ss.ServerID, len(players))
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
	ss.dbMu.Lock()
	ss.dbID = dbID
	ss.dbAddr = dbAddr
	if ss.dbConn != nil || ss.dbConnecting {
		ss.dbMu.Unlock()
		return
	}
	ss.dbConnecting = true
	ss.dbMu.Unlock()

	go ss.DbConnectLoop()
}

func (ss *worldServer) DbConnectLoop() {
	backoff := time.Second
	for {
		select {
		case <-ss.Done():
			ss.SetDBConnecting(false)
			return
		default:
		}

		ss.dbMu.Lock()
		dbID, dbAddr := ss.dbID, ss.dbAddr
		ss.dbMu.Unlock()
		if dbAddr == "" {
			ss.SetDBConnecting(false)
			return
		}

		conn, err := net.DialTimeout("tcp", dbAddr, 5*time.Second)
		if err != nil {
			log.Printf("[world %s] failed to connect to dbproxy %s at %s: %v, retry in %v", ss.ServerID, dbID, dbAddr, err, backoff)
			select {
			case <-ss.Done():
				return
			case <-time.After(backoff):
			}
			if backoff < 30*time.Second {
				backoff *= 2
			}
			continue
		}

		result := make(chan bool, 1)
		posted := ss.Loop.Post(common.Event{Kind: common.EventDefer, Fn: func() {
			if ss.dbConn != nil {
				_ = conn.Close()
				ss.SetDBConnecting(false)
				result <- true
				return
			}
			cw := common.NewConnWrapper(conn)
			cw.PeerType = common.ServerDbProxy
			ss.dbConn = cw
			ss.SetDBConnecting(false)
			log.Printf("[world %s] connected to dbproxy %s at %s", ss.ServerID, dbID, dbAddr)
			ss.SendHello(cw)
			go ss.DbReadLoop(cw)
			result <- true
		}})
		if !posted {
			_ = conn.Close()
			ss.SetDBConnecting(false)
			return
		}
		if <-result {
			return
		}
	}
}

func (ss *worldServer) SetDBConnecting(value bool) {
	ss.dbMu.Lock()
	ss.dbConnecting = value
	ss.dbMu.Unlock()
}

func (ss *worldServer) DbReadLoop(cw *common.ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[world %s] DbReadLoop panic recovered: %v", ss.ServerID, r)
		}
	}()
	for {
		msg, err := common.ReadMessage(cw.Conn)
		if err != nil {
			ss.Loop.Defer(func() {
				if ss.dbConn == cw {
					_ = ss.dbConn.Close()
					ss.dbConn = nil
					log.Printf("[world %s] dbproxy connection lost: %v", ss.ServerID, err)
				}
				ss.SetDBConnecting(false)
				ss.ConnectToDb(ss.dbID, ss.dbAddr)
			})
			return
		}
		if !ss.Loop.Post(common.Event{Kind: common.EventMessage, Conn: cw, Msg: msg}) {
			cw.Close()
			ss.Loop.Defer(func() {
				if ss.dbConn == cw {
					ss.dbConn = nil
				}
				ss.SetDBConnecting(false)
				ss.ConnectToDb(ss.dbID, ss.dbAddr)
			})
			return
		}
	}
}
func entitySceneID(e *playerEntity) string {
	if e.Scene == nil {
		return ""
	}
	return e.Scene.SceneID
}

func (ss *worldServer) SavePlayer(e *playerEntity) {
	req := &common.SavePlayerReq{
		Players: []*common.PlayerData{
			{PlayerId: e.PlayerID, Account: e.Account, X: e.X, Z: e.Z, SceneId: entitySceneID(e)},
		},
	}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, req); err != nil {
		log.Printf("[world %s] save player %d failed: %v", ss.ServerID, e.PlayerID, err)
	}
}

func (ss *worldServer) AutosaveAll() {
	players := make([]*common.PlayerData, 0, len(ss.players))
	for _, e := range ss.players {
		if e.Transferring || e.IncomingTransfer {
			continue
		}
		players = append(players, &common.PlayerData{
			PlayerId: e.PlayerID, Account: e.Account, X: e.X, Z: e.Z, SceneId: entitySceneID(e),
		})
	}
	if len(players) == 0 {
		return
	}
	if err := ss.ForwardMsgToDb(common.Wd2Db_SavePlayerReq, &common.SavePlayerReq{Players: players}); err != nil {
		log.Printf("[world %s] autosave failed: %v", ss.ServerID, err)
		return
	}
	log.Printf("[world %s] autosaved %d players", ss.ServerID, len(players))
}
