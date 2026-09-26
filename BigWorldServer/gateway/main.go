package main

import (
	"flag"
	"log"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	configPath := flag.String("config", "common/config.json", "config file path")
	flag.Parse()
	cfg := common.MustLoadServerConfig(*configPath, "gateway")
	gs := NewGatewayServer(cfg.Name)
	if err := gs.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("网关服启动中，监听", cfg.ListenAddr, "...")
	if err := gs.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("gateway server error: %v", err)
	}
}
