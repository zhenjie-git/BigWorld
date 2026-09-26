package main

import (
	"fmt"
	"log"
	"net"
	"time"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

const (
	defaultPeerHeartbeatMs  = 5000
	defaultPeerTimeoutMs    = 15000
	defaultPeerBackoffMaxMs = 30000
	peerDialTimeout         = 3 * time.Second
	peerOutboxMax           = 64
	peerOutboxTTL           = 3 * time.Second
)

type worldPeer struct {
	ID        string
	Addr      string
	Conn      *common.ConnWrapper
	Inbound   bool
	Dialing   bool
	DialGen   int
	NextRetry time.Time
	Backoff   time.Duration
	LastRecv  time.Time
	LastPing  time.Time
}

type queuedPeerMsg struct {
	MsgType  common.MessageType
	Data     []byte
	ExpireAt time.Time
}

type worldPeerMgr struct {
	world      *worldServer
	selfID     string
	selfAddr   string
	heartbeat  time.Duration
	timeout    time.Duration
	backoffMax time.Duration
	peers      map[string]*worldPeer
	outbox     map[string][]queuedPeerMsg
}

func NewWorldPeerMgr(ss *worldServer, cfg common.ServerConfig) *worldPeerMgr {
	heartbeat := time.Duration(cfg.PeerHeartbeatMs) * time.Millisecond
	if heartbeat <= 0 {
		heartbeat = defaultPeerHeartbeatMs * time.Millisecond
	}
	timeout := time.Duration(cfg.PeerTimeoutMs) * time.Millisecond
	if timeout <= 0 {
		timeout = defaultPeerTimeoutMs * time.Millisecond
	}
	backoffMax := time.Duration(cfg.PeerBackoffMaxMs) * time.Millisecond
	if backoffMax <= 0 {
		backoffMax = defaultPeerBackoffMaxMs * time.Millisecond
	}
	return &worldPeerMgr{
		world:      ss,
		selfID:     ss.ServerID,
		selfAddr:   ss.ListenAddr,
		heartbeat:  heartbeat,
		timeout:    timeout,
		backoffMax: backoffMax,
		peers:      make(map[string]*worldPeer),
		outbox:     make(map[string][]queuedPeerMsg),
	}
}

func (m *worldPeerMgr) ShouldDial(peerID string) bool {
	return m.selfID < peerID
}

func (m *worldPeerMgr) AddRoute(worldID, addr string) {
	if worldID == "" || worldID == m.selfID || addr == "" {
		return
	}
	p := m.peers[worldID]
	if p == nil {
		p = &worldPeer{ID: worldID, Backoff: time.Second, NextRetry: time.Now()}
		m.peers[worldID] = p
	}
	if p.Addr != addr {
		p.Addr = addr
		if p.Conn != nil {
			_ = p.Conn.Close()
			p.Conn = nil
		}
		p.NextRetry = time.Now()
	}
	log.Printf("[world %s] peer route %s -> %s", m.selfID, worldID, addr)
	if m.ShouldDial(worldID) {
		m.TryDial(worldID)
	}
}

func (m *worldPeerMgr) RemoveRoute(worldID string) {
	p := m.peers[worldID]
	if p == nil {
		return
	}
	if p.Conn != nil {
		_ = p.Conn.Close()
		p.Conn = nil
	}
	delete(m.peers, worldID)
	delete(m.outbox, worldID)
	log.Printf("[world %s] peer route removed: %s", m.selfID, worldID)
}

func (m *worldPeerMgr) Ensure(worldID string) bool {
	p := m.peers[worldID]
	if p == nil {
		return false
	}
	if p.Conn != nil {
		return true
	}
	if p.Addr != "" && !p.Dialing {
		m.TryDial(worldID)
	}
	return false
}

func (m *worldPeerMgr) SendWorld(worldID string, msgType common.MessageType, msg proto.Message) error {
	p := m.peers[worldID]
	if p == nil {
		return fmt.Errorf("world peer %s not registered", worldID)
	}
	data, err := common.MarshalHelper(msg)
	if err != nil {
		return err
	}
	if p.Conn != nil {
		if err := p.Conn.Send(common.Message{Type: msgType, Data: data}); err != nil {
			m.ClosePeerConn(p, "send failed")
			return err
		}
		return nil
	}
	if p.Addr == "" {
		return fmt.Errorf("world peer %s has no address", worldID)
	}
	if !p.Dialing {
		m.TryDial(worldID)
	}
	m.Enqueue(worldID, msgType, data)
	return nil
}

func (m *worldPeerMgr) Enqueue(worldID string, msgType common.MessageType, data []byte) {
	q := m.outbox[worldID]
	if len(q) >= peerOutboxMax {
		q = q[1:]
	}
	q = append(q, queuedPeerMsg{MsgType: msgType, Data: data, ExpireAt: time.Now().Add(peerOutboxTTL)})
	m.outbox[worldID] = q
}

func (m *worldPeerMgr) FlushOutbox(p *worldPeer) {
	q := m.outbox[p.ID]
	if len(q) == 0 || p.Conn == nil {
		return
	}
	now := time.Now()
	for _, item := range q {
		if now.After(item.ExpireAt) {
			continue
		}
		if err := p.Conn.Send(common.Message{Type: item.MsgType, Data: item.Data}); err != nil {
			m.ClosePeerConn(p, "flush failed")
			return
		}
	}
	delete(m.outbox, p.ID)
}

func (m *worldPeerMgr) TryDial(worldID string) {
	p := m.peers[worldID]
	if p == nil || p.Conn != nil || p.Dialing || p.Addr == "" {
		return
	}
	if !m.ShouldDial(worldID) {
		return
	}
	if !p.NextRetry.IsZero() && time.Now().Before(p.NextRetry) {
		return
	}
	p.DialGen++
	gen := p.DialGen
	p.Dialing = true
	addr := p.Addr
	go m.DialRoutine(worldID, addr, gen)
}

func (m *worldPeerMgr) DialRoutine(worldID, addr string, gen int) {
	conn, err := net.DialTimeout("tcp", addr, peerDialTimeout)
	if err != nil {
		m.PostDialFailed(worldID, gen, err)
		return
	}
	cw := common.NewConnWrapper(conn)
	cw.PeerType = common.ServerWorld
	hello := &common.HelloReq{
		ServerType:      common.ServerWorld,
		ServerId:        m.selfID,
		ListenAddr:      m.selfAddr,
		Secret:          common.Config.ServerSecret,
		ProtocolVersion: common.HelloProtocolVersion,
	}
	if err := common.SendMsg(cw, common.Srv2Srv_HelloReq, hello); err != nil {
		_ = cw.Close()
		m.PostDialFailed(worldID, gen, err)
		return
	}
	_ = conn.SetReadDeadline(time.Now().Add(peerDialTimeout))
	msg, err := common.ReadMessage(conn)
	if err != nil {
		_ = cw.Close()
		m.PostDialFailed(worldID, gen, err)
		return
	}
	_ = conn.SetReadDeadline(time.Time{})
	if msg.Type != common.Srv2Srv_HelloRsp {
		_ = cw.Close()
		m.PostDialFailed(worldID, gen, fmt.Errorf("unexpected handshake message %d", msg.Type))
		return
	}
	var rsp common.HelloRsp
	if err := proto.Unmarshal(msg.Data, &rsp); err != nil {
		_ = cw.Close()
		m.PostDialFailed(worldID, gen, err)
		return
	}
	if !rsp.Success {
		_ = cw.Close()
		m.PostDialFailed(worldID, gen, fmt.Errorf("hello rejected: %s", rsp.Message))
		return
	}

	ok := m.world.Loop.Post(common.Event{Kind: common.EventDefer, Fn: func() {
		p := m.peers[worldID]
		if p == nil || p.DialGen != gen || p.Addr != addr {
			if p != nil && p.DialGen == gen {
				p.Dialing = false
			}
			_ = cw.Close()
			return
		}
		if p.Conn != nil {
			p.Dialing = false
			_ = cw.Close()
			return
		}
		now := time.Now()
		p.Conn = cw
		p.Inbound = false
		p.Dialing = false
		p.Backoff = time.Second
		p.NextRetry = time.Time{}
		p.LastRecv = now
		p.LastPing = now
		log.Printf("[world %s] peer connected: %s at %s", m.selfID, worldID, addr)
		m.world.TrackConn(cw)
		go func() {
			m.world.ReadLoop(cw)
			m.world.UntrackConn(cw)
			_ = cw.Close()
		}()
		m.FlushOutbox(p)
	}})
	if !ok {
		_ = cw.Close()
	}
}

func (m *worldPeerMgr) PostDialFailed(worldID string, gen int, err error) {
	m.world.Loop.Defer(func() {
		p := m.peers[worldID]
		if p == nil || p.DialGen != gen {
			return
		}
		p.Dialing = false
		if p.Backoff <= 0 {
			p.Backoff = time.Second
		} else {
			p.Backoff *= 2
			if p.Backoff > m.backoffMax {
				p.Backoff = m.backoffMax
			}
		}
		p.NextRetry = time.Now().Add(p.Backoff)
		log.Printf("[world %s] peer %s connect failed: %v, retry in %v", m.selfID, worldID, err, p.Backoff)
	})
}

func (m *worldPeerMgr) HandlePeerHello(conn *common.ConnWrapper, req *common.HelloReq) *common.HelloRsp {
	if req.ServerType != common.ServerWorld {
		return &common.HelloRsp{Success: false, Message: "unsupported peer type"}
	}
	if req.ServerId == "" || req.ServerId == m.selfID || req.ListenAddr == "" {
		return &common.HelloRsp{Success: false, Message: "invalid world id"}
	}
	p := m.peers[req.ServerId]
	if p == nil {
		return &common.HelloRsp{Success: false, Message: "unknown world"}
	}
	if m.ShouldDial(req.ServerId) {
		return &common.HelloRsp{Success: false, Message: "duplicate dial direction"}
	}
	if p.Conn != nil {
		return &common.HelloRsp{Success: false, Message: "already connected"}
	}
	p.Addr = req.ListenAddr
	p.Conn = conn
	p.Inbound = true
	p.Dialing = false
	p.NextRetry = time.Time{}
	p.Backoff = time.Second
	p.LastRecv = time.Now()
	p.LastPing = time.Now()
	log.Printf("[world %s] peer accepted: %s at %s", m.selfID, req.ServerId, req.ListenAddr)
	m.FlushOutbox(p)
	return &common.HelloRsp{Success: true, Message: "peer accepted", Registered: true}
}

func (m *worldPeerMgr) HandleDisconnect(conn *common.ConnWrapper) {
	for _, p := range m.peers {
		if p.Conn != conn {
			continue
		}
		log.Printf("[world %s] peer disconnected: %s", m.selfID, p.ID)
		p.Conn = nil
		p.Inbound = false
		if m.ShouldDial(p.ID) {
			p.NextRetry = time.Now().Add(p.Backoff)
		}
		return
	}
}

func (m *worldPeerMgr) ClosePeerConn(p *worldPeer, reason string) {
	if p == nil || p.Conn == nil {
		return
	}
	log.Printf("[world %s] peer %s closed: %s", m.selfID, p.ID, reason)
	_ = p.Conn.Close()
	p.Conn = nil
	p.Inbound = false
	if m.ShouldDial(p.ID) {
		p.NextRetry = time.Now().Add(p.Backoff)
	}
}

func (m *worldPeerMgr) PeerByConn(conn *common.ConnWrapper) *worldPeer {
	for _, p := range m.peers {
		if p.Conn == conn {
			return p
		}
	}
	return nil
}

func (m *worldPeerMgr) HandleHeartbeatReq(conn *common.ConnWrapper, req *common.HeartbeatReq) {
	p := m.PeerByConn(conn)
	if p == nil {
		return
	}
	p.LastRecv = time.Now()
	rsp := common.HeartbeatRsp{Success: true}
	_ = common.SendMsg(conn, common.Wd2Wd_HeartbeatRsp, &rsp)
}

func (m *worldPeerMgr) HandleHeartbeatRsp(conn *common.ConnWrapper, rsp *common.HeartbeatRsp) {
	p := m.PeerByConn(conn)
	if p == nil {
		return
	}
	p.LastRecv = time.Now()
}

func (m *worldPeerMgr) OnTick(now time.Time) {
	for id, p := range m.peers {
		if p.Conn == nil {
			m.TryDial(id)
			continue
		}
		if now.Sub(p.LastPing) >= m.heartbeat {
			p.LastPing = now
			if err := common.SendMsg(p.Conn, common.Wd2Wd_HeartbeatReq, &common.HeartbeatReq{ServerId: m.selfID}); err != nil {
				m.ClosePeerConn(p, "heartbeat send failed")
				continue
			}
		}
		if !p.LastRecv.IsZero() && now.Sub(p.LastRecv) > m.timeout {
			m.ClosePeerConn(p, "heartbeat timeout")
		}
	}
	for id, q := range m.outbox {
		alive := q[:0]
		for _, item := range q {
			if now.Before(item.ExpireAt) {
				alive = append(alive, item)
			}
		}
		if len(alive) == 0 {
			delete(m.outbox, id)
		} else {
			m.outbox[id] = alive
		}
	}
}
