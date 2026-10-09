using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace BigWorldClient
{
    public class WorldHud : MonoBehaviour
    {
        public static WorldHud Instance { get; private set; }

        Text _text;
        float _nextUpdate;

        public static WorldHud EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[WorldHud]");
            DontDestroyOnLoad(go);
            return go.AddComponent<WorldHud>();
        }

        void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            var canvasGo = new GameObject("HudCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();
            canvasGo.GetComponent<GraphicRaycaster>().enabled = false;

            var textGo = new GameObject("HudText");
            textGo.transform.SetParent(canvasGo.transform, false);
            _text = textGo.AddComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (_text.font == null)
                _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = 22;
            _text.alignment = TextAnchor.UpperLeft;
            _text.color = new Color(1f, 1f, 1f, 0.85f);
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            var rect = _text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(16f, -16f);
            rect.sizeDelta = new Vector2(560f, 120f);
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            if (_text == null || Time.unscaledTime < _nextUpdate)
                return;
            _nextUpdate = Time.unscaledTime + 0.5f;
            Game game = Game.Instance;
            PlayerController player = game != null ? game.ActiveController : null;
            if (player == null || player.Player == null)
            {
                _text.text = "";
                return;
            }
            Vector3 pos = player.transform.position;
            string zone = "-";
            int template = player.Player.SceneTemplate;
            if (template != 0)
            {
                if (ZoneTable.TryZoneAt(template, pos.x, pos.z, out ZoneInfo info))
                    zone = info.Name;
                else if (ZoneTable.TryGetMap(template, out ZoneMap map))
                    zone = map.MapName;
            }
            string world = player.Player.WorldId;
            int remoteCount = RemotePlayerManager.Instance != null ? RemotePlayerManager.Instance.VisibleCount : 0;
            _text.text = $"Zone {zone}  World {world}\nPos ({pos.x:F1}, {pos.z:F1})\nRemote {remoteCount}";
        }
    }
}
