package main

import (
	"log"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	cfg := common.MustLoadServerConfig("common/config.json", "gateway")
	gs := newGatewayServer(cfg.Name)
	if err := gs.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("网关服启动中，监听", cfg.ListenAddr, "...")
	if err := gs.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("gateway server error: %v", err)
	}
}
