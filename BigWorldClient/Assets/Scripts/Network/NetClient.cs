using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace BigWorldClient.Network
{

    public sealed class NetClient : IDisposable
    {
        public enum NetEventKind { Connected, Message, Disconnected }

        public readonly struct NetEvent
        {
            public readonly NetEventKind Kind;
            public readonly int ConnectionId;
            public readonly int MsgType;
            public readonly byte[] Payload;
            public readonly string Error;

            public NetEvent(NetEventKind kind, int connectionId, int msgType, byte[] payload, string error)
            {
                Kind = kind;
                ConnectionId = connectionId;
                MsgType = msgType;
                Payload = payload;
                Error = error;
            }
        }

        private readonly struct OutboxItem
        {
            public readonly int Token;
            public readonly byte[] Frame;

            public OutboxItem(int token, byte[] frame)
            {
                Token = token;
                Frame = frame;
            }
        }

        private readonly ConcurrentQueue<NetEvent> _events = new ConcurrentQueue<NetEvent>();
        private readonly ConcurrentQueue<OutboxItem> _outbox = new ConcurrentQueue<OutboxItem>();
        private readonly AutoResetEvent _inboxSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent _outboxSignal = new AutoResetEvent(false);
        private readonly object _ioLock = new object();

        private TcpClient _tcp;
        private Thread _readThread;
        private Thread _writeThread;
        private volatile bool _running;
        private volatile int _connectionToken;

        public bool IsConnected
        {
            get
            {
                lock (_ioLock) return _running && _tcp != null && _tcp.Connected;
            }
        }

        public int Connect(string host, int port, int timeoutMs)
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
                    return 0;
                }
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    tcp.Close();
                    return 0;
                }
                try
                {
                    tcp.EndConnect(ar);
                }
                catch
                {
                    tcp.Close();
                    return 0;
                }

                tcp.NoDelay = true;
                tcp.SendTimeout = 5000;
                _tcp = tcp;
                _running = true;
                int token = ++_connectionToken;
                _readThread = new Thread(() => ReadLoop(tcp, token)) { IsBackground = true };
                _writeThread = new Thread(() => WriteLoop(tcp, token)) { IsBackground = true };
                _readThread.Start();
                _writeThread.Start();
                EnqueueEvent(new NetEvent(NetEventKind.Connected, token, 0, null, null));
                return token;
            }
        }

        public bool Send(int msgType, byte[] payload)
        {
            if (!_running) return false;
            _outbox.Enqueue(new OutboxItem(_connectionToken, Frame.Encode(msgType, payload)));
            _outboxSignal.Set();
            return true;
        }

        public bool TryDequeue(out NetEvent evt) => _events.TryDequeue(out evt);

        public void WaitForEvent(int milliseconds) => _inboxSignal.WaitOne(milliseconds);

        public void Drain()
        {
            while (_events.TryDequeue(out _)) { }
            while (_outbox.TryDequeue(out _)) { }
        }

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
            if (_writeThread != null && _writeThread.IsAlive) _writeThread.Join(300);
        }

        private void EnqueueEvent(NetEvent evt)
        {
            _events.Enqueue(evt);
            _inboxSignal.Set();
        }

        private void AbortConnection(int token, string error)
        {
            lock (_ioLock)
            {
                if (token != _connectionToken) return;
                _running = false;
                _connectionToken++;
                try { _tcp?.Close(); } catch { }
            }
            EnqueueEvent(new NetEvent(NetEventKind.Disconnected, token, 0, null, error));
        }

        private void ReadLoop(TcpClient tcp, int token)
        {
            string err = null;
            try
            {
                var stream = tcp.GetStream();
                var header = new byte[4];
                byte[] body = new byte[1024];
                while (_running && token == _connectionToken)
                {
                    if (!ReadExactly(stream, header, 4)) { err = "connection closed"; break; }

                    int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    if (length < 2 || length > 1024 * 1024) { err = "invalid frame length"; break; }

                    if (body.Length < length) body = new byte[Math.Max(length, body.Length * 2)];
                    if (!ReadExactly(stream, body, length)) { err = "connection closed"; break; }

                    int msgType = (body[0] << 8) | body[1];
                    var payload = new byte[length - 2];
                    Buffer.BlockCopy(body, 2, payload, 0, length - 2);
                    EnqueueEvent(new NetEvent(NetEventKind.Message, token, msgType, payload, null));
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            finally
            {
                if (token == _connectionToken)
                    EnqueueEvent(new NetEvent(NetEventKind.Disconnected, token, 0, null, err));
            }
        }

        private void WriteLoop(TcpClient tcp, int token)
        {
            string err = null;
            try
            {
                var stream = tcp.GetStream();
                while (_running && token == _connectionToken)
                {
                    while (_outbox.TryDequeue(out OutboxItem item))
                    {
                        if (item.Token != token) continue;
                        stream.Write(item.Frame, 0, item.Frame.Length);
                    }
                    if (_outbox.IsEmpty)
                        _outboxSignal.WaitOne(200);
                }
            }
            catch (Exception ex)
            {
                err = "send failed: " + ex.Message;
            }
            finally
            {
                if (err != null)
                    AbortConnection(token, err);
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
