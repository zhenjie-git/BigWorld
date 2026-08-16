package main

import (
	"log"

	"bigworld/common"
)

func main() {
	log.SetFlags(log.Ldate | log.Ltime | log.Lshortfile)

	cfg := common.MustLoadServerConfig("common/config.json", "dbproxy")

	// Connect to MySQL before anything else: a dbproxy that cannot reach its
	// database is useless. Auto-creates the database, tables, and seed accounts.
	db, err := OpenDB(common.Config.MySQL.DSN)
	if err != nil {
		log.Fatalf("failed to connect to MySQL: %v", err)
	}

	srv := NewDbProxyServer(cfg.Name, db)
	if err := srv.Listen(cfg.ListenAddr); err != nil {
		log.Fatalf("failed to listen: %v", err)
	}

	log.Println("持久化代理启动中，监听", cfg.ListenAddr, "...")
	if err := srv.Start(cfg.CentralAddr); err != nil {
		log.Fatalf("dbproxy server error: %v", err)
	}
}
