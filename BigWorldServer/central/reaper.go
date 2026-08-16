package main

import (
	"log"
	"time"
)

func (cs *centralServer) ReapStale(timeout time.Duration) {
	cs.mu.Lock()
	defer cs.mu.Unlock()
	now := time.Now().Unix()
	for id, rec := range cs.servers {
		if now-rec.lastHB > int64(timeout.Seconds()) {
			log.Printf("[central] removing stale server: %s", id)
			delete(cs.servers, id)

			// 清理该 gateway 名下的在线玩家与账号状态,避免崩溃后孤儿残留。
			// WaitingKick/LoggingIn 多半已被 forceKickTimers(10s)/loginTimers(30s)兜底,
			// 这里兜底清残留(含未触发的 timer),AccountOnline 的孤儿则只能靠这里。
			for pid, p := range cs.onlinePlayers {
				if p.GatewayId == id {
					delete(cs.onlinePlayers, pid)
					if timer, ok := cs.forceKickTimers[pid]; ok {
						timer.Stop()
						delete(cs.forceKickTimers, pid)
					}
				}
			}
			for acct, st := range cs.accountStates {
				if st.GatewayId == id {
					delete(cs.accountStates, acct)
					if timer, ok := cs.loginTimers[acct]; ok {
						timer.Stop()
						delete(cs.loginTimers, acct)
					}
				}
			}
		}
	}
}
