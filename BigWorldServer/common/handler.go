package common

import (
	"log"

	"google.golang.org/protobuf/proto"
)

type Handler[T any, PT interface {
	*T
	proto.Message
}] func(conn *ConnWrapper, req PT)

type MessageRouter struct {
	handlers map[MessageType]func(conn *ConnWrapper, data []byte)
}

func NewMessageRouter() *MessageRouter {
	return &MessageRouter{handlers: make(map[MessageType]func(*ConnWrapper, []byte))}
}

func Register[T any, PT interface {
	*T
	proto.Message
}](r *MessageRouter, msgType MessageType, h Handler[T, PT]) {
	r.handlers[msgType] = func(conn *ConnWrapper, data []byte) {
		var v T
		if err := proto.Unmarshal(data, PT(&v)); err != nil {
			log.Printf("bad message type %d: %v", msgType, err)
			return
		}
		h(conn, PT(&v))
	}
}

func (r *MessageRouter) Dispatch(conn *ConnWrapper, msg Message) {
	h, ok := r.handlers[msg.Type]
	if !ok {
		return
	}
	h(conn, msg.Data)
}
