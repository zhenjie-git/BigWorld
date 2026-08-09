package common

import (
	"log"

	"google.golang.org/protobuf/proto"
)

// Handler is a callback for a specific message type. req is a pointer to the
// unmarshalled proto message (proto messages are not safe to copy by value).
type Handler[T any, PT interface{ *T; proto.Message }] func(conn *ConnWrapper, req PT)

// MessageRouter dispatches incoming messages to registered handlers by MessageType.
// Each server creates one per message source (client, central, world, etc.).
type MessageRouter struct {
	handlers map[MessageType]func(conn *ConnWrapper, data []byte)
}

// NewMessageRouter creates an empty MessageRouter.
func NewMessageRouter() *MessageRouter {
	return &MessageRouter{handlers: make(map[MessageType]func(*ConnWrapper, []byte))}
}

// Register binds a message type to a typed handler.
// The router handles protobuf unmarshalling automatically. T is the concrete
// message type; PT is a pointer to T satisfying proto.Message.
func Register[T any, PT interface{ *T; proto.Message }](r *MessageRouter, msgType MessageType, h Handler[T, PT]) {
	r.handlers[msgType] = func(conn *ConnWrapper, data []byte) {
		var v T
		if err := proto.Unmarshal(data, PT(&v)); err != nil {
			log.Printf("bad message type %d: %v", msgType, err)
			return
		}
		h(conn, PT(&v))
	}
}

// Dispatch looks up and calls the handler for a message.
func (r *MessageRouter) Dispatch(conn *ConnWrapper, msg Message) {
	h, ok := r.handlers[msg.Type]
	if !ok {
		return
	}
	h(conn, msg.Data)
}
