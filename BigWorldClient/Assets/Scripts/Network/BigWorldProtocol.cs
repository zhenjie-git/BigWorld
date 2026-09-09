using System;

namespace BigWorldClient.Network
{

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
