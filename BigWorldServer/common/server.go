package common

import (
	"context"
	"log"
	"net"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"google.golang.org/protobuf/proto"
)

type ServerBase struct {
	ServerType ServerType
	ServerId   string
	ListenAddr string

	Loop *EventLoop

	listener net.Listener
	quit     chan struct{}
	wg       sync.WaitGroup

	centralAddr string

	mu          sync.RWMutex
	centralConn *ConnWrapper
	conns       map[*ConnWrapper]struct{}

	routers map[EventSrc]*MessageRouter

	OnDisconnect func(conn *ConnWrapper)
}

func NewServerBase(st ServerType, id string) *ServerBase {
	s := &ServerBase{
		ServerType: st,
		ServerId:   id,
		quit:       make(chan struct{}),
		conns:      make(map[*ConnWrapper]struct{}),
		routers:    make(map[EventSrc]*MessageRouter),
	}
	s.Loop = NewEventLoop(4096)
	s.Loop.Handler = s.DefaultDispatch
	return s
}

func (s *ServerBase) Router(src EventSrc) *MessageRouter {
	r, ok := s.routers[src]
	if !ok {
		r = NewMessageRouter()
		s.routers[src] = r
	}
	return r
}

func (s *ServerBase) DefaultDispatch(ev Event) {
	switch ev.Kind {
	case EventMessage:
		if ev.Conn == nil {
			return
		}
		if r, ok := s.routers[SrcForServerType(ev.Conn.PeerType)]; ok {
			r.Dispatch(ev.Conn, ev.Msg)
		}
	case EventIdentify:
		if ev.Conn != nil {
			s.HandleIdentify(ev.Conn, ev.Msg)
		}
	case EventDisconnect:
		if s.OnDisconnect != nil {
			s.OnDisconnect(ev.Conn)
		}
	case EventDefer:
		if ev.Fn != nil {
			ev.Fn()
		}
	}
}

func (s *ServerBase) HandleIdentify(conn *ConnWrapper, msg Message) {
	var req IdentifyReq
	if err := proto.Unmarshal(msg.Data, &req); err != nil {
		log.Printf("[%s] bad IdentifyReq from %s: %v", s.ServerId, conn.RemoteAddr(), err)
		return
	}
	conn.PeerType = req.ServerType
	log.Printf("[%s] peer identified as %s from %s", s.ServerId, req.ServerType, conn.RemoteAddr())
}

func (s *ServerBase) Listen(addr string) error {
	var err error
	s.ListenAddr = addr
	s.listener, err = net.Listen("tcp", addr)
	if err != nil {
		return err
	}
	log.Printf("[%s %s] listening on %s", s.ServerType, s.ServerId, addr)
	return nil
}

func (s *ServerBase) AcceptLoop() {
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		for {
			conn, err := s.listener.Accept()
			if err != nil {
				select {
				case <-s.quit:
					return
				default:
					log.Printf("[%s] accept error: %v", s.ServerId, err)
					continue
				}
			}
			cw := NewConnWrapper(conn)
			s.mu.Lock()
			s.conns[cw] = struct{}{}
			s.mu.Unlock()
			s.wg.Add(1)
			go func() {
				defer s.wg.Done()
				defer cw.Close()
				defer func() {
					s.mu.Lock()
					delete(s.conns, cw)
					s.mu.Unlock()
				}()
				s.ReadLoop(cw)
			}()
		}
	}()
}

func (s *ServerBase) ReadLoop(cw *ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[%s] ReadLoop panic recovered: %v", s.ServerId, r)
		}
		s.Loop.Post(Event{Kind: EventDisconnect, Conn: cw})
	}()
	for {
		msg, err := ReadMessage(cw.Conn)
		if err != nil {
			return
		}
		if msg.Type == Srv2Srv_IdentifyReq {
			if !s.Loop.Post(Event{Kind: EventIdentify, Conn: cw, Msg: msg}) {
				cw.Close()
				return
			}
			continue
		}
		if !s.Loop.Post(Event{Kind: EventMessage, Conn: cw, Msg: msg}) {

			cw.Close()
			return
		}
	}
}

func (s *ServerBase) ConnectToCentral(addr string) error {
	if err := s.DialCentral(addr); err != nil {
		return err
	}
	s.wg.Add(1)
	go s.CentralConnectionLoop(addr)
	return nil
}

func (s *ServerBase) DialCentral(addr string) error {
	conn, err := net.DialTimeout("tcp", addr, 5*time.Second)
	if err != nil {
		return err
	}
	cw := NewConnWrapper(conn)
	cw.PeerType = ServerCentral
	s.mu.Lock()
	s.centralConn = cw
	s.mu.Unlock()
	log.Printf("[%s %s] connected to central at %s", s.ServerType, s.ServerId, addr)
	s.SendIdentify(cw)
	return nil
}

func (s *ServerBase) SendIdentify(cw *ConnWrapper) {
	if s.ServerType == ServerCentral {
		return
	}
	_ = SendMsg(cw, Srv2Srv_IdentifyReq, &IdentifyReq{ServerType: s.ServerType})
}

func (s *ServerBase) CentralConnectionLoop(addr string) {
	defer s.wg.Done()
	backoff := time.Second
	for {
		select {
		case <-s.quit:
			return
		default:
		}

		s.mu.RLock()
		cw := s.centralConn
		s.mu.RUnlock()

		if cw == nil {

			if err := s.DialCentral(addr); err != nil {
				log.Printf("[%s %s] reconnect to central failed: %v, retry in %v", s.ServerType, s.ServerId, err, backoff)
				select {
				case <-s.quit:
					return
				case <-time.After(backoff):
				}
				if backoff < 30*time.Second {
					backoff *= 2
				}
				continue
			}
			backoff = time.Second
			if err := s.Register(); err != nil {
				log.Printf("[%s %s] re-register after reconnect failed: %v", s.ServerType, s.ServerId, err)
			}
			continue
		}

		msg, err := ReadMessage(cw.Conn)
		if err != nil {
			log.Printf("[%s %s] central connection lost: %v", s.ServerType, s.ServerId, err)
			s.mu.Lock()
			if s.centralConn == cw {
				s.centralConn.Close()
				s.centralConn = nil
			}
			s.mu.Unlock()
			continue
		}
		s.Loop.Post(Event{Kind: EventMessage, Conn: cw, Msg: msg})
	}
}

func (s *ServerBase) SendToCentral(msg Message) error {
	s.mu.RLock()
	cw := s.centralConn
	s.mu.RUnlock()
	if cw == nil {
		return net.ErrClosed
	}
	return cw.Send(msg)
}

func (s *ServerBase) SendToCentralMsg(msgType MessageType, m proto.Message) error {
	data, err := MarshalHelper(m)
	if err != nil {
		return err
	}
	return s.SendToCentral(Message{Type: msgType, Data: data})
}

func (s *ServerBase) Register() error {
	req := RegisterReq{
		ServerType: s.ServerType,
		ServerId:   s.ServerId,
		ListenAddr: s.ListenAddr,
	}
	data, err := MarshalHelper(&req)
	if err != nil {
		return err
	}
	return s.SendToCentral(Message{Type: Srv2Ct_RegisterReq, Data: data})
}

func (s *ServerBase) StartHeartbeat(ctx context.Context, interval time.Duration) {
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-s.quit:
				return
			case <-ticker.C:
				data, _ := MarshalHelper(&HeartbeatReq{ServerId: s.ServerId})
				_ = s.SendToCentral(Message{Type: Srv2Ct_HeartbeatReq, Data: data})
			}
		}
	}()
}

func (s *ServerBase) Start(centralAddr string) error {
	s.centralAddr = centralAddr

	s.Loop.Start()

	s.AcceptLoop()

	if centralAddr != "" {
		if err := s.ConnectToCentral(centralAddr); err != nil {
			return err
		}
		if err := s.Register(); err != nil {
			return err
		}

		ctx, cancel := context.WithCancel(context.Background())
		defer cancel()
		s.StartHeartbeat(ctx, 10*time.Second)
	}

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	select {
	case sig := <-sigCh:
		log.Printf("[%s %s] received signal %v, shutting down...", s.ServerType, s.ServerId, sig)
	case <-s.quit:
		log.Printf("[%s %s] shutdown requested, stopping...", s.ServerType, s.ServerId)
	}
	s.Stop()

	done := make(chan struct{})
	go func() {
		s.wg.Wait()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		log.Printf("[%s %s] forced shutdown after timeout", s.ServerType, s.ServerId)
	}
	log.Printf("[%s %s] stopped", s.ServerType, s.ServerId)
	return nil
}

func (s *ServerBase) Stop() {
	select {
	case <-s.quit:
	default:
		close(s.quit)
	}

	if s.listener != nil {
		s.listener.Close()
	}

	s.mu.Lock()
	if s.centralConn != nil {
		s.centralConn.Close()
		s.centralConn = nil
	}
	for cw := range s.conns {
		cw.Close()
	}
	s.mu.Unlock()

	s.Loop.Stop()
}
