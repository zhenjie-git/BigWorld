using UnityEngine;
using UnityEngine.SceneManagement;
using BigWorldClient.UI;
using BigWorldClient.UI.Framework;
using BigWorldClient.UI.Events;
using BigWorldClient.Network;

namespace BigWorldClient
{
    /// <summary>
    /// Application entry point. Coordinates the full startup flow:
    /// Show login → Validate password → Load MainCity → Spawn character.
    /// </summary>
    public class Game : MonoBehaviour
    {
        public static Game Instance { get; private set; }

        [Header("Config")]
        [SerializeField] private PlayerConfig playerConfig;

        [Header("Server")]
        [SerializeField, Tooltip("BigWorld login server host (phase 1)")]
        private string loginServerHost = "127.0.0.1";
        [SerializeField, Tooltip("BigWorld login server port (phase 1)")]
        private int loginServerPort = 9200;

        [Header("Scene & UI")]
        [SerializeField, Tooltip("Login scene. Loaded when the session ends (the persistent UI lives in the Main scene).")]
        private string entrySceneName = "Login";
        [SerializeField, Tooltip("Scene name for the main city")]
        private string mainCitySceneName = "MainCity";
        [SerializeField, Tooltip("Login panel ID registered in UIPanelRegistry")]
        private string loginPanelId = "login";

        [Header("Player Spawning")]
        [SerializeField, Tooltip("Player prefab path under Resources/ (used if no player in scene)")]
        private string playerPrefabPath = "Models/Robot";
        [SerializeField, Tooltip("If non-empty, spawn the player at this named GameObject's position")]
        private string spawnPointName = "SpawnPoint";

        // Single source of truth for the app state: null = at the login screen,
        // set = in a game scene (loading or loaded). Re-login is only valid when null.
        private string currentSceneId;

        private void Awake()
        {
            // Game persists across scenes (DontDestroyOnLoad). Reloading the entry
            // scene on logout would otherwise spawn a duplicate instance.
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            SceneMgr.Instance.Initialize(this);
            GameNetworkManager.EnsureInstance();
            CjkFontFallback.Ensure(); // CJK font fallback before any Chinese UI renders
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

        // ===== Event wiring =====

        private void SubscribeEvents()
        {
            UIEventBus.Subscribe<LoginAttemptEvent>(this, OnLoginAttempt);
            UIEventBus.Subscribe<LoginResultEvent>(this, OnLoginResult);
            UIEventBus.Subscribe<SceneLoadCompleteEvent>(this, OnSceneLoadComplete);
            UIEventBus.Subscribe<SessionEndedEvent>(this, OnSessionEnded);
        }

        private void UnsubscribeEvents()
        {
            UIEventBus.Unsubscribe<LoginAttemptEvent>(this);
            UIEventBus.Unsubscribe<LoginResultEvent>(this);
            UIEventBus.Unsubscribe<SceneLoadCompleteEvent>(this);
            UIEventBus.Unsubscribe<SessionEndedEvent>(this);
        }

        // ===== Login flow =====

        private void ShowLoginPanel()
        {
            if (UIManager.Instance != null)
            {
                UIManager.Instance.OpenPanel(loginPanelId);
                {}
            }
            else
            {
                {}
            }
        }

        private void OnLoginAttempt(LoginAttemptEvent evt)
        {
            // Already in (or loading) a scene — ignore; login is only valid at the login screen.
            if (currentSceneId != null) return;

            // Delegate to the real two-phase network login (login server -> token
            // -> gateway). The result arrives as a LoginResultEvent.
            GameNetworkManager.EnsureInstance().StartLogin(
                loginServerHost, loginServerPort, evt.Username, evt.Password);
        }

        private void OnLoginResult(LoginResultEvent evt)
        {
            // Failures are shown by the LoginViewModel and leave us at the login
            // screen (currentSceneId stays null) — nothing to do here.
            if (!evt.Success) return;

            if (currentSceneId != null) return; // ignore duplicate success events

            UIManager.Instance.HidePanel(loginPanelId);
            CreateGameScene();
        }

        private void CreateGameScene()
        {
            float maxStep = playerConfig != null ? playerConfig.VoxelMaxStepHeight : 0.5f;

            var scene = SceneMgr.Instance.CreateScene(mainCitySceneName, maxStep);
            currentSceneId = scene.SceneId;

            // GameScene loads voxel binary + Unity scene async
            scene.LoadAsync();
            {}
        }

        // ===== Scene load complete =====

        private void OnSceneLoadComplete(SceneLoadCompleteEvent evt)
        {
            if (evt.SceneName != mainCitySceneName) return;
            if (currentSceneId == null) return; // session ended before this scene finished loading

            {}
            SpawnPlayer();
        }

        // ===== Session end (return to entry) =====

        private void OnSessionEnded(SessionEndedEvent evt)
        {
            {}
            ReturnToEntry();
        }

        private void ReturnToEntry()
        {
            if (!string.IsNullOrEmpty(currentSceneId))
            {
                SceneMgr.Instance.RemoveScene(currentSceneId);
                currentSceneId = null;
            }

            SceneManager.LoadScene(entrySceneName, LoadSceneMode.Single);
            {}
        }

        // ===== Player spawning =====

        private void SpawnPlayer()
        {
            // Prefer an existing PlayerController already placed in the scene
            var existing = FindObjectOfType<PlayerController>();
            if (existing != null)
            {
                {}
                OnPlayerReady(existing);
                return;
            }

            // Otherwise, instantiate from Resources prefab
            var prefab = Resources.Load<GameObject>(playerPrefabPath);
            if (prefab == null)
            {
                {}
                return;
            }

            Vector3 spawnPos = Vector3.zero;
            Quaternion spawnRot = Quaternion.identity;

            // Server-authoritative spawn position (world X/Z); Y is corrected by voxel snap.
            if (GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.TryGetServerSpawn(out Vector3 serverPos))
            {
                spawnPos = serverPos;
                {}
            }
            else
            {
                // Fallback: use a named SpawnPoint if one exists in the scene
                var spawnPoint = GameObject.Find(spawnPointName);
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

        /// <summary>
        /// Called after the player is ready (either existing in scene or freshly spawned).
        /// Hook for any post-spawn initialization (camera setup, UI, etc.).
        /// </summary>
        private void OnPlayerReady(PlayerController controller)
        {
            // Bind the player to the current scene (voxel data lookup by sceneId)
            controller.Player.SceneId = currentSceneId;

            // Initialise the tick predictor with the server-authoritative spawn.
            if (GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.TryGetServerSpawn(out Vector3 serverPos))
            {
                controller.InitPrediction(serverPos.x, serverPos.z);
            }
            else
            {
                {}
            }

            // Assign the player's CameraLookPoint as the camera follow target
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
