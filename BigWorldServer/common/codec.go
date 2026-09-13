package common

import (
	"encoding/binary"
	"fmt"
	"io"
	"log"
	"net"
	"sync"

	"google.golang.org/protobuf/proto"
)

const sendQueueSize = 1024

type sendItem struct {
	data       []byte
	closeAfter bool
}

type ConnWrapper struct {
	net.Conn
	sendCh    chan sendItem
	closed    chan struct{}
	closeOnce sync.Once
	sendMu    sync.Mutex
	closing   bool

	helloMu   sync.Mutex
	helloDone bool

	PeerType ServerType
}

func NewConnWrapper(conn net.Conn) *ConnWrapper {
	cw := &ConnWrapper{
		Conn:   conn,
		sendCh: make(chan sendItem, sendQueueSize),
		closed: make(chan struct{}),
	}
	go cw.WriteLoop()
	return cw
}

func (cw *ConnWrapper) MarkHelloDone() bool {
	cw.helloMu.Lock()
	defer cw.helloMu.Unlock()
	if cw.helloDone {
		return false
	}
	cw.helloDone = true
	return true
}
func (cw *ConnWrapper) Send(msg Message) error {
	data, err := Encode(msg)
	if err != nil {
		return err
	}
	cw.sendMu.Lock()
	if cw.closing {
		cw.sendMu.Unlock()
		return net.ErrClosed
	}
	select {
	case <-cw.closed:
		cw.sendMu.Unlock()
		return net.ErrClosed
	default:
	}
	select {
	case cw.sendCh <- sendItem{data: data}:
		cw.sendMu.Unlock()
		return nil
	case <-cw.closed:
		cw.sendMu.Unlock()
		return net.ErrClosed
	default:
		cw.sendMu.Unlock()
		log.Printf("[conn] send queue full, closing slow connection %v", cw.RemoteAddr())
		cw.Close()
		return fmt.Errorf("send queue full")
	}
}

func (cw *ConnWrapper) WriteLoop() {
	defer func() {
		cw.markClosed()
		_ = cw.Conn.Close()
	}()
	for {
		select {
		case item := <-cw.sendCh:
			if item.closeAfter {
				return
			}
			if _, err := cw.Conn.Write(item.data); err != nil {
				return
			}
		case <-cw.closed:
			return
		}
	}
}

func (cw *ConnWrapper) markClosed() {
	cw.closeOnce.Do(func() {
		close(cw.closed)
	})
}

func (cw *ConnWrapper) Close() error {
	cw.markClosed()
	return cw.Conn.Close()
}

func (cw *ConnWrapper) CloseAfterSend() error {
	cw.sendMu.Lock()
	if cw.closing {
		cw.sendMu.Unlock()
		return nil
	}
	cw.closing = true
	select {
	case cw.sendCh <- sendItem{closeAfter: true}:
		cw.sendMu.Unlock()
		return nil
	case <-cw.closed:
		cw.sendMu.Unlock()
		return net.ErrClosed
	default:
		cw.sendMu.Unlock()
		return cw.Close()
	}
}

type Message struct {
	Type MessageType
	Data []byte
}

func Encode(msg Message) ([]byte, error) {
	buf := make([]byte, 4+2+len(msg.Data))
	binary.BigEndian.PutUint32(buf[:4], uint32(2+len(msg.Data)))
	binary.BigEndian.PutUint16(buf[4:6], uint16(msg.Type))
	copy(buf[6:], msg.Data)
	return buf, nil
}

func SendMessage(conn net.Conn, msg Message) error {
	data, err := Encode(msg)
	if err != nil {
		return err
	}
	_, err = conn.Write(data)
	return err
}

func ReadMessage(conn net.Conn) (Message, error) {
	header := make([]byte, 4)
	if _, err := io.ReadFull(conn, header); err != nil {
		return Message{}, fmt.Errorf("read header: %w", err)
	}
	length := binary.BigEndian.Uint32(header)
	if length < 2 {
		return Message{}, fmt.Errorf("invalid message length: %d", length)
	}
	if length > 1024*1024 {
		return Message{}, fmt.Errorf("message too large: %d", length)
	}
	body := make([]byte, length)
	if _, err := io.ReadFull(conn, body); err != nil {
		return Message{}, fmt.Errorf("read body: %w", err)
	}
	return Message{
		Type: MessageType(binary.BigEndian.Uint16(body[:2])),
		Data: body[2:],
	}, nil
}

func MarshalHelper(m proto.Message) ([]byte, error) {
	return proto.Marshal(m)
}

func SendMsg(conn *ConnWrapper, msgType MessageType, m proto.Message) error {
	data, err := MarshalHelper(m)
	if err != nil {
		return err
	}
	return conn.Send(Message{Type: msgType, Data: data})
}
