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

// ServerBase provides common lifecycle logic for all servers.
type ServerBase struct {
	ServerType ServerType
	ServerId   string
	ListenAddr string

	listener net.Listener
	quit     chan struct{}
	wg       sync.WaitGroup

	centralAddr string

	mu          sync.RWMutex
	centralConn *ConnWrapper
	conns       map[*ConnWrapper]struct{} // 活跃 peer 连接,优雅关闭时统一断开

	// OnMessage is called when a message arrives on a peer connection.
	// The handler may reply via conn.Send().
	OnMessage func(conn *ConnWrapper, msg Message)

	// OnCentralMessage is called for messages arriving from the central controller.
	OnCentralMessage func(msg Message)

	// OnDisconnect is called when a peer connection's read loop exits
	// (connection closed or read error), with the connection that closed.
	OnDisconnect func(conn *ConnWrapper)
}

// NewServerBase creates a ServerBase with initialised fields.
func NewServerBase(st ServerType, id string) *ServerBase {
	return &ServerBase{
		ServerType: st,
		ServerId:   id,
		quit:       make(chan struct{}),
		conns:      make(map[*ConnWrapper]struct{}),
	}
}

// Listen starts the TCP listener.
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

// AcceptLoop accepts peer connections and dispatches messages.
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

// ReadLoop reads messages from a single connection and dispatches them.
func (s *ServerBase) ReadLoop(cw *ConnWrapper) {
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[%s] ReadLoop panic recovered: %v", s.ServerId, r)
			return
		}
		if s.OnDisconnect != nil {
			s.OnDisconnect(cw)
		}
	}()
	for {
		msg, err := ReadMessage(cw.Conn)
		if err != nil {
			return // connection closed or error
		}
		if s.OnMessage != nil {
			s.OnMessage(cw, msg)
		}
	}
}

// ConnectToCentral connects to the central controller and starts the central
// message loop with automatic reconnect on disconnect.
func (s *ServerBase) ConnectToCentral(addr string) error {
	if err := s.DialCentral(addr); err != nil {
		return err
	}
	s.wg.Add(1)
	go s.CentralConnectionLoop(addr)
	return nil
}

// DialCentral dials central and stores the connection. Does not reconnect.
func (s *ServerBase) DialCentral(addr string) error {
	conn, err := net.DialTimeout("tcp", addr, 5*time.Second)
	if err != nil {
		return err
	}
	cw := NewConnWrapper(conn)
	s.mu.Lock()
	s.centralConn = cw
	s.mu.Unlock()
	log.Printf("[%s %s] connected to central at %s", s.ServerType, s.ServerId, addr)
	return nil
}

// CentralConnectionLoop reads messages from central. On disconnect it clears
// centralConn and reconnects with exponential backoff (1s..30s) until quit.
// After a successful reconnect it re-registers so central recognises this server again.
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
			// 断连后重连
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
		if s.OnCentralMessage != nil {
			func() {
				defer func() {
					if r := recover(); r != nil {
						log.Printf("[%s %s] central connection loop panic recovered: %v", s.ServerType, s.ServerId, r)
					}
				}()
				s.OnCentralMessage(msg)
			}()
		}
	}
}

// SendToCentral sends a message to the central controller.
func (s *ServerBase) SendToCentral(msg Message) error {
	s.mu.RLock()
	cw := s.centralConn
	s.mu.RUnlock()
	if cw == nil {
		return net.ErrClosed
	}
	return cw.Send(msg)
}

// SendToCentralMsg marshals m and sends it as a typed message to the central controller.
func (s *ServerBase) SendToCentralMsg(msgType MessageType, m proto.Message) error {
	data, err := MarshalHelper(m)
	if err != nil {
		return err
	}
	return s.SendToCentral(Message{Type: msgType, Data: data})
}

// Register sends a registration request to the central controller.
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

// StartHeartbeat sends heartbeats to the central controller on the given interval.
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

// Start runs the server: listener, optional central connection, and blocks until signal.
func (s *ServerBase) Start(centralAddr string) error {
	s.centralAddr = centralAddr

	// Start accepting peer connections.
	s.AcceptLoop()

	// Connect to central (skip for central itself).
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

	// Wait for shutdown: either an OS signal (Ctrl+C / SIGTERM) or a message-
	// triggered Stop() from a peer (graceful shutdown). Stop() closes s.quit,
	// which also unblocks the Wait here so the process actually exits.
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	select {
	case sig := <-sigCh:
		log.Printf("[%s %s] received signal %v, shutting down...", s.ServerType, s.ServerId, sig)
	case <-s.quit:
		log.Printf("[%s %s] shutdown requested, stopping...", s.ServerType, s.ServerId)
	}
	s.Stop()

	// Wait for all goroutines to finish (with timeout).
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

// Stop signals the server to shut down.
func (s *ServerBase) Stop() {
	// Close listener to unblock Accept.
	if s.listener != nil {
		s.listener.Close()
	}
	// Close central connection and active peer connections to unblock read loops.
	s.mu.Lock()
	if s.centralConn != nil {
		s.centralConn.Close()
		s.centralConn = nil
	}
	for cw := range s.conns {
		cw.Close()
	}
	s.mu.Unlock()
	// Signal quit.
	select {
	case <-s.quit:
	default:
		close(s.quit)
	}
}
