using System.Collections.Generic;
using BigWorldClient.Network;
using UnityEngine;

namespace BigWorldClient
{
    public class RemotePlayerManager : MonoBehaviour
    {
        public static RemotePlayerManager Instance { get; private set; }

        const float LeaveGraceSeconds = 0.6f;
        const string PlayerPrefabPath = "Models/Robot";

        readonly Dictionary<ulong, RemoteEntityView> _views = new();
        readonly Dictionary<ulong, float> _leaveAt = new();
        GameObject _prefab;

        public int VisibleCount => _views.Count;

        public static RemotePlayerManager EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[RemotePlayerManager]");
            DontDestroyOnLoad(go);
            return go.AddComponent<RemotePlayerManager>();
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
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void HandleEvent(RemoteEntityEvent evt)
        {
            switch (evt.Kind)
            {
                case RemoteEntityEventKind.Enter:
                    _leaveAt.Remove(evt.PlayerId);
                    if (_views.TryGetValue(evt.PlayerId, out RemoteEntityView existing))
                    {
                        existing.ApplySnapshot(in evt);
                        return;
                    }
                    CreateView(in evt);
                    break;
                case RemoteEntityEventKind.Leave:
                    _leaveAt[evt.PlayerId] = Time.time + LeaveGraceSeconds;
                    break;
                case RemoteEntityEventKind.MoveEvent:
                    if (_views.TryGetValue(evt.PlayerId, out RemoteEntityView mover))
                        mover.ApplyMoveEvent(in evt);
                    break;
                case RemoteEntityEventKind.Snapshot:
                    if (_views.TryGetValue(evt.PlayerId, out RemoteEntityView target))
                        target.ApplySnapshot(in evt);
                    break;
            }
        }

        void CreateView(in RemoteEntityEvent enter)
        {
            PlayerController local = Game.Instance != null ? Game.Instance.ActiveController : null;
            if (local == null || local.SimContext == null)
                return;
            GameNetworkManager mgr = GameNetworkManager.Instance;
            if (mgr == null || mgr.Clock == null)
                return;
            if (_prefab == null)
            {
                _prefab = Resources.Load<GameObject>(PlayerPrefabPath);
                if (_prefab == null)
                    return;
            }
            Vector3 pos = new Vector3((float)enter.X, (float)enter.Y, (float)enter.Z);
            GameObject go = Instantiate(_prefab, pos, Quaternion.identity);
            go.name = $"RemotePlayer_{enter.PlayerId}";
            foreach (MonoBehaviour behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is PlayerController || behaviour is PlayerInput || behaviour is RemoteEntityView)
                    Destroy(behaviour);
            }
            if (go.TryGetComponent(out Rigidbody rb))
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            foreach (Collider col in go.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            var view = go.AddComponent<RemoteEntityView>();
            view.Init(in enter, local.SimContext, mgr.Clock);
            string label = string.IsNullOrEmpty(enter.Account) ? $"P{enter.PlayerId}" : enter.Account;
            view.SetLabel(label);
            _views[enter.PlayerId] = view;
        }

        public void ClearAll()
        {
            foreach (KeyValuePair<ulong, RemoteEntityView> kv in _views)
            {
                if (kv.Value != null)
                    Destroy(kv.Value.gameObject);
            }
            _views.Clear();
            _leaveAt.Clear();
        }

        void Update()
        {
            if (_leaveAt.Count == 0) return;
            List<ulong> expired = null;
            foreach (KeyValuePair<ulong, float> kv in _leaveAt)
            {
                if (Time.time < kv.Value) continue;
                expired ??= new List<ulong>();
                expired.Add(kv.Key);
            }
            if (expired == null) return;
            foreach (ulong id in expired)
            {
                _leaveAt.Remove(id);
                if (_views.TryGetValue(id, out RemoteEntityView view))
                {
                    if (view != null)
                        Destroy(view.gameObject);
                    _views.Remove(id);
                }
            }
        }
    }
}
