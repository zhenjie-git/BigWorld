// shutdown is a tiny CLI that tells central to start a graceful shutdown.
// It reuses the common codec, so the protocol framing lives in exactly one
// place (common) rather than being hand-encoded in stop.bat.
package main

import (
	"log"
	"net"
	"time"

	"bigworld/common"

	"google.golang.org/protobuf/proto"
)

func main() {
	cfg := common.MustLoadServerConfig("common/config.json", "central")
	if cfg.ListenAddr == "" {
		log.Fatal("central listen_addr is empty in common/config.json")
	}

	conn, err := net.DialTimeout("tcp", cfg.ListenAddr, 5*time.Second)
	if err != nil {
		log.Fatalf("cannot reach central at %s (is it running?): %v", cfg.ListenAddr, err)
	}
	defer conn.Close()
	cw := common.NewConnWrapper(conn)

	req := &common.ShutdownReq{Reason: "user initiated (stop.bat / shutdown tool)"}
	data, err := common.MarshalHelper(req)
	if err != nil {
		log.Fatalf("failed to marshal ShutdownReq: %v", err)
	}
	if err := cw.Send(common.Message{Type: common.Srv2Ct_ShutdownReq, Data: data}); err != nil {
		log.Fatalf("failed to send ShutdownReq: %v", err)
	}

	// Read the one-shot confirmation. Central replies before it starts draining.
	_ = conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	msg, err := common.ReadMessage(conn)
	if err != nil {
		log.Printf("central accepted the shutdown request but did not reply (already stopping?): %v", err)
		return
	}
	var rsp common.ShutdownRsp
	if err := proto.Unmarshal(msg.Data, &rsp); err == nil {
		if rsp.Success {
			log.Printf("central: %s", rsp.Message)
			return
		}
		log.Fatalf("central rejected shutdown: %s", rsp.Message)
	}
	log.Printf("central replied with unexpected message type %d", msg.Type)
}
