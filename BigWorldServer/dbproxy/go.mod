module bigworld/dbproxy

go 1.23

require (
	bigworld/common v0.0.0
	github.com/go-sql-driver/mysql v1.9.3
	google.golang.org/protobuf v1.36.11
)

require filippo.io/edwards25519 v1.1.0 // indirect

replace bigworld/common => ../Common
