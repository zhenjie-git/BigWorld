using System;

namespace BigWorldClient.Network
{
    /// <summary>Message type numbers, mirroring common/message_type.go.</summary>
    public static class MessageTypes
    {
        public const int Cli2Lg_LoginReq = 7;
        public const int Lg2Cli_LoginRsp = 8;
        public const int Wd2Cli_EnterSceneNotify = 15;
        public const int Wd2Cli_MoveRsp = 17;
        public const int Cli2Gw_LogoutReq = 21;
        public const int Gw2Cli_LogoutRsp = 22;
        public const int Gw2Cli_LoginRsp = 34;
        public const int Cli2Gw_LoginReq = 35;
        public const int Cli2Gw_HeartbeatReq = 36;
        public const int Gw2Cli_HeartbeatRsp = 37;
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
    }

    /// <summary>
    /// Wire frame: [4B big-endian total_len][2B big-endian msgType][payload],
    /// total_len = 2 + len(payload). The payload is a protobuf message — encoded
    /// and decoded by the protoc-generated classes in Generated/Bigworld.cs
    /// (namespace BigWorldClient.Network.Protocol).
    /// </summary>
    public static class Frame
    {
        public static byte[] Encode(int msgType, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            int total = 2 + payload.Length;
            var buf = new byte[4 + total];
            buf[0] = (byte)(total >> 24);
            buf[1] = (byte)(total >> 16);
            buf[2] = (byte)(total >> 8);
            buf[3] = (byte)total;
            buf[4] = (byte)(msgType >> 8);
            buf[5] = (byte)msgType;
            Buffer.BlockCopy(payload, 0, buf, 6, payload.Length);
            return buf;
        }
    }
}
