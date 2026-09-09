module bigworld/world

go 1.23

require (
	bigworld/common v0.0.0
	google.golang.org/protobuf v1.36.11
)

require github.com/google/flatbuffers v24.3.25+incompatible // indirect

replace bigworld/common => ../Common
