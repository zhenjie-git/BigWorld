package common

import (
	"encoding/json"
	"log"
	"os"
)

type ServerConfig struct {
	ID          int    `json:"id"`
	Name        string `json:"name"`
	ListenAddr  string `json:"listen_addr"`
	CentralAddr string `json:"central_addr"`

	StateConfigFile string `json:"state_config_file,omitempty"`

	// Shared GameConfig files (relative to the BigWorldServer working dir).
	// The single source of truth for movement params + state transition table;
	// movement params are derived from Player/player_config.bytes, never
	// duplicated here. The nine server-read scalars are authored in
	// GameConfig/Player/player_config.xlsx.
	PlayerConfigFile         string `json:"player_config_file,omitempty"`
	StateTransitionTableFile string `json:"state_transition_table_file,omitempty"`
	SceneConfigFile          string `json:"scene_config_file,omitempty"`

	// Server-authoritative movement simulation tick interval (world process).
	MovementTickMs int `json:"movement_tick_ms,omitempty"`
}

// MySQLConfig holds the connection string for the dbproxy's database.
// Only the dbproxy process reads this; other processes reach MySQL indirectly
// via the dbproxy over TCP.
type MySQLConfig struct {
	DSN string `json:"dsn"` // e.g. root:password@tcp(127.0.0.1:3306)/bigworld?parseTime=true
}

// AppConfig is the top-level application configuration.
type AppConfig struct {
	ECDSAPrivateKey string                  `json:"ecdsa_private_key"`
	ECDSAPublicKey  string                  `json:"ecdsa_public_key"`
	MySQL           MySQLConfig             `json:"mysql"`
	Servers         map[string]ServerConfig `json:"servers"`
}

// Config is the global configuration, populated by LoadConfig.
var Config AppConfig

// LoadConfig reads and parses a JSON config file, then initializes
// the token verifier with the configured public key.
func LoadConfig(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	if err := json.Unmarshal(data, &Config); err != nil {
		return err
	}
	return InitTokenVerifier(Config.ECDSAPublicKey)
}

// MustLoadServerConfig loads config from path and returns the named server's config.
// It calls log.Fatalf if loading fails or the server key is missing.
func MustLoadServerConfig(path, key string) ServerConfig {
	if err := LoadConfig(path); err != nil {
		log.Fatalf("failed to load config: %v", err)
	}
	cfg, ok := Config.Servers[key]
	if !ok {
		log.Fatalf("config missing server entry: %s", key)
	}
	if cfg.ListenAddr == "" {
		log.Fatalf("config server %s missing listen_addr", key)
	}
	return cfg
}
