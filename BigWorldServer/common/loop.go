package common

import (
	"log"
	"runtime/debug"
	"sync"
	"time"
)

type EventKind int

const (
	EventMessage EventKind = iota

	EventDisconnect

	EventDefer

	EventIdentify
)

type EventSrc int

const (
	SrcClient EventSrc = iota

	SrcGateway
	SrcLogin
	SrcWorld
	SrcDbProxy

	SrcCentral
)

var ServerSrcs = []EventSrc{SrcGateway, SrcLogin, SrcWorld, SrcDbProxy}

func SrcForServerType(t ServerType) EventSrc {
	switch t {
	case ServerCentral:
		return SrcCentral
	case ServerGateway:
		return SrcGateway
	case ServerLogin:
		return SrcLogin
	case ServerWorld:
		return SrcWorld
	case ServerDbProxy:
		return SrcDbProxy
	default:
		return SrcClient
	}
}

type Event struct {
	Kind EventKind
	Conn *ConnWrapper
	Msg  Message
	Fn   func()
}

type EventLoop struct {
	Handler   func(ev Event)
	OnTick    func()
	TickEvery time.Duration

	queue    chan Event
	quit     chan struct{}
	wg       sync.WaitGroup
	stopOnce sync.Once
}

func NewEventLoop(queueSize int) *EventLoop {
	if queueSize <= 0 {
		queueSize = 4096
	}
	return &EventLoop{
		queue: make(chan Event, queueSize),
		quit:  make(chan struct{}),
	}
}

func (l *EventLoop) Post(ev Event) bool {
	select {
	case l.queue <- ev:
		return true
	default:
		log.Printf("[loop] event queue full, dropping kind=%d", ev.Kind)
		return false
	}
}

func (l *EventLoop) Defer(fn func()) bool {
	return l.Post(Event{Kind: EventDefer, Fn: fn})
}

func (l *EventLoop) Start() {
	l.wg.Add(1)
	go l.Run()
}

func (l *EventLoop) Run() {
	defer l.wg.Done()

	var tickCh <-chan time.Time
	if l.TickEvery > 0 && l.OnTick != nil {
		ticker := time.NewTicker(l.TickEvery)
		defer ticker.Stop()
		tickCh = ticker.C
	}

	for {
		select {
		case <-l.quit:
			l.Drain()
			return
		case ev := <-l.queue:
			l.Invoke(ev)
		case <-tickCh:
			l.Drain()
			l.InvokeTick()
		}
	}
}

func (l *EventLoop) Drain() {
	for {
		select {
		case ev := <-l.queue:
			l.Invoke(ev)
		default:
			return
		}
	}
}

func (l *EventLoop) Invoke(ev Event) {
	if l.Handler == nil {
		return
	}
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[loop] handler panic: %v\n%s", r, debug.Stack())
		}
	}()
	l.Handler(ev)
}

func (l *EventLoop) InvokeTick() {
	if l.OnTick == nil {
		return
	}
	defer func() {
		if r := recover(); r != nil {
			log.Printf("[loop] tick panic: %v\n%s", r, debug.Stack())
		}
	}()
	l.OnTick()
}

func (l *EventLoop) Stop() {
	l.stopOnce.Do(func() {
		close(l.quit)
	})
	l.wg.Wait()
}
