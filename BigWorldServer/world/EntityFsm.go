package main

import (
	"fmt"
	"log"
	"sort"
	"strings"

	pb "bigworld/common/pb"
	"bigworld/world/Config"
)

type entityPhase uint8

const (
	PhaseActive entityPhase = iota
	PhaseTeleportFrozen
	PhaseZoneHandoff
	PhaseWorldHandoff
	PhaseArriving
	PhaseArrivingFrozen
)

var phaseNames = [...]string{
	PhaseActive:         "Active",
	PhaseTeleportFrozen: "TeleportFrozen",
	PhaseZoneHandoff:    "ZoneHandoff",
	PhaseWorldHandoff:   "WorldHandoff",
	PhaseArriving:       "Arriving",
	PhaseArrivingFrozen: "ArrivingFrozen",
}

type phaseDef struct {
	acceptsMoveInput bool
	simulates        bool
	joinsAoi         bool
	sendsMoveRsp     bool
	canStartTravel   bool
	savesToDb        bool
	forcesMoveIdle   bool
}

func (p entityPhase) def() phaseDef {
	d, ok := config.Phases().Def(phaseNames[p])
	if !ok {
		log.Panicf("phase %s missing from config", phaseNames[p])
	}
	return phaseDef{
		acceptsMoveInput: d.AcceptsMoveInput,
		simulates:        d.Simulates,
		joinsAoi:         d.JoinsAoi,
		sendsMoveRsp:     d.SendsMoveRsp,
		canStartTravel:   d.CanStartTravel,
		savesToDb:        d.SavesToDb,
		forcesMoveIdle:   d.ForcesMoveIdle,
	}
}

func (p entityPhase) AcceptsMoveInput() bool { return p.def().acceptsMoveInput }
func (p entityPhase) Simulates() bool        { return p.def().simulates }
func (p entityPhase) JoinsAoi() bool         { return p.def().joinsAoi }
func (p entityPhase) SendsMoveRsp() bool     { return p.def().sendsMoveRsp }
func (p entityPhase) CanStartTravel() bool   { return p.def().canStartTravel }
func (p entityPhase) SavesToDb() bool        { return p.def().savesToDb }

func phaseTransitionAllowed(from, to entityPhase) bool {
	return config.Phases().Allows(phaseNames[from], phaseNames[to])
}

func validatePhaseConfig() {
	rows := make(map[string]bool, len(phaseNames))
	for _, name := range config.Phases().Names() {
		rows[name] = true
	}
	for _, name := range phaseNames {
		if !rows[name] {
			log.Fatalf("phase table missing phase row: %s", name)
		}
		delete(rows, name)
	}
	for name := range rows {
		log.Fatalf("phase table has unknown phase row: %s", name)
	}
}

func (ss *worldServer) applyPhase(e *playerEntity, p entityPhase) {
	if e.Phase == p {
		return
	}
	if !phaseTransitionAllowed(e.Phase, p) {
		log.Panicf("[world %s] illegal phase transition %s -> %s player=%d", ss.ServerID, phaseNames[e.Phase], phaseNames[p], e.PlayerID)
	}
	e.Phase = p
	e.TransferID = ""
	e.PhaseStartTick = 0
	e.PhaseUntilMs = 0
	if p.def().forcesMoveIdle {
		ss.resetMovement(e)
	}
}

func (ss *worldServer) resetMovement(e *playerEntity) {
	e.State = pb.MoveState_MOVE_IDLE
	e.MoveDirX = 0
	e.MoveDirZ = 0
	e.CurveNorm = 0
	e.FallVelY = 0
	e.Airborne = false
	e.Inputs = nil
	e.InputSeq = 0
	ss.InitEntityTimeline(e)
}

func (ss *worldServer) beginTeleportFreeze(e *playerEntity, untilMs int64) {
	ss.applyPhase(e, PhaseTeleportFrozen)
	e.PhaseStartTick = ss.NowTick()
	e.PhaseUntilMs = untilMs
}

type moveEvent uint8

const (
	MoveEventWalk moveEvent = iota
	MoveEventRun
	MoveEventSprint
	MoveEventJump
	MoveEventDash
	MoveEventRoll
	MoveEventStopLight
	MoveEventStopMed
	MoveEventStopHard
	MoveEventHalt
	MoveEventDirChange
	MoveEventCount
)

var moveEventNames = [MoveEventCount]string{
	"Walk", "Run", "Sprint", "Jump", "Dash", "Roll",
	"StopLight", "StopMed", "StopHard", "Halt", "DirChange",
}

var moveEventTargets = map[moveEvent]pb.MoveState{
	MoveEventWalk:      pb.MoveState_MOVE_WALK,
	MoveEventRun:       pb.MoveState_MOVE_RUN,
	MoveEventSprint:    pb.MoveState_MOVE_SPRINT,
	MoveEventJump:      pb.MoveState_MOVE_JUMP_UP,
	MoveEventDash:      pb.MoveState_MOVE_DASH,
	MoveEventRoll:      pb.MoveState_MOVE_ROLL,
	MoveEventStopLight: pb.MoveState_MOVE_STOP_LIGHT,
	MoveEventStopMed:   pb.MoveState_MOVE_STOP_MED,
	MoveEventStopHard:  pb.MoveState_MOVE_STOP_HARD,
	MoveEventHalt:      pb.MoveState_MOVE_IDLE,
}

var moveTargetEvents = func() map[pb.MoveState]moveEvent {
	reversed := make(map[pb.MoveState]moveEvent, len(moveEventTargets))
	for ev, target := range moveEventTargets {
		reversed[target] = ev
	}
	return reversed
}()

func (ss *worldServer) buildMovePolicy() {
	ss.movePolicy = make(map[pb.MoveState][MoveEventCount]pb.EventPolicy)
	for state := range pb.MoveState_name {
		from := pb.MoveState(state)
		var row [MoveEventCount]pb.EventPolicy
		for ev := moveEvent(0); ev < MoveEventCount; ev++ {
			p, ok := config.MoveTransitions().PolicyOf(from, moveEventNames[ev])
			if !ok {
				log.Fatalf("[world %s] move policy matrix missing cell state=%s event=%s", ss.ServerID, pb.MoveState_name[state], moveEventNames[ev])
			}
			row[ev] = p
		}
		ss.movePolicy[from] = row
	}
}

func (ss *worldServer) logMovePolicy() {
	states := make([]int32, 0, len(pb.MoveState_name))
	for state := range pb.MoveState_name {
		states = append(states, state)
	}
	sort.Slice(states, func(i, j int) bool { return states[i] < states[j] })
	for _, state := range states {
		row := ss.movePolicy[pb.MoveState(state)]
		cells := make([]string, 0, MoveEventCount)
		for ev := moveEvent(0); ev < MoveEventCount; ev++ {
			cells = append(cells, fmt.Sprintf("%s=%d", moveEventNames[ev], row[ev]))
		}
		log.Printf("[world %s] move policy %-14s %s", ss.ServerID, pb.MoveState_name[state], strings.Join(cells, " "))
	}
}

func (e *playerEntity) moveStateBlocksTravel() bool {
	return config.MoveTransitions().BlocksTravel(e.State)
}
