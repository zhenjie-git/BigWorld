namespace BigWorldClient.Network
{
    public static class MessageTypes
    {
        public const int Srv2Srv_HelloReq = 1;
        public const int Srv2Srv_HelloRsp = 2;
        public const int Srv2Ct_HeartbeatReq = 3;
        public const int Ct2Srv_HeartbeatRsp = 4;
        public const int Srv2Ct_ServerListReq = 5;
        public const int Ct2Srv_ServerListRsp = 6;
        public const int Cli2Lg_LoginReq = 7;
        public const int Lg2Cli_LoginRsp = 8;
        public const int Lg2Ct_GatewayAssignReq = 9;
        public const int Ct2Lg_GatewayAssignRsp = 10;
        public const int Gw2Wd_CreateEntityReq = 13;
        public const int Wd2Gw_CreateEntityRsp = 14;
        public const int Wd2Cli_EnterSceneNotify = 15;
        public const int Wd2Cli_MoveRsp = 17;
        public const int Cli2Wd_SkillReq = 18;
        public const int Wd2Cli_SkillRsp = 19;
        public const int Ct2Gw_NewWorldNotify = 20;
        public const int Cli2Gw_LogoutReq = 21;
        public const int Gw2Cli_LogoutRsp = 22;
        public const int Gw2Wd_DestroyEntityReq = 23;
        public const int Wd2Gw_DestroyEntityRsp = 24;
        public const int Gw2Ct_LogoutBeginReq = 25;
        public const int Ct2Gw_LogoutBeginRsp = 26;
        public const int Gw2Ct_LogoutCleanupReq = 27;
        public const int Ct2Gw_LogoutCleanupRsp = 28;
        public const int Gw2Ct_LoginPrepareReq = 29;
        public const int Ct2Gw_LoginPrepareRsp = 30;
        public const int Gw2Ct_LoginFinishReq = 31;
        public const int Ct2Gw_LoginFinishRsp = 32;
        public const int Ct2Gw_ForceKickNotify = 33;
        public const int Gw2Cli_LoginRsp = 34;
        public const int Cli2Gw_LoginReq = 35;
        public const int Cli2Gw_HeartbeatReq = 36;
        public const int Gw2Cli_HeartbeatRsp = 37;
        public const int Lg2Db_ValidateAccountReq = 38;
        public const int Db2Lg_ValidateAccountRsp = 39;
        public const int Wd2Db_LoadPlayerReq = 40;
        public const int Db2Wd_LoadPlayerRsp = 41;
        public const int Wd2Db_SavePlayerReq = 42;
        public const int Db2Wd_SavePlayerRsp = 43;
        public const int Ct2Srv_NewDbProxyNotify = 44;
        public const int Srv2Ct_ShutdownReq = 45;
        public const int Ct2Srv_ShutdownRsp = 46;
        public const int Ct2Srv_ShutdownNotify = 47;
        public const int Srv2Ct_ShutdownAck = 48;
        public const int Gw2Cli_ServerShutdownNotify = 49;
        public const int Cli2Wd_WalkStartReq = 50;
        public const int Cli2Wd_RunStartReq = 51;
        public const int Cli2Wd_SprintStartReq = 52;
        public const int Cli2Wd_JumpStartReq = 53;
        public const int Cli2Wd_DashStartReq = 54;
        public const int Cli2Wd_RollStartReq = 55;
        public const int Cli2Wd_StopStartReq = 56;
        public const int Cli2Wd_MoveStopReq = 57;
        public const int Cli2Wd_MoveDirChangeReq = 58;
        public const int Gw2Wd_ResumeEntityReq = 60;
        public const int Wd2Gw_ResumeEntityRsp = 61;
    }
}
