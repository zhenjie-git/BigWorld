package main

import (
	"bigworld/world/Config"
	"time"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

const (
	aoiCellSize         = 32.0
	aoiCellRadius       = 1
	aoiBorderBand       = 96.0
	aoiBorderEveryTicks = 10
	aoiSnapEveryTicks   = 20
	foreignExpireMs     = 3000
)

type aoiCellKey struct {
	X, Z int
}

type foreignEntity struct {
	state    entitySnapshot
	sceneID  config.SceneID
	worldID  string
	account  string
	lastSeen time.Time
}

type aoiCandidate struct {
	id      uint64
	account string
	snap    entitySnapshot
}

func (ss *worldServer) aoiCellOf(x, z float64) aoiCellKey {
	return aoiCellKey{X: int(config.FloorDiv(x, aoiCellSize)), Z: int(config.FloorDiv(z, aoiCellSize))}
}

func (ss *worldServer) buildAoiGrid() map[int]map[aoiCellKey][]aoiCandidate {
	grid := make(map[int]map[aoiCellKey][]aoiCandidate)
	add := func(sceneID config.SceneID, id uint64, account string, snap entitySnapshot) {
		template := sceneID.Template()
		cells := grid[template]
		if cells == nil {
			cells = make(map[aoiCellKey][]aoiCandidate)
			grid[template] = cells
		}
		key := ss.aoiCellOf(snap.X, snap.Z)
		cells[key] = append(cells[key], aoiCandidate{id: id, account: account, snap: snap})
	}
	for _, e := range ss.players {
		if e == nil || e.Scene == nil {
			continue
		}
		add(e.Scene.SceneID, e.PlayerID, e.Account, CaptureEntitySnapshot(e))
	}
	for id, f := range ss.foreigns {
		add(f.sceneID, id, f.account, f.state)
	}
	return grid
}

func (ss *worldServer) visibleCandidates(grid map[int]map[aoiCellKey][]aoiCandidate, e *playerEntity) []aoiCandidate {
	if e.Scene == nil {
		return nil
	}
	cells := grid[e.Scene.SceneID.Template()]
	if cells == nil {
		return nil
	}
	center := ss.aoiCellOf(e.X, e.Z)
	var visible []aoiCandidate
	for dx := -aoiCellRadius; dx <= aoiCellRadius; dx++ {
		for dz := -aoiCellRadius; dz <= aoiCellRadius; dz++ {
			visible = append(visible, cells[aoiCellKey{X: center.X + dx, Z: center.Z + dz}]...)
		}
	}
	return visible
}

func entityStateFromSnapshot(id uint64, account string, s entitySnapshot) *common.EntityStatePb {
	return &common.EntityStatePb{
		PlayerId:     id,
		Account:      account,
		X:            s.X,
		Y:            s.Y,
		Z:            s.Z,
		VoxelK:       int32(s.VoxelK),
		Airborne:     s.Airborne,
		State:        s.State,
		DirX:         s.MoveDirX,
		DirZ:         s.MoveDirZ,
		CurveNorm:    s.CurveNorm,
		StateStartMs: s.StateStartMs,
		FallVelY:     s.FallVelY,
		SimTick:      s.Tick,
	}
}

func (ss *worldServer) sendToEntity(e *playerEntity, msgType common.MessageType, m proto.Message) {
	if e.GatewayConn == nil {
		return
	}
	_ = common.SendMsg(e.GatewayConn, msgType, m)
}

func (ss *worldServer) RunAoi(now time.Time, nowTick int64) {
	ss.expireForeigns(now)
	grid := ss.buildAoiGrid()

	for _, e := range ss.players {
		if e == nil || e.Scene == nil || !e.Phase.JoinsAoi() {
			continue
		}
		if e.KnownEntities == nil {
			e.KnownEntities = make(map[uint64]bool)
		}
		visible := ss.visibleCandidates(grid, e)
		visSet := make(map[uint64]bool, len(visible))
		for _, c := range visible {
			if c.id == e.PlayerID {
				continue
			}
			visSet[c.id] = true
			if e.KnownEntities[c.id] {
				continue
			}
			e.KnownEntities[c.id] = true
			ss.sendToEntity(e, common.Wd2Cli_EntityEnterNotify, &common.EntityEnterNotify{
				TargetPlayerId: e.PlayerID,
				Entity:         entityStateFromSnapshot(c.id, c.account, c.snap),
			})
		}
		for id := range e.KnownEntities {
			if visSet[id] {
				continue
			}
			delete(e.KnownEntities, id)
			ss.sendToEntity(e, common.Wd2Cli_EntityLeaveNotify, &common.EntityLeaveNotify{
				TargetPlayerId: e.PlayerID,
				PlayerId:       id,
			})
		}
	}

	if nowTick%aoiSnapEveryTicks == 0 {
		subjects := make(map[uint64]aoiCandidate, len(ss.players))
		for _, e := range ss.players {
			if e == nil || e.Scene == nil || !e.Phase.JoinsAoi() {
				continue
			}
			if len(e.KnownEntities) == 0 {
				continue
			}
			subjects[e.PlayerID] = aoiCandidate{id: e.PlayerID, account: e.Account, snap: CaptureEntitySnapshot(e)}
		}
		for _, e := range ss.players {
			if e == nil || e.GatewayConn == nil || len(e.KnownEntities) == 0 {
				continue
			}
			for id := range e.KnownEntities {
				subject, ok := subjects[id]
				if !ok {
					continue
				}
				ss.sendToEntity(e, common.Wd2Cli_EntitySnapshotNotify, &common.EntitySnapshotNotify{
					TargetPlayerId: e.PlayerID,
					Entity:         entityStateFromSnapshot(subject.id, subject.account, subject.snap),
				})
			}
		}
	}

	if nowTick%aoiBorderEveryTicks == 0 {
		ss.forwardBorderInterest()
	}
}

func (ss *worldServer) BroadcastEntityMove(e *playerEntity, in moveInput) {
	if e == nil || e.Scene == nil || len(e.KnownEntities) == 0 {
		return
	}
	notify := &common.EntityMoveEventNotify{
		PlayerId:     e.PlayerID,
		Kind:         uint32(in.Kind),
		State:        in.State,
		DirX:         in.DirX,
		DirZ:         in.DirZ,
		ServerTimeMs: ss.TickToMs(in.Tick),
	}
	for id := range e.KnownEntities {
		watcher := ss.players[id]
		if watcher == nil || watcher.GatewayConn == nil {
			continue
		}
		notify.TargetPlayerId = watcher.PlayerID
		ss.sendToEntity(watcher, common.Wd2Cli_EntityMoveEventNotify, notify)
	}
}

func (ss *worldServer) forwardBorderInterest() {
	byZone := make(map[config.SceneID][]*common.EntityStatePb)
	for _, e := range ss.players {
		if e == nil || e.Scene == nil || !e.Phase.JoinsAoi() {
			continue
		}
		entry, ok := config.SceneConfig().Get(e.Scene.SceneID)
		if !ok || zoneDepth(entry, e.X, e.Z) > aoiBorderBand {
			continue
		}
		neighbors := config.SceneConfig().NeighborZones(e.Scene.SceneID)
		needsForward := false
		for _, nz := range neighbors {
			if !ss.zoneHostedLocally(nz) {
				needsForward = true
				break
			}
		}
		if !needsForward {
			continue
		}
		byZone[e.Scene.SceneID] = append(byZone[e.Scene.SceneID], entityStateFromSnapshot(e.PlayerID, e.Account, CaptureEntitySnapshot(e)))
	}
	if len(byZone) == 0 {
		return
	}
	for zoneID, entities := range byZone {
		notify := &common.BorderInterestNotify{
			FromWorldId: ss.ServerID,
			SceneId:     int64(zoneID),
			Entities:    entities,
		}
		ss.peerMgr.BroadcastPeers(common.Wd2Wd_BorderInterestNotify, notify)
	}
}

func (ss *worldServer) zoneHostedLocally(sceneID config.SceneID) bool {
	return ss.sceneMgr.PlayerCount(sceneID) > 0
}

func (ss *worldServer) foreignRelevant(sceneID config.SceneID) bool {
	for _, nz := range config.SceneConfig().NeighborZones(sceneID) {
		if ss.zoneHostedLocally(nz) {
			return true
		}
	}
	return ss.zoneHostedLocally(sceneID)
}

func (ss *worldServer) HandlePeerBorderInterestNotify(conn *common.ConnWrapper, notify *common.BorderInterestNotify) {
	peer := ss.peerMgr.PeerByConn(conn)
	if peer == nil {
		return
	}
	now := time.Now()
	for _, id := range notify.RemovedPlayerIds {
		if f := ss.foreigns[id]; f != nil && f.worldID == peer.ID {
			delete(ss.foreigns, id)
		}
	}
	for _, st := range notify.Entities {
		if st == nil || st.PlayerId == 0 {
			continue
		}
		if ss.players[st.PlayerId] != nil {
			continue
		}
		if !ss.foreignRelevant(config.SceneID(notify.SceneId)) {
			continue
		}
		ss.foreigns[st.PlayerId] = &foreignEntity{
			state: entitySnapshot{
				Tick:         st.SimTick,
				X:            st.X,
				Y:            st.Y,
				Z:            st.Z,
				VoxelK:       int(st.VoxelK),
				Airborne:     st.Airborne,
				State:        st.State,
				MoveDirX:     st.DirX,
				MoveDirZ:     st.DirZ,
				CurveNorm:    st.CurveNorm,
				StateStartMs: st.StateStartMs,
				FallVelY:     st.FallVelY,
			},
			sceneID:  config.SceneID(notify.SceneId),
			worldID:  peer.ID,
			account:  st.Account,
			lastSeen: now,
		}
	}
}

func (ss *worldServer) RemoveForeign(playerID uint64) {
	delete(ss.foreigns, playerID)
}

func (ss *worldServer) expireForeigns(now time.Time) {
	for id, f := range ss.foreigns {
		if now.Sub(f.lastSeen) > foreignExpireMs {
			delete(ss.foreigns, id)
		}
	}
}
