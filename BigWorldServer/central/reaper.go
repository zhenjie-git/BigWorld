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
			delete(cs.servers, id)

			for pid, p := range cs.onlinePlayers {
				if p.GatewayId == id {
					delete(cs.onlinePlayers, pid)
					delete(cs.forceKickDeadlines, pid)
				}
			}
			for acct, st := range cs.accountStates {
				if st.GatewayId == id {
					delete(cs.accountStates, acct)
					delete(cs.loginDeadlines, acct)
				}
			}
		}
	}
}
