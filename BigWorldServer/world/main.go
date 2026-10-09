package main

import (
	"flag"
	"log"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	serverKey := flag.String("server", "world-1", "server config key")
	configPath := flag.String("config", "common/config.json", "config file path")
	flag.Parse()
	cfg := common.MustLoadServerConfig(*configPath, *serverKey)
	ws := NewWorldServer(cfg.Name, cfg)
	if err := ws.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("世界服启动中，监听", cfg.ListenAddr, "...")
	if err := ws.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("world server error: %v", err)
	}
}
