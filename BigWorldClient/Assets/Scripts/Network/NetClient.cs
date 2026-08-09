using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace BigWorldClient.Network
{
    /// <summary>
    /// Thread-safe TCP client for the BigWorld binary frame protocol. A background
    /// thread reads frames and pushes them into an event queue; the consumer
    /// (GameSession) drains the queue. All pure C#, no UnityEngine references, so
    /// it can be exercised by the standalone harness too.
    /// </summary>
    public sealed class NetClient : IDisposable
    {
        public enum NetEventKind { Connected, Message, Disconnected }

        public readonly struct NetEvent
        {
            public readonly NetEventKind Kind;
            public readonly int MsgType;
            public readonly byte[] Payload;
            public readonly string Error;

            public NetEvent(NetEventKind kind, int msgType, byte[] payload, string error)
            {
                Kind = kind;
                MsgType = msgType;
                Payload = payload;
                Error = error;
            }
        }

        private readonly ConcurrentQueue<NetEvent> _events = new ConcurrentQueue<NetEvent>();
        private readonly object _ioLock = new object();

        private TcpClient _tcp;
        private Thread _readThread;
        private volatile bool _running;
        private volatile int _connectionToken;

        public bool IsConnected
        {
            get
            {
                lock (_ioLock) return _running && _tcp != null && _tcp.Connected;
            }
        }

        /// <summary>Connect to host:port with a timeout. Enqueues a Connected event on success.</summary>
        public bool Connect(string host, int port, int timeoutMs)
        {
            lock (_ioLock)
            {
                CloseInternalLocked();

                var tcp = new TcpClient();
                IAsyncResult ar;
                try
                {
                    ar = tcp.BeginConnect(host, port, null, null);
                }
                catch
                {
                    tcp.Close();
                    return false;
                }
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    tcp.Close();
                    return false;
                }
                try
                {
                    tcp.EndConnect(ar);
                }
                catch
                {
                    tcp.Close();
                    return false;
                }

                _tcp = tcp;
                _running = true;
                int token = ++_connectionToken;
                _readThread = new Thread(() => ReadLoop(tcp, token)) { IsBackground = true };
                _readThread.Start();
                _events.Enqueue(new NetEvent(NetEventKind.Connected, 0, null, null));
                return true;
            }
        }

        public bool Send(int msgType, byte[] payload)
        {
            lock (_ioLock)
            {
                if (!_running || _tcp == null || !_tcp.Connected) return false;
                try
                {
                    byte[] frame = Frame.Encode(msgType, payload);
                    _tcp.GetStream().Write(frame, 0, frame.Length);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool TryDequeue(out NetEvent evt) => _events.TryDequeue(out evt);

        public void Disconnect()
        {
            lock (_ioLock) CloseInternalLocked();
        }

        private void CloseInternalLocked()
        {
            _running = false;
            _connectionToken++;
            try { _tcp?.Close(); } catch { }
            if (_readThread != null && _readThread.IsAlive) _readThread.Join(300);
        }

        private void ReadLoop(TcpClient tcp, int token)
        {
            string err = null;
            try
            {
                var stream = tcp.GetStream();
                var header = new byte[4];
                while (_running && token == _connectionToken)
                {
                    if (!ReadExactly(stream, header, 4)) { err = "connection closed"; break; }

                    int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    if (length < 2 || length > 1024 * 1024) { err = "invalid frame length"; break; }

                    var body = new byte[length];
                    if (!ReadExactly(stream, body, length)) { err = "connection closed"; break; }

                    int msgType = (body[0] << 8) | body[1];
                    var payload = new byte[length - 2];
                    Buffer.BlockCopy(body, 2, payload, 0, length - 2);
                    _events.Enqueue(new NetEvent(NetEventKind.Message, msgType, payload, null));
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            finally
            {
                // Only report the disconnect for the current connection; a stale
                // thread from a previous connection must not raise spurious events.
                if (token == _connectionToken)
                    _events.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, null, err));
            }
        }

        private static bool ReadExactly(NetworkStream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public void Dispose() => Disconnect();
    }
}
