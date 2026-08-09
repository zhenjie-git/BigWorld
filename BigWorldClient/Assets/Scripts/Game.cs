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
                Debug.Log("[Game] Login panel opened: " + loginPanelId);
            }
            else
            {
                Debug.LogError("[Game] UIManager is not available. Ensure UIManager.prefab exists at Resources/UI/.");
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
            Debug.Log("[Game] Login success, loading scene: " + mainCitySceneName);
        }

        // ===== Scene load complete =====

        private void OnSceneLoadComplete(SceneLoadCompleteEvent evt)
        {
            if (evt.SceneName != mainCitySceneName) return;
            if (currentSceneId == null) return; // session ended before this scene finished loading

            Debug.Log("[Game] MainCity scene loaded, spawning player...");
            SpawnPlayer();
        }

        // ===== Session end (return to entry) =====

        private void OnSessionEnded(SessionEndedEvent evt)
        {
            Debug.Log("[Game] Session ended (" + evt.Reason + "), returning to entry scene.");
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
            Debug.Log("[Game] Returned to entry scene: " + entrySceneName);
        }

        // ===== Player spawning =====

        private void SpawnPlayer()
        {
            // Prefer an existing PlayerController already placed in the scene
            var existing = FindObjectOfType<PlayerController>();
            if (existing != null)
            {
                Debug.Log("[Game] Using existing player in scene: " + existing.name);
                OnPlayerReady(existing);
                return;
            }

            // Otherwise, instantiate from Resources prefab
            var prefab = Resources.Load<GameObject>(playerPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[Game] Player prefab not found at Resources/" + playerPrefabPath);
                return;
            }

            Vector3 spawnPos = Vector3.zero;
            Quaternion spawnRot = Quaternion.identity;

            // Server-authoritative spawn position (world X/Z); Y is corrected by voxel snap.
            if (GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.TryGetServerSpawn(out Vector3 serverPos))
            {
                spawnPos = serverPos;
                Debug.Log("[Game] Spawning at server position: " + spawnPos);
            }
            else
            {
                // Fallback: use a named SpawnPoint if one exists in the scene
                var spawnPoint = GameObject.Find(spawnPointName);
                if (spawnPoint != null)
                {
                    spawnPos = spawnPoint.transform.position;
                    spawnRot = spawnPoint.transform.rotation;
                    Debug.Log("[Game] Spawn point found: " + spawnPointName + " at " + spawnPos);
                }
            }

            var playerGO = Instantiate(prefab, spawnPos, spawnRot);
            playerGO.name = "Player";

            var controller = playerGO.GetComponent<PlayerController>();
            if (controller != null)
            {
                Debug.Log("[Game] Player spawned from prefab at " + spawnPos);
                OnPlayerReady(controller);
            }
            else
            {
                Debug.LogError("[Game] Player prefab is missing PlayerController component!");
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

            // Assign the player's CameraLookPoint as the camera follow target
            var cameraController = FindObjectOfType<CameraController>();
            if (cameraController != null)
            {
                var cameraLookPoint = controller.transform.Find("CameraLookPoint");
                if (cameraLookPoint != null)
                {
                    cameraController.SetTarget(cameraLookPoint);
                    Debug.Log("[Game] Camera target assigned to Player/CameraLookPoint");
                }
                else
                {
                    Debug.LogWarning("[Game] CameraLookPoint not found under Player prefab, falling back to player root");
                    cameraController.SetTarget(controller.transform);
                }
            }

            Debug.Log("[Game] Player ready: " + controller.name);
        }
    }
}
