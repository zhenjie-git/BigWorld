package main

import (
	"log"
	"time"

	"bigworld/common"
)

const shutdownAckTimeout = 10 * time.Second

var shutdownOrder = []common.ServerType{
	common.ServerGateway,
	common.ServerLogin,
	common.ServerWorld,
	common.ServerDbProxy,
}

func (cs *centralServer) IsShuttingDown() bool {
	return cs.shuttingDown
}

func (cs *centralServer) HandleShutdown(conn *common.ConnWrapper, req *common.ShutdownReq) {
	log.Printf("[central] shutdown requested: %s", req.Reason)
	common.SendMsg(conn, common.Ct2Srv_ShutdownRsp, &common.ShutdownRsp{
		Success: true,
		Message: "shutdown initiated",
	})

	if cs.shuttingDown {
		return
	}
	cs.shuttingDown = true
	cs.BeginShutdownStage(0)
}

func (cs *centralServer) BeginShutdownStage(stage int) {
	cs.shutdownStage = stage
	for id := range cs.shutdownPending {
		delete(cs.shutdownPending, id)
	}
	if stage >= len(shutdownOrder) {
		log.Printf("[central] all servers stopped, shutting down central")
		go cs.Stop()
		return
	}

	st := shutdownOrder[stage]
	targets := 0
	for _, rec := range cs.servers {
		if rec.info.ServerType == st {
			log.Printf("[central] sending shutdown to %s (%s)", rec.info.ServerId, rec.info.ServerType)
			common.SendMsg(rec.conn, common.Ct2Srv_ShutdownNotify, &common.ShutdownNotify{Reason: "graceful shutdown"})
			cs.shutdownPending[rec.info.ServerId] = true
			targets++
		}
	}
	if targets == 0 {
		log.Printf("[central] no %v registered, skipping", st)
		cs.BeginShutdownStage(stage + 1)
		return
	}
	cs.shutdownDeadline = time.Now().Add(shutdownAckTimeout)
}

func (cs *centralServer) AdvanceShutdownStage(timedOut int) {
	if timedOut > 0 {
		log.Printf("[central] shutdown ack timeout for %d server(s) in stage %d, proceeding", timedOut, cs.shutdownStage)
	}
	cs.BeginShutdownStage(cs.shutdownStage + 1)
}

func (cs *centralServer) HandleShutdownAck(_ *common.ConnWrapper, ack *common.ShutdownAck) {
	if _, waiting := cs.shutdownPending[ack.ServerId]; !waiting {
		return
	}
	delete(cs.shutdownPending, ack.ServerId)
	log.Printf("[central] %s acknowledged shutdown", ack.ServerId)
	if cs.shuttingDown && len(cs.shutdownPending) == 0 {
		cs.AdvanceShutdownStage(0)
	}
}
