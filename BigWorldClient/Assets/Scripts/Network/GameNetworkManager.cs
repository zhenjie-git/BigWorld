using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using BigWorldClient.UI.Events;
using BigWorldClient.UI.Framework;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient.Network
{
    /// <summary>
    /// MonoBehaviour glue between the pure-C# GameSession and the Unity UI.
    /// Drains SessionEvents on the main thread, publishes UI events, runs the
    /// 5s heartbeat, and exposes Login/Logout. EnsureInstance() creates the
    /// singleton without requiring a scene edit.
    /// </summary>
    public class GameNetworkManager : MonoBehaviour
    {
        public static GameNetworkManager Instance { get; private set; }

        [Header("Session")]
        [SerializeField] private float heartbeatIntervalSeconds = 5f;
        [SerializeField, Tooltip("Login panel ID registered in UIPanelRegistry")]
        private string loginPanelId = "login";

        private GameSession _session;
        private Coroutine _heartbeat;
        private InputAction _logoutAction;
        private bool _syncLogged;
        private Vector3 _serverSpawnPos;
        private bool _hasServerSpawn;

        /// <summary>Server-clock estimate; null until a session exists.</summary>
        public ServerClock Clock => _session?.Clock;

        /// <summary>Authoritative spawn position from the enter-scene notify.</summary>
        public bool TryGetServerSpawn(out Vector3 pos)
        {
            pos = _serverSpawnPos;
            return _hasServerSpawn;
        }

        public bool SendWalkStart(float dirX, float dirZ) => _session != null && _session.SendWalkStart(dirX, dirZ);
        public bool SendRunStart(float dirX, float dirZ) => _session != null && _session.SendRunStart(dirX, dirZ);
        public bool SendSprintStart(float dirX, float dirZ) => _session != null && _session.SendSprintStart(dirX, dirZ);
        public bool SendJumpStart(float dirX, float dirZ) => _session != null && _session.SendJumpStart(dirX, dirZ);
        public bool SendDashStart(float dirX, float dirZ) => _session != null && _session.SendDashStart(dirX, dirZ);
        public bool SendRollStart(float dirX, float dirZ) => _session != null && _session.SendRollStart(dirX, dirZ);
        public bool SendStopStart(MoveState stopKind) => _session != null && _session.SendStopStart(stopKind);
        public bool SendMoveStop() => _session != null && _session.SendMoveStop();
        public bool SendMoveDirChange(float dirX, float dirZ) => _session != null && _session.SendMoveDirChange(dirX, dirZ);

        public bool SendWalkStartAt(float dirX, float dirZ, long tick) => _session != null && _session.SendWalkStartAt(dirX, dirZ, tick);
        public bool SendRunStartAt(float dirX, float dirZ, long tick) => _session != null && _session.SendRunStartAt(dirX, dirZ, tick);
        public bool SendSprintStartAt(float dirX, float dirZ, long tick) => _session != null && _session.SendSprintStartAt(dirX, dirZ, tick);
        public bool SendJumpStartAt(float dirX, float dirZ, long tick) => _session != null && _session.SendJumpStartAt(dirX, dirZ, tick);
        public bool SendDashStartAt(float dirX, float dirZ, long tick) => _session != null && _session.SendDashStartAt(dirX, dirZ, tick);
        public bool SendRollStartAt(float dirX, float dirZ, long tick) => _session != null && _session.SendRollStartAt(dirX, dirZ, tick);
        public bool SendStopStartAt(MoveState stopKind, long tick) => _session != null && _session.SendStopStartAt(stopKind, tick);
        public bool SendMoveStopAt(long tick) => _session != null && _session.SendMoveStopAt(tick);
        public bool SendMoveDirChangeAt(float dirX, float dirZ, long tick) => _session != null && _session.SendMoveDirChangeAt(dirX, dirZ, tick);

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

            // F10 logs out back to the login panel.
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

            LogFirstSync();
        }

        /// <summary>Logs the first successful clock sync once (visible in the Unity console).</summary>
        private void LogFirstSync()
        {
            if (_session == null || _syncLogged) return;
            var clock = _session.Clock;
            if (!clock.IsSynced) return;
            _syncLogged = true;
            {}
        }

        // ===== Public API =====

        public void StartLogin(string host, int port, string username, string password)
        {
            if (_session == null) return;
            StopHeartbeat();
            _session.BeginLogin(host, port, username, password);
        }

        public void Logout()
        {
            if (_session == null) return;
            StopHeartbeat();
            _session.Logout();
        }

        // ===== Session events =====

        private void HandleSessionEvent(SessionEvent evt)
        {
            switch (evt.Kind)
            {
                case SessionEventKind.LoginSucceeded:
                    StartHeartbeat();
                    _serverSpawnPos = new Vector3((float)evt.Player.X, 0f, (float)evt.Player.Z);
                    _hasServerSpawn = true;
                    UIEventBus.Publish(new LoginResultEvent { Success = true, Message = "登录成功" });
                    break;

                case SessionEventKind.LoginFailed:
                    StopHeartbeat();
                    // Panel is already open and its ViewModel is subscribed -> shows the error.
                    UIEventBus.Publish(new LoginResultEvent { Success = false, Message = evt.Message });
                    break;

                case SessionEventKind.ServerShutdown:
                    StopHeartbeat();
                    // Game returns to the entry scene; then we reopen the login panel.
                    UIEventBus.Publish(new SessionEndedEvent { Reason = "server_shutdown" });
                    StartCoroutine(ReopenLoginWithMessage("服务器已关闭: " + evt.Message));
                    break;

                case SessionEventKind.Disconnected:
                    StopHeartbeat();
                    UIEventBus.Publish(new SessionEndedEvent { Reason = "disconnected" });
                    StartCoroutine(ReopenLoginWithMessage("与服务器连接断开"));
                    break;

                case SessionEventKind.LoggedOut:
                    StopHeartbeat();
                    UIEventBus.Publish(new SessionEndedEvent { Reason = "logout" });
                    StartCoroutine(ReopenLoginWithMessage("已退出登录"));
                    break;
            }
        }

        /// <summary>
        /// Reopens the login panel and then publishes the message. The panel's
        /// OnShow clears its status text, so the message must be published AFTER
        /// the panel is shown or it would be wiped.
        /// </summary>
        private IEnumerator ReopenLoginWithMessage(string message)
        {
            if (UIManager.Instance != null) UIManager.Instance.OpenPanel(loginPanelId);
            yield return null; // let the (cached) panel run OnShow
            yield return null;
            UIEventBus.Publish(new LoginResultEvent { Success = false, Message = message });
        }

        // ===== Heartbeat =====

        private void StartHeartbeat()
        {
            if (_heartbeat != null) return;
            _syncLogged = false;
            _session?.SendHeartbeat(); // prime the clock now instead of waiting a full interval
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

        private IEnumerator HeartbeatLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(heartbeatIntervalSeconds);
                _session?.SendHeartbeat();
            }
        }

        // ===== Shutdown =====

        private void OnApplicationQuit()
        {
            // Best-effort logout before the process exits (may not flush, but try).
            if (_session != null)
            {
                _session.Logout();
                _session.Dispose();
            }
        }
    }
}
