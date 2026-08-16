package main

import (
	"log"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	cfg := common.MustLoadServerConfig("common/config.json", "login")

	if err := common.InitTokenSigner(common.Config.ECDSAPrivateKey); err != nil {
		log.Fatalf("failed to init token signer: %v", err)
	}

	ls := NewLoginServer(cfg.Name)
	if err := ls.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("登录服启动中，监听", cfg.ListenAddr, "...")
	if err := ls.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("login server error: %v", err)
	}
}
