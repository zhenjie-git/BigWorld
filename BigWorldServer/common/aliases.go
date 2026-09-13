package common

import "bigworld/common/pb"

type ServerType = pb.ServerType

const (
	ServerInvalid = pb.ServerType_SERVER_INVALID
	ServerCentral = pb.ServerType_SERVER_CENTRAL
	ServerWorld   = pb.ServerType_SERVER_WORLD
	ServerGateway = pb.ServerType_SERVER_GATEWAY
	ServerLogin   = pb.ServerType_SERVER_LOGIN
	ServerDbProxy = pb.ServerType_SERVER_DBPROXY
)

type (
	HelloReq             = pb.HelloReq
	HelloRsp             = pb.HelloRsp
	HeartbeatReq         = pb.HeartbeatReq
	HeartbeatRsp         = pb.HeartbeatRsp
	ServerListReq        = pb.ServerListReq
	ServerEntry          = pb.ServerEntry
	ServerListRsp        = pb.ServerListRsp
	LoginReq             = pb.LoginReq
	LoginRsp             = pb.LoginRsp
	GatewayAssignReq     = pb.GatewayAssignReq
	GatewayAssignRsp     = pb.GatewayAssignRsp
	TokenVerifyReq       = pb.TokenVerifyReq
	TokenVerifyRsp       = pb.TokenVerifyRsp
	LoginPrepareReq      = pb.LoginPrepareReq
	LoginPrepareRsp      = pb.LoginPrepareRsp
	LoginFinishReq       = pb.LoginFinishReq
	LoginFinishRsp       = pb.LoginFinishRsp
	ForceKickNotify      = pb.ForceKickNotify
	EnterSceneNotify     = pb.EnterSceneNotify
	LogoutReq            = pb.LogoutReq
	LogoutRsp            = pb.LogoutRsp
	LogoutBeginReq       = pb.LogoutBeginReq
	LogoutBeginRsp       = pb.LogoutBeginRsp
	LogoutCleanupReq     = pb.LogoutCleanupReq
	LogoutCleanupRsp     = pb.LogoutCleanupRsp
	CreateEntityReq      = pb.CreateEntityReq
	CreateEntityRsp      = pb.CreateEntityRsp
	DestroyEntityReq     = pb.DestroyEntityReq
	DestroyEntityRsp     = pb.DestroyEntityRsp
	ResumeEntityReq      = pb.ResumeEntityReq
	ResumeEntityRsp      = pb.ResumeEntityRsp
	MoveRsp              = pb.MoveRsp
	MoveDir              = pb.MoveDir
	WalkStartReq         = pb.WalkStartReq
	RunStartReq          = pb.RunStartReq
	SprintStartReq       = pb.SprintStartReq
	JumpStartReq         = pb.JumpStartReq
	DashStartReq         = pb.DashStartReq
	RollStartReq         = pb.RollStartReq
	StopStartReq         = pb.StopStartReq
	MoveStopReq          = pb.MoveStopReq
	MoveDirChangeReq     = pb.MoveDirChangeReq
	SkillReq             = pb.SkillReq
	SkillRsp             = pb.SkillRsp
	ClientHeartbeatReq   = pb.ClientHeartbeatReq
	ClientHeartbeatRsp   = pb.ClientHeartbeatRsp
	ValidateAccountReq   = pb.ValidateAccountReq
	ValidateAccountRsp   = pb.ValidateAccountRsp
	LoadPlayerReq        = pb.LoadPlayerReq
	LoadPlayerRsp        = pb.LoadPlayerRsp
	PlayerData           = pb.PlayerData
	SavePlayerReq        = pb.SavePlayerReq
	SavePlayerRsp        = pb.SavePlayerRsp
	ShutdownReq          = pb.ShutdownReq
	ShutdownRsp          = pb.ShutdownRsp
	ShutdownNotify       = pb.ShutdownNotify
	ShutdownAck          = pb.ShutdownAck
	ServerShutdownNotify = pb.ServerShutdownNotify
)
