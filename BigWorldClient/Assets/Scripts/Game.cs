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
        [SerializeField, Tooltip("Player prefab path under Resources/ (used if no player in scene)")]
        private string _playerPrefabPath = "Models/Robot";
        [SerializeField, Tooltip("If non-empty, spawn the player at this named GameObject's position")]
        private string _spawnPointName = "SpawnPoint";

        private string _currentSceneId;
        private string _currentTemplateId;
        private const string DefaultTemplateId = "MainCity";

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
            UIEventBus.Subscribe<LoginResultEvent>(OnLoginResult);
            UIEventBus.Subscribe<SceneLoadCompleteEvent>(OnSceneLoadComplete);
            UIEventBus.Subscribe<SceneTransferEvent>(OnSceneTransfer);
            UIEventBus.Subscribe<SessionEndedEvent>(OnSessionEnded);
        }

        private void UnsubscribeEvents()
        {
            UIEventBus.Unsubscribe<LoginAttemptEvent>(OnLoginAttempt);
            UIEventBus.Unsubscribe<LoginResultEvent>(OnLoginResult);
            UIEventBus.Unsubscribe<SceneLoadCompleteEvent>(OnSceneLoadComplete);
            UIEventBus.Unsubscribe<SceneTransferEvent>(OnSceneTransfer);
            UIEventBus.Unsubscribe<SessionEndedEvent>(OnSessionEnded);
        }

        private void ShowLoginPanel()
        {
            if (UIManager.Instance != null)
                UIManager.Instance.OpenPanel(PanelIds.Login);
        }

        private void OnLoginAttempt(LoginAttemptEvent evt)
        {

            if (_currentSceneId != null) return;

            GameNetworkManager.EnsureInstance().StartLogin(
                _loginServerHost, _loginServerPort, evt.Username, evt.Password);
        }

        private void OnLoginResult(LoginResultEvent evt)
        {

            if (!evt.Success) return;

            if (_currentSceneId != null) return;

            UIManager.Instance.HidePanel(PanelIds.Login);
            CreateGameScene();
        }

        private void CreateGameScene()
        {
            float maxStep = PlayerConfigTable.Instance.VoxelMaxStepHeight;

            string templateId = DefaultTemplateId;
            if (GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.TryGetServerScene(out string serverScene))
                templateId = serverScene;

            _currentTemplateId = templateId;
            var scene = SceneMgr.Instance.CreateScene(templateId, maxStep);
            _currentSceneId = scene.SceneId;

            scene.LoadAsync();
        }

        private void OnSceneLoadComplete(SceneLoadCompleteEvent evt)
        {
            if (evt.SceneName != _currentTemplateId) return;
            if (_currentSceneId == null) return;

            SpawnPlayer();
        }

        private void OnSceneTransfer(SceneTransferEvent evt)
        {
            if (string.IsNullOrEmpty(evt.SceneId)) return;
            if (evt.SceneId == _currentTemplateId) return;

            if (!string.IsNullOrEmpty(_currentSceneId))
                SceneMgr.Instance.RemoveScene(_currentSceneId);

            _currentTemplateId = evt.SceneId;
            var scene = SceneMgr.Instance.CreateScene(evt.SceneId, PlayerConfigTable.Instance.VoxelMaxStepHeight);
            _currentSceneId = scene.SceneId;
            scene.LoadAsync();
        }

        private void OnSessionEnded(SessionEndedEvent evt)
        {
            {}
            ReturnToEntry();
        }

        private void ReturnToEntry()
        {
            if (!string.IsNullOrEmpty(_currentSceneId))
            {
                SceneMgr.Instance.RemoveScene(_currentSceneId);
                _currentSceneId = null;
                _currentTemplateId = null;
            }

            SceneManager.LoadScene(_entrySceneName, LoadSceneMode.Single);
        }

        private void SpawnPlayer()
        {

            var existing = FindObjectOfType<PlayerController>();
            if (existing != null)
            {
                {}
                OnPlayerReady(existing);
                return;
            }

            var prefab = Resources.Load<GameObject>(_playerPrefabPath);
            if (prefab == null)
            {
                {}
                return;
            }

            Vector3 spawnPos = Vector3.zero;
            Quaternion spawnRot = Quaternion.identity;

            if (GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.TryGetServerSpawn(out Vector3 serverPos))
            {
                spawnPos = serverPos;
                {}
            }
            else
            {

                var spawnPoint = GameObject.Find(_spawnPointName);
                if (spawnPoint != null)
                {
                    spawnPos = spawnPoint.transform.position;
                    spawnRot = spawnPoint.transform.rotation;
                    {}
                }
            }

            var playerGO = Instantiate(prefab, spawnPos, spawnRot);
            playerGO.name = "Player";

            var controller = playerGO.GetComponent<PlayerController>();
            if (controller != null)
            {
                {}
                OnPlayerReady(controller);
            }
            else
            {
                {}
            }
        }

        private void OnPlayerReady(PlayerController controller)
        {

            controller.Player.SceneId = _currentSceneId;

            if (GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.TryGetServerSpawn(out Vector3 serverPos))
            {
                controller.InitPrediction(serverPos.x, serverPos.z);
            }
            else
            {
                {}
            }

            var cameraController = FindObjectOfType<CameraController>();
            if (cameraController != null)
            {
                var cameraLookPoint = controller.transform.Find("CameraLookPoint");
                if (cameraLookPoint != null)
                {
                    cameraController.SetTarget(cameraLookPoint);
                    {}
                }
                else
                {
                    {}
                    cameraController.SetTarget(controller.transform);
                }
            }

            {}
        }
    }
}
