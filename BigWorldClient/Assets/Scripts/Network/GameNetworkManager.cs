using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using BigWorldClient.UI.Events;
using BigWorldClient.UI.Framework;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient.Network
{

    public class GameNetworkManager : MonoBehaviour
    {
        public static GameNetworkManager Instance { get; private set; }

        [Header("Session")]
        [SerializeField] private float _heartbeatIntervalSeconds = 5f;

        [Header("Reconnect")]
        [SerializeField] private float _reconnectBaseDelaySeconds = 1f;
        [SerializeField] private float _reconnectMaxDelaySeconds = 8f;
        [SerializeField] private int _maxReconnectAttempts = 8;

        private GameSession _session;
        private Coroutine _heartbeat;
        private Coroutine _reconnect;
        private bool _reconnecting;
        private bool _attemptResolved;
        private bool _attemptSucceeded;
        private string _lastHost;
        private int _lastPort;
        private string _lastUsername;
        private string _lastPassword;
        private InputAction _logoutAction;
        private Vector3 _serverSpawnPos;
        private bool _hasServerSpawn;
        private string _serverSceneId;

        public ServerClock Clock => _session?.Clock;

        public bool TryGetServerSpawn(out Vector3 pos)
        {
            pos = _serverSpawnPos;
            return _hasServerSpawn;
        }

        public bool TryGetServerScene(out string sceneId)
        {
            sceneId = _serverSceneId;
            return !string.IsNullOrEmpty(sceneId);
        }

        public bool SendDirStart(int msgType, float dirX, float dirZ, long tick) => _session != null && _session.SendDirStart(msgType, dirX, dirZ, tick);
        public bool SendStopStart(MoveState stopKind, long tick) => _session != null && _session.SendStopStart(stopKind, tick);
        public bool SendMoveStop(long tick) => _session != null && _session.SendMoveStop(tick);

        public bool TryDequeueMove(out MoveRspInfo info)
        {
            info = default;
            return _session != null && _session.TryDequeueMove(out info);
        }

        public static GameNetworkManager EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[GameNetworkManager]");
            DontDestroyOnLoad(go);
            return go.AddComponent<GameNetworkManager>();
        }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _session = new GameSession();

            _logoutAction = new InputAction("Logout", InputActionType.Button, "<Keyboard>/f10");
            _logoutAction.performed += _ => Logout();
            _logoutAction.Enable();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                _logoutAction?.Dispose();
                _session?.Dispose();
                Instance = null;
            }
        }

        private void Update()
        {
            SessionEvent evt;
            while (_session != null && _session.TryDequeue(out evt))
                HandleSessionEvent(evt);
        }

        public void StartLogin(string host, int port, string username, string password)
        {
            if (_session == null) return;
            StopHeartbeat();
            StopReconnect();
            _lastHost = host;
            _lastPort = port;
            _lastUsername = username ?? "";
            _lastPassword = password ?? "";
            _session.BeginLogin(host, port, username, password);
        }

        public void Logout()
        {
            if (_session == null) return;
            StopHeartbeat();
            StopReconnect();
            _session.Logout();
        }

        private void HandleSessionEvent(SessionEvent evt)
        {
            switch (evt.Kind)
            {
                case SessionEventKind.LoginSucceeded:
                    StartHeartbeat();
                    _serverSpawnPos = new Vector3((float)evt.Player.X, 0f, (float)evt.Player.Z);
                    _hasServerSpawn = true;
                    _serverSceneId = evt.Player.SceneId;
                    if (_reconnecting)
                    {
                        _attemptResolved = true;
                        _attemptSucceeded = true;
                    }
                    UIEventBus.Publish(new LoginResultEvent { Success = true, Message = "登录成功" });
                    break;

                case SessionEventKind.LoginFailed:
                    StopHeartbeat();
                    if (_reconnecting)
                    {
                        _attemptResolved = true;
                        _attemptSucceeded = false;
                        Debug.Log($"[GameNetworkManager] reconnect attempt failed: {evt.Message}");
                    }
                    else
                    {
                        UIEventBus.Publish(new LoginResultEvent { Success = false, Message = evt.Message });
                    }
                    break;

                case SessionEventKind.ServerShutdown:
                    StopHeartbeat();
                    StopReconnect();
                    UIEventBus.Publish(new SessionEndedEvent { Reason = "server_shutdown" });
                    ReopenLoginWithMessage("服务器已关闭: " + evt.Message);
                    break;

                case SessionEventKind.Disconnected:
                    StopHeartbeat();
                    StartReconnect();
                    break;

                case SessionEventKind.Kicked:
                    StopHeartbeat();
                    StopReconnect();
                    _reconnecting = false;
                    _reconnect = null;
                    UIEventBus.Publish(new SessionEndedEvent { Reason = "kicked" });
                    ReopenLoginWithMessage(string.IsNullOrEmpty(evt.Message) ? "account logged in elsewhere" : evt.Message);
                    break;
                case SessionEventKind.LoggedOut:
                    StopHeartbeat();
                    StopReconnect();
                    UIEventBus.Publish(new SessionEndedEvent { Reason = "logout" });
                    ReopenLoginWithMessage("已退出登录");
                    break;
            }
        }

        private void ReopenLoginWithMessage(string message)
        {
            if (UIManager.Instance != null)
                UIManager.Instance.OpenPanel(PanelIds.Login, message);
        }

        private void StartHeartbeat()
        {
            if (_heartbeat != null) return;
            _session?.ResetHeartbeatWatch();
            _session?.SendHeartbeat();
            _heartbeat = StartCoroutine(HeartbeatLoop());
        }

        private void StopHeartbeat()
        {
            if (_heartbeat != null)
            {
                StopCoroutine(_heartbeat);
                _heartbeat = null;
            }
        }

        private void StartReconnect()
        {
            _reconnecting = true;
            if (_reconnect != null) StopCoroutine(_reconnect);
            _reconnect = StartCoroutine(ReconnectRoutine());
        }

        private IEnumerator ReconnectRoutine()
        {
            const float attemptTimeoutSeconds = 20f;
            for (int attempt = 1; attempt <= _maxReconnectAttempts; attempt++)
            {
                float delay = Mathf.Min(_reconnectBaseDelaySeconds * (1 << Mathf.Min(attempt - 1, 4)), _reconnectMaxDelaySeconds);
                Debug.Log($"[GameNetworkManager] reconnect {attempt}/{_maxReconnectAttempts} in {delay:F0}s");
                yield return new WaitForSecondsRealtime(delay);

                _attemptResolved = false;
                _attemptSucceeded = false;
                _session.BeginLogin(_lastHost, _lastPort, _lastUsername, _lastPassword);

                float startedAt = Time.realtimeSinceStartup;
                while (!_attemptResolved && Time.realtimeSinceStartup - startedAt < attemptTimeoutSeconds)
                    yield return null;

                if (!_attemptResolved)
                {
                    _session.Abort("reconnect attempt timeout");
                    _attemptResolved = true;
                    _attemptSucceeded = false;
                }

                if (_attemptSucceeded)
                {
                    _reconnecting = false;
                    _reconnect = null;
                    yield break;
                }
            }

            _reconnecting = false;
            _reconnect = null;
            UIEventBus.Publish(new SessionEndedEvent { Reason = "reconnect_failed" });
            ReopenLoginWithMessage("reconnect failed");
        }
        private void StopReconnect()
        {
            if (_reconnect != null)
            {
                StopCoroutine(_reconnect);
                _reconnect = null;
            }
            _reconnecting = false;
        }

        private IEnumerator HeartbeatLoop()
        {
            long intervalMs = (long)(_heartbeatIntervalSeconds * 1000f);
            long timeoutMs = intervalMs * 3 + 2000;
            while (true)
            {
                yield return new WaitForSecondsRealtime(_heartbeatIntervalSeconds);
                if (_session == null) yield break;
                if (_session.HeartbeatRspAgeMs > timeoutMs)
                {
                    _session.Abort("心跳超时");
                    yield break;
                }
                _session.SendHeartbeat();
            }
        }

        private void OnApplicationQuit()
        {

            if (_session != null)
            {
                _session.Logout();
                _session.Dispose();
            }
        }
    }
}
