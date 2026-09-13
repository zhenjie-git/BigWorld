package main

import (
	"log"
	"time"
)

func (cs *centralServer) ReapStale(timeout time.Duration) {
	now := time.Now().Unix()
	for id, rec := range cs.servers {
		if now-rec.lastHB > int64(timeout.Seconds()) {
			log.Printf("[central] removing stale server: %s", id)
			cs.CleanupServer(id)
		}
	}
}

func (cs *centralServer) CleanupServer(id string) {
	rec, exists := cs.servers[id]
	if !exists {
		return
	}
	delete(cs.servers, id)
	if rec.conn != nil {
		delete(cs.connToID, rec.conn)
		_ = rec.conn.Close()
	}

	for pid, p := range cs.onlinePlayers {
		if p.GatewayId != id {
			continue
		}
		delete(cs.onlinePlayers, pid)
		delete(cs.forceKickDeadlines, pid)
		if state, ok := cs.accountStates[p.Account]; ok && state.PlayerId == pid {
			delete(cs.accountStates, p.Account)
			delete(cs.loginDeadlines, p.Account)
		}
	}

	for acct, st := range cs.accountStates {
		if st.GatewayId == id {
			delete(cs.accountStates, acct)
			delete(cs.loginDeadlines, acct)
		}
	}
}
