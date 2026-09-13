package common

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"math/big"
	"strings"
	"sync"
	"time"
)

var (
	tokenSignKey   *ecdsa.PrivateKey
	tokenVerifyKey *ecdsa.PublicKey
)

var usedTokens = struct {
	sync.Mutex
	seen map[string]int64
}{seen: make(map[string]int64)}

func NewSessionId() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("sess-%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

func InitTokenVerifier(pemStr string) error {
	block, _ := pem.Decode([]byte(pemStr))
	if block == nil {
		return fmt.Errorf("failed to decode token public key PEM")
	}
	key, err := x509.ParsePKIXPublicKey(block.Bytes)
	if err != nil {
		return fmt.Errorf("failed to parse token public key: %w", err)
	}
	var ok bool
	tokenVerifyKey, ok = key.(*ecdsa.PublicKey)
	if !ok {
		return fmt.Errorf("token public key is not ECDSA")
	}
	return nil
}

func InitTokenSigner(pemStr string) error {
	block, _ := pem.Decode([]byte(pemStr))
	if block == nil {
		return fmt.Errorf("failed to decode token private key PEM")
	}
	key, err := x509.ParseECPrivateKey(block.Bytes)
	if err != nil {
		return fmt.Errorf("failed to parse token private key: %w", err)
	}
	tokenSignKey = key
	return nil
}

func GenerateToken(account string) (string, error) {
	if tokenSignKey == nil {
		return "", fmt.Errorf("token sign key not initialized")
	}
	nonce := make([]byte, 8)
	if _, err := rand.Read(nonce); err != nil {
		return "", fmt.Errorf("generate nonce: %w", err)
	}
	nonceHex := hex.EncodeToString(nonce)
	tsHex := fmt.Sprintf("%x", time.Now().Unix())
	payload := strings.Join([]string{nonceHex, account, tsHex}, ":")

	hash := sha256.Sum256([]byte(payload))
	r, s, err := ecdsa.Sign(rand.Reader, tokenSignKey, hash[:])
	if err != nil {
		return "", fmt.Errorf("sign token: %w", err)
	}

	sig := make([]byte, 64)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return payload + ":" + hex.EncodeToString(sig), nil
}

func VerifyToken(token string) (string, error) {
	if tokenVerifyKey == nil {
		return "", fmt.Errorf("token verify key not initialized")
	}
	parts := strings.Split(token, ":")
	if len(parts) != 4 {
		return "", fmt.Errorf("invalid token format")
	}
	nonceHex, account, tsHex, sigHex := parts[0], parts[1], parts[2], parts[3]
	payload := strings.Join([]string{nonceHex, account, tsHex}, ":")

	sigBytes, err := hex.DecodeString(sigHex)
	if err != nil {
		return "", fmt.Errorf("invalid token signature encoding: %w", err)
	}
	if len(sigBytes) != 64 {
		return "", fmt.Errorf("invalid token signature length: %d", len(sigBytes))
	}

	r := new(big.Int).SetBytes(sigBytes[:32])
	s := new(big.Int).SetBytes(sigBytes[32:])

	hash := sha256.Sum256([]byte(payload))
	if !ecdsa.Verify(tokenVerifyKey, hash[:], r, s) {
		return "", fmt.Errorf("invalid token signature")
	}

	var tokenUnix int64
	if _, err := fmt.Sscanf(tsHex, "%x", &tokenUnix); err != nil {
		return "", fmt.Errorf("cannot parse timestamp: %w", err)
	}

	elapsed := time.Now().Unix() - tokenUnix
	if elapsed < 0 || elapsed > 600 {
		return "", fmt.Errorf("token expired")
	}

	now := time.Now().Unix()
	usedTokens.Lock()
	for k, exp := range usedTokens.seen {
		if exp <= now {
			delete(usedTokens.seen, k)
		}
	}
	if exp, ok := usedTokens.seen[token]; ok && exp > now {
		usedTokens.Unlock()
		return "", fmt.Errorf("token already used")
	}
	usedTokens.seen[token] = now + 600
	usedTokens.Unlock()

	return account, nil
}
