package main

import (
	"log"
	"time"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	cfg := common.MustLoadServerConfig("common/config.json", "central")
	cs := newCentralServer(cfg.Name)
	if err := cs.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	reapTicker := time.NewTicker(15 * time.Second)
	go func() {
		for range reapTicker.C {
			cs.reapStale(25 * time.Second)
		}
	}()
	defer reapTicker.Stop()

	log.Println("中央控制器启动成功，监听", cfg.ListenAddr)
	if err := cs.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("central server error: %v", err)
	}
}
