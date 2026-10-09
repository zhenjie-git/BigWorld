package main

import (
	"log"
	"time"

	"bigworld/common"
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
	for _, tr := range cs.transfers {
		if tr.FromWorldID == id || tr.ToWorldID == id {
			cs.FinishTransfer(tr, false, "world offline")
		}
	}

	if rec.info.ServerType == common.ServerWorld {
		entry := common.ServerEntry{ServerType: common.ServerWorld, ServerId: id, ListenAddr: rec.info.ListenAddr}
		notifyData, _ := common.MarshalHelper(&entry)
		for _, other := range cs.servers {
			if other.info.ServerType == common.ServerWorld && other.info.ServerId != id {
				other.conn.Send(common.Message{Type: common.Ct2Wd_WorldOfflineNotify, Data: notifyData})
			}
		}
	}

	for pid, p := range cs.onlinePlayers {
		if p.GatewayID != id {
			continue
		}
		delete(cs.onlinePlayers, pid)
		delete(cs.forceKickDeadlines, pid)
		if state, ok := cs.accountStates[p.Account]; ok && state.PlayerID == pid {
			delete(cs.accountStates, p.Account)
			delete(cs.loginDeadlines, p.Account)
		}
	}

	for acct, st := range cs.accountStates {
		if st.GatewayID == id {
			delete(cs.accountStates, acct)
			delete(cs.loginDeadlines, acct)
		}
	}
}
