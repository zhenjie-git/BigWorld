package main

import (
	"log"
	"time"

	"bigworld/common"
)

const shutdownAckTimeout = 10 * time.Second

// handleShutdown is triggered by the shutdown tool (shutdown/main.go). It replies
// immediately that shutdown has been initiated, then orchestrates the graceful
// shutdown in a background goroutine so the reply is not blocked on server drains.
func (cs *centralServer) HandleShutdown(conn *common.ConnWrapper, req *common.ShutdownReq) {
	log.Printf("[central] shutdown requested: %s", req.Reason)
	common.SendMsg(conn, common.Ct2Srv_ShutdownRsp, &common.ShutdownRsp{
		Success: true,
		Message: "shutdown initiated",
	})
	go cs.RunShutdown(req.Reason)
}

// runShutdown stops servers in dependency-safe order: gateways first (cut the
// client entry point), then login, then world (which must finish flushing
// players to dbproxy BEFORE dbproxy stops), then dbproxy, and finally central
// itself. Each step waits for the target's ShutdownAck (with a timeout), so the
// sequence is confirmed by acks rather than guessed from timing.
// isShuttingDown reports whether a graceful shutdown is in progress, so login
// orchestration can reject new logins instead of half-completing them.
func (cs *centralServer) IsShuttingDown() bool {
	cs.mu.RLock()
	defer cs.mu.RUnlock()
	return cs.shuttingDown
}

func (cs *centralServer) RunShutdown(reason string) {
	cs.mu.Lock()
	cs.shuttingDown = true
	cs.mu.Unlock()

	for _, st := range []common.ServerType{
		common.ServerGateway,
		common.ServerLogin,
		common.ServerWorld,
		common.ServerDbProxy,
	} {
		cs.ShutdownServerType(st, reason)
	}

	log.Printf("[central] all servers stopped, shutting down central")
	cs.Stop()
}

// shutdownServerType sends Ct2Srv_ShutdownNotify to every registered server of
// the given type and waits for each to acknowledge (mirrors the inline
// cs.servers iteration pattern in registry.go).
func (cs *centralServer) ShutdownServerType(st common.ServerType, reason string) {
	cs.mu.RLock()
	var targets []*serverRecord
	for _, rec := range cs.servers {
		if rec.info.ServerType == st {
			targets = append(targets, rec)
		}
	}
	cs.mu.RUnlock()

	if len(targets) == 0 {
		log.Printf("[central] no %v registered, skipping", st)
		return
	}
	for _, rec := range targets {
		cs.ShutdownOne(rec, reason)
	}
}

func (cs *centralServer) ShutdownOne(rec *serverRecord, reason string) {
	log.Printf("[central] sending shutdown to %s (%s)", rec.info.ServerId, rec.info.ServerType)
	common.SendMsg(rec.conn, common.Ct2Srv_ShutdownNotify, &common.ShutdownNotify{Reason: reason})

	ch := make(chan struct{})
	cs.mu.Lock()
	cs.shutdownAcks[rec.info.ServerId] = ch
	cs.mu.Unlock()

	select {
	case <-ch:
		log.Printf("[central] %s acknowledged shutdown", rec.info.ServerId)
	case <-time.After(shutdownAckTimeout):
		log.Printf("[central] shutdown ack timeout for %s, proceeding", rec.info.ServerId)
	}

	// Clean up the ack slot if no ack arrived (a late ack then finds nothing).
	cs.mu.Lock()
	if _, ok := cs.shutdownAcks[rec.info.ServerId]; ok {
		delete(cs.shutdownAcks, rec.info.ServerId)
	}
	cs.mu.Unlock()
}

// handleShutdownAck records a server's acknowledgement. The channel is deleted
// on close so a duplicate ack cannot double-close (which would panic).
func (cs *centralServer) HandleShutdownAck(_ *common.ConnWrapper, ack *common.ShutdownAck) {
	cs.mu.Lock()
	if ch, ok := cs.shutdownAcks[ack.ServerId]; ok {
		delete(cs.shutdownAcks, ack.ServerId)
		close(ch)
	}
	cs.mu.Unlock()
}
