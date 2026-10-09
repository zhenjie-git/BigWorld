using UnityEngine;
using UnityEngine.SceneManagement;
using BigWorldClient.UI;
using BigWorldClient.UI.Framework;
using BigWorldClient.UI.Events;
using BigWorldClient.Network;

namespace BigWorldClient
{

    public class Game : MonoBehaviour
    {
        public static Game Instance { get; private set; }

        [Header("Server")]
        [SerializeField, Tooltip("BigWorld login server host (phase 1)")]
        private string _loginServerHost = "127.0.0.1";
        [SerializeField, Tooltip("BigWorld login server port (phase 1)")]
        private int _loginServerPort = 9200;

        [Header("Scene & UI")]
        [SerializeField, Tooltip("Login scene. Loaded when the session ends (the persistent UI lives in the Main scene).")]
        private string _entrySceneName = "Login";

        [Header("Player Spawning")]
        [SerializeField, Tooltip("Player prefab path under Resources/")]
        private string _playerPrefabPath = "Models/Robot";

        private GameScene _currentScene;
        private string _currentWorldId;

        public PlayerController ActiveController { get; private set; }

        private bool SceneReady => _currentScene != null && _currentScene.Loaded;

        private void Awake()
        {

            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            SceneMgr.Instance.Initialize(this);
            GameNetworkManager.EnsureInstance();
            CjkFontFallback.Ensure();
            SubscribeEvents();
        }

        private void Start()
        {
            ShowLoginPanel();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                UnsubscribeEvents();
                Instance = null;
            }
        }

        private void SubscribeEvents()
        {
            UIEventBus.Subscribe<LoginAttemptEvent>(OnLoginAttempt);
            UIEventBus.Subscribe<SessionEndedEvent>(OnSessionEnded);
        }

        private void UnsubscribeEvents()
        {
            UIEventBus.Unsubscribe<LoginAttemptEvent>(OnLoginAttempt);
            UIEventBus.Unsubscribe<SessionEndedEvent>(OnSessionEnded);
        }

        private void ShowLoginPanel()
        {
            if (UIManager.Instance != null)
                UIManager.Instance.OpenPanel(PanelIds.Login);
        }

        private void OnLoginAttempt(LoginAttemptEvent evt)
        {

            if (SceneReady) return;

            GameNetworkManager.EnsureInstance().StartLogin(
                _loginServerHost, _loginServerPort, evt.Username, evt.Password);
        }

        public void HandleLoginSucceeded(PlayerInfo player)
        {
            if (!SceneReady)
            {
                UIManager.Instance.HidePanel(PanelIds.Login);
                HandleSceneTransfer(player);
                return;
            }

            ApplyWorldId(player.WorldId);
            if (new SceneID(player.SceneId).Template() != _currentScene.Template)
                HandleSceneTransfer(player);
        }

        public void HandleSceneTransfer(PlayerInfo player)
        {
            var sceneID = new SceneID(player.SceneId);
            if (!sceneID.Valid())
            {
                Debug.LogError($"[Game] invalid server scene id {sceneID.Value}");
                return;
            }

            bool worldChanged = !string.IsNullOrEmpty(_currentWorldId) && player.WorldId != _currentWorldId;
            ApplyWorldId(player.WorldId);

            if (_currentScene != null && sceneID.Template() == _currentScene.Template && player.Seamless)
            {
                if (worldChanged)
                    RemotePlayerManager.Instance?.ClearAll();
                _currentScene.UpdateCenterZone(sceneID);
                ActiveController?.Predictor?.ResendPending();
                return;
            }

            if (ActiveController != null)
                ActiveController.gameObject.SetActive(false);

            if (_currentScene != null)
            {
                _currentScene.UnloadZones();
                SceneMgr.Instance.RemoveScene(_currentScene.Template);
                _currentScene = null;
            }

            Vector3 serverSpawn = new Vector3((float)player.X, 0f, (float)player.Z);
            _currentScene = SceneMgr.Instance.CreateScene(sceneID, serverSpawn, PlayerConfigTable.Instance.VoxelMaxStepHeight);
            RemotePlayerManager.Instance?.ClearAll();
            _currentScene.PlayerPrefabPath = _playerPrefabPath;
            _currentScene.SceneEntered = OnSceneEntered;
            _currentScene.LoadAsync();
        }

        private void OnSceneEntered(GameScene scene, PlayerController controller)
        {
            if (controller == null) return;

            ActiveController = controller;
            RemotePlayerManager.EnsureInstance();
            WorldHud.EnsureInstance();
            scene.ActivatePlayer(controller, _currentWorldId);
        }

        private void ApplyWorldId(string worldId)
        {
            _currentWorldId = worldId;
            if (ActiveController != null)
                ActiveController.Player.WorldId = worldId;
        }

        private void OnSessionEnded(SessionEndedEvent evt)
        {
            ReturnToEntry();
        }

        private void Update()
        {
            if (ActiveController == null) return;
            _currentScene?.UpdateCenterFromPosition(
                ActiveController.transform.position.x, ActiveController.transform.position.z);
        }

        private void ReturnToEntry()
        {
            if (_currentScene != null)
            {
                _currentScene.UnloadZones();
                SceneMgr.Instance.RemoveScene(_currentScene.Template);
                _currentScene = null;
            }
            _currentWorldId = null;
            RemotePlayerManager.Instance?.ClearAll();

            SceneManager.LoadScene(_entrySceneName, LoadSceneMode.Single);
        }

    }
}
