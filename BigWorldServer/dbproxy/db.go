package main

import (
	"database/sql"
	"fmt"
	"log"
	"strings"

	"bigworld/common"

	"github.com/go-sql-driver/mysql"
)

// playerDB wraps a *sql.DB connection pool to the MySQL backend.
// dbproxy is the only process that touches MySQL; world and login reach it
// over TCP via the messages defined in common/pb.
type playerDB struct {
	db *sql.DB
}

const (
	schemaPlayers = `CREATE TABLE IF NOT EXISTS players (
  player_id   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  account     VARCHAR(64)  NOT NULL UNIQUE,
  x           DOUBLE       NOT NULL DEFAULT 0,
  z           DOUBLE       NOT NULL DEFAULT 0,
  updated_at  TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`

	schemaAccounts = `CREATE TABLE IF NOT EXISTS accounts (
  account    VARCHAR(64)  NOT NULL PRIMARY KEY,
  password   VARCHAR(128) NOT NULL,
  created_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;`
)

// openDB parses the DSN, creates the database if it does not yet exist,
// opens a pool, verifies connectivity, and ensures the schema + seed accounts
// are present. This lets dbproxy start cleanly on a fresh MySQL instance
// without any manual SQL setup.
func openDB(dsn string) (*playerDB, error) {
	cfg, err := mysql.ParseDSN(dsn)
	if err != nil {
		return nil, fmt.Errorf("parse dsn: %w", err)
	}
	dbName := cfg.DBName
	if dbName == "" {
		dbName = "bigworld"
	}

	// Connect to the server (no database selected) to create the DB if missing.
	cfg.DBName = ""
	serverDB, err := sql.Open("mysql", cfg.FormatDSN())
	if err != nil {
		return nil, fmt.Errorf("open server conn: %w", err)
	}
	if _, err := serverDB.Exec("CREATE DATABASE IF NOT EXISTS `" + dbName + "`"); err != nil {
		serverDB.Close()
		return nil, fmt.Errorf("create database %s: %w", dbName, err)
	}
	serverDB.Close()

	db, err := sql.Open("mysql", dsn)
	if err != nil {
		return nil, fmt.Errorf("open db: %w", err)
	}
	db.SetMaxOpenConns(16)
	db.SetMaxIdleConns(4)

	if err := db.Ping(); err != nil {
		db.Close()
		return nil, fmt.Errorf("ping %s: %w", dbName, err)
	}
	if err := ensureSchema(db); err != nil {
		db.Close()
		return nil, err
	}
	log.Printf("[dbproxy] connected to MySQL database %s", dbName)
	return &playerDB{db: db}, nil
}

func ensureSchema(db *sql.DB) error {
	if _, err := db.Exec(schemaPlayers); err != nil {
		return fmt.Errorf("create players table: %w", err)
	}
	if _, err := db.Exec(schemaAccounts); err != nil {
		return fmt.Errorf("create accounts table: %w", err)
	}
	// Seed the two demo accounts that the old hardcoded table provided,
	// so existing clients (admin/test) keep working on a fresh DB.
	for _, a := range [][2]string{{"admin", "123456"}, {"test", "123456"}} {
		if _, err := db.Exec("INSERT IGNORE INTO accounts (account, password) VALUES (?, ?)", a[0], a[1]); err != nil {
			return fmt.Errorf("seed account %s: %w", a[0], err)
		}
	}
	if _, err := db.Exec(`ALTER TABLE players CHANGE y z DOUBLE NOT NULL DEFAULT 0`); err != nil {
		if !isColumnGone(err) {
			return fmt.Errorf("migrate players.y -> z: %w", err)
		}
	}
	return nil
}

func isColumnGone(err error) bool {
	return strings.Contains(err.Error(), "1054") ||
		strings.Contains(err.Error(), "Unknown column") ||
		strings.Contains(err.Error(), "check that column/key exists")
}

// LoadPlayer returns the saved player for an account. If the account has no
// row yet, a new one is inserted (allocating a stable AUTO_INCREMENT player_id)
// and found is false so the caller spawns at a random position.
func (p *playerDB) LoadPlayer(account string) (found bool, playerID uint64, x, z float64, err error) {
	row := p.db.QueryRow("SELECT player_id, x, z FROM players WHERE account = ?", account)
	switch e := row.Scan(&playerID, &x, &z); e {
	case nil:
		return true, playerID, x, z, nil
	case sql.ErrNoRows:
		res, e := p.db.Exec("INSERT INTO players (account, x, z) VALUES (?, 0, 0)", account)
		if e != nil {
			return false, 0, 0, 0, fmt.Errorf("insert new player: %w", e)
		}
		id, e := res.LastInsertId()
		if e != nil {
			return false, 0, 0, 0, fmt.Errorf("last insert id: %w", e)
		}
		return false, uint64(id), 0, 0, nil
	default:
		return false, 0, 0, 0, fmt.Errorf("load player: %w", e)
	}
}

// SavePlayers upserts positions for the given players in a single transaction.
// Used both for single-player save on destroy and batch autosave.
func (p *playerDB) SavePlayers(players []*common.PlayerData) error {
	if len(players) == 0 {
		return nil
	}
	tx, err := p.db.Begin()
	if err != nil {
		return fmt.Errorf("begin tx: %w", err)
	}
	const stmt = "INSERT INTO players (player_id, account, x, z) VALUES (?, ?, ?, ?) " +
		"ON DUPLICATE KEY UPDATE x = VALUES(x), z = VALUES(z)"
	for _, pl := range players {
		if _, err := tx.Exec(stmt, pl.PlayerId, pl.Account, pl.X, pl.Z); err != nil {
			tx.Rollback()
			return fmt.Errorf("upsert player %d: %w", pl.PlayerId, err)
		}
	}
	return tx.Commit()
}

// ValidateAccount checks the credentials against the accounts table.
// NOTE: passwords are stored in plaintext to match the previous hardcoded
// table; production should hash (e.g. bcrypt).
func (p *playerDB) ValidateAccount(account, password string) (bool, error) {
	var n int
	err := p.db.QueryRow("SELECT 1 FROM accounts WHERE account = ? AND password = ?", account, password).Scan(&n)
	switch err {
	case nil:
		return true, nil
	case sql.ErrNoRows:
		return false, nil
	default:
		return false, fmt.Errorf("validate account: %w", err)
	}
}
