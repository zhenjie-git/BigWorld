package main

import (
	"log"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	cfg := common.MustLoadServerConfig("common/config.json", "world")
	ws := NewWorldServer(cfg.Name)
	if err := ws.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("世界服启动中，监听", cfg.ListenAddr, "...")
	if err := ws.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("world server error: %v", err)
	}
}
