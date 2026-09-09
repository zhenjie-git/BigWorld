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

type ConnWrapper struct {
	net.Conn
	sendCh    chan []byte
	closed    chan struct{}
	closeOnce sync.Once

	PeerType ServerType
}

func NewConnWrapper(conn net.Conn) *ConnWrapper {
	cw := &ConnWrapper{
		Conn:   conn,
		sendCh: make(chan []byte, sendQueueSize),
		closed: make(chan struct{}),
	}
	go cw.WriteLoop()
	return cw
}

func (cw *ConnWrapper) Send(msg Message) error {
	data, err := Encode(msg)
	if err != nil {
		return err
	}
	select {
	case <-cw.closed:
		return net.ErrClosed
	default:
	}
	select {
	case cw.sendCh <- data:
		return nil
	case <-cw.closed:
		return net.ErrClosed
	default:
		log.Printf("[conn] send queue full, closing slow connection %v", cw.RemoteAddr())
		cw.Close()
		return fmt.Errorf("send queue full")
	}
}

func (cw *ConnWrapper) WriteLoop() {
	defer cw.Conn.Close()
	for {
		select {
		case data := <-cw.sendCh:
			if _, err := cw.Conn.Write(data); err != nil {
				return
			}
		case <-cw.closed:
			for {
				select {
				case data := <-cw.sendCh:
					if _, err := cw.Conn.Write(data); err != nil {
						return
					}
				default:
					return
				}
			}
		}
	}
}

func (cw *ConnWrapper) Close() error {
	cw.closeOnce.Do(func() {
		close(cw.closed)
	})
	return nil
}

func (cw *ConnWrapper) CloseAfterSend() error {
	return cw.Close()
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
