package common

import (
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"sync"

	"google.golang.org/protobuf/proto"
)

// ConnWrapper wraps net.Conn with a thread-safe Send method.
type ConnWrapper struct {
	net.Conn
	mu sync.Mutex
}

// NewConnWrapper creates a ConnWrapper from a raw connection.
func NewConnWrapper(conn net.Conn) *ConnWrapper {
	return &ConnWrapper{Conn: conn}
}

// Send serialises and writes a message in a thread-safe manner.
func (cw *ConnWrapper) Send(msg Message) error {
	cw.mu.Lock()
	defer cw.mu.Unlock()
	return SendMessage(cw.Conn, msg)
}

// CloseAfterSend closes the connection, waiting for pending data to be sent.
// Uses SO_LINGER to tell the kernel to block until the send buffer is flushed,
// preventing data loss that would occur with an immediate Close() after Send().
func (cw *ConnWrapper) CloseAfterSend() error {
	if tcpConn, ok := cw.Conn.(*net.TCPConn); ok {
		tcpConn.SetLinger(5) // wait up to 5 seconds for data to be sent
	}
	return cw.Conn.Close()
}

// Message is the wire envelope: a type tag plus an opaque protobuf payload.
type Message struct {
	Type MessageType
	Data []byte
}

// Encode serialises a Message to a length-prefixed binary frame:
//
//	[4B big-endian length][2B big-endian MessageType][N bytes payload]
//
// where length = 2 + len(Data).
func Encode(msg Message) ([]byte, error) {
	buf := make([]byte, 4+2+len(msg.Data))
	binary.BigEndian.PutUint32(buf[:4], uint32(2+len(msg.Data)))
	binary.BigEndian.PutUint16(buf[4:6], uint16(msg.Type))
	copy(buf[6:], msg.Data)
	return buf, nil
}

// SendMessage encodes and writes a message to a connection.
func SendMessage(conn net.Conn, msg Message) error {
	data, err := Encode(msg)
	if err != nil {
		return err
	}
	_, err = conn.Write(data)
	return err
}

// ReadMessage reads one length-prefixed binary-framed message from a connection.
func ReadMessage(conn net.Conn) (Message, error) {
	header := make([]byte, 4)
	if _, err := io.ReadFull(conn, header); err != nil {
		return Message{}, fmt.Errorf("read header: %w", err)
	}
	length := binary.BigEndian.Uint32(header)
	if length < 2 { // must at least carry the 2-byte type tag
		return Message{}, fmt.Errorf("invalid message length: %d", length)
	}
	if length > 1024*1024 { // 1 MB sanity limit
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

// MarshalHelper marshals a protobuf message into bytes.
func MarshalHelper(m proto.Message) ([]byte, error) {
	return proto.Marshal(m)
}

// SendMsg marshals m and sends it as a typed message on conn.
func SendMsg(conn *ConnWrapper, msgType MessageType, m proto.Message) error {
	data, err := MarshalHelper(m)
	if err != nil {
		return err
	}
	return conn.Send(Message{Type: msgType, Data: data})
}
