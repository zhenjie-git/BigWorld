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
	cfg := common.MustLoadServerConfig(*configPath, "central")
	cs := NewCentralServer(cfg.Name)
	if err := cs.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("中央控制器启动成功，监听", cfg.ListenAddr)
	if err := cs.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("central server error: %v", err)
	}
}
