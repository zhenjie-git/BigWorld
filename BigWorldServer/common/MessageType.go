package common

type MessageType uint16

const (
	Srv2Srv_HelloReq            MessageType = 1
	Srv2Srv_HelloRsp            MessageType = 2
	Srv2Ct_HeartbeatReq         MessageType = 3
	Ct2Srv_HeartbeatRsp         MessageType = 4
	Srv2Ct_ServerListReq        MessageType = 5
	Ct2Srv_ServerListRsp        MessageType = 6
	Cli2Lg_LoginReq             MessageType = 7
	Lg2Cli_LoginRsp             MessageType = 8
	Lg2Ct_GatewayAssignReq      MessageType = 9
	Ct2Lg_GatewayAssignRsp      MessageType = 10
	Gw2Wd_CreateEntityReq       MessageType = 13
	Wd2Gw_CreateEntityRsp       MessageType = 14
	Wd2Cli_EnterSceneNotify     MessageType = 15
	Wd2Cli_MoveRsp              MessageType = 17
	Cli2Wd_SkillReq             MessageType = 18
	Wd2Cli_SkillRsp             MessageType = 19
	Ct2Gw_NewWorldNotify        MessageType = 20
	Cli2Gw_LogoutReq            MessageType = 21
	Gw2Cli_LogoutRsp            MessageType = 22
	Gw2Wd_DestroyEntityReq      MessageType = 23
	Wd2Gw_DestroyEntityRsp      MessageType = 24
	Gw2Ct_LogoutBeginReq        MessageType = 25
	Ct2Gw_LogoutBeginRsp        MessageType = 26
	Gw2Ct_LogoutCleanupReq      MessageType = 27
	Ct2Gw_LogoutCleanupRsp      MessageType = 28
	Gw2Ct_LoginPrepareReq       MessageType = 29
	Ct2Gw_LoginPrepareRsp       MessageType = 30
	Gw2Ct_LoginFinishReq        MessageType = 31
	Ct2Gw_LoginFinishRsp        MessageType = 32
	Ct2Gw_ForceKickNotify       MessageType = 33
	Gw2Cli_LoginRsp             MessageType = 34
	Cli2Gw_LoginReq             MessageType = 35
	Cli2Gw_HeartbeatReq         MessageType = 36
	Gw2Cli_HeartbeatRsp         MessageType = 37
	Lg2Db_ValidateAccountReq    MessageType = 38
	Db2Lg_ValidateAccountRsp    MessageType = 39
	Wd2Db_LoadPlayerReq         MessageType = 40
	Db2Wd_LoadPlayerRsp         MessageType = 41
	Wd2Db_SavePlayerReq         MessageType = 42
	Db2Wd_SavePlayerRsp         MessageType = 43
	Ct2Srv_NewDbProxyNotify     MessageType = 44
	Srv2Ct_ShutdownReq          MessageType = 45
	Ct2Srv_ShutdownRsp          MessageType = 46
	Ct2Srv_ShutdownNotify       MessageType = 47
	Srv2Ct_ShutdownAck          MessageType = 48
	Gw2Cli_ServerShutdownNotify MessageType = 49
	Cli2Wd_WalkStartReq         MessageType = 50
	Cli2Wd_RunStartReq          MessageType = 51
	Cli2Wd_SprintStartReq       MessageType = 52
	Cli2Wd_JumpStartReq         MessageType = 53
	Cli2Wd_DashStartReq         MessageType = 54
	Cli2Wd_RollStartReq         MessageType = 55
	Cli2Wd_StopStartReq         MessageType = 56
	Cli2Wd_MoveStopReq          MessageType = 57
	Cli2Wd_MoveDirChangeReq     MessageType = 58
	Gw2Wd_ResumeEntityReq       MessageType = 60
	Wd2Gw_ResumeEntityRsp       MessageType = 61
)
