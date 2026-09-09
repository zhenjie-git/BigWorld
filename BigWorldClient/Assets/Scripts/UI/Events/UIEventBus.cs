using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BigWorldClient.UI.Events
{
    public static class UIEventBus
    {
        private static readonly Dictionary<Type, List<Subscription>> subscriptions
            = new Dictionary<Type, List<Subscription>>();
        private static readonly object _lockObj = new object();

        public static void Subscribe<T>(Action<T> handler)
        {
            if (handler == null) return;
            lock (_lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.TryGetValue(type, out var list))
                    subscriptions[type] = list = new List<Subscription>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Matches(handler)) return;
                }
                list.Add(new Subscription(handler));
            }
        }

        public static void Unsubscribe<T>(Action<T> handler)
        {
            if (handler == null) return;
            lock (_lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.TryGetValue(type, out var list)) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Matches(handler)) list.RemoveAt(i);
                }
            }
        }

        public static void UnsubscribeTarget(object target)
        {
            if (target == null) return;
            lock (_lockObj)
            {
                foreach (var list in subscriptions.Values)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].OwnedBy(target)) list.RemoveAt(i);
                    }
                }
            }
        }

        public static void Publish<T>(T eventData)
        {
            Subscription[] handlers = null;
            lock (_lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.TryGetValue(type, out var list) || list.Count == 0) return;
                handlers = list.ToArray();
            }

            bool needsCleanup = false;
            foreach (var sub in handlers)
            {
                var handler = sub.GetHandler<T>();
                if (handler == null)
                {
                    needsCleanup = true;
                    continue;
                }
                try { handler(eventData); }
                catch (Exception e) { Debug.LogException(e); }
            }
            if (needsCleanup) Cleanup<T>();
        }

        public static void Clear()
        {
            lock (_lockObj) { subscriptions.Clear(); }
        }

        private static void Cleanup<T>()
        {
            lock (_lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.TryGetValue(type, out var list)) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!list[i].IsAlive) list.RemoveAt(i);
                }
                if (list.Count == 0) subscriptions.Remove(type);
            }
        }

        private class Subscription
        {
            private readonly WeakReference _targetRef;
            private readonly MethodInfo _method;

            public Subscription(Delegate handler)
            {
                _method = handler.Method;
                _targetRef = handler.Target == null ? null : new WeakReference(handler.Target);
            }

            public bool IsAlive
            {
                get { return _method.IsStatic || (_targetRef != null && _targetRef.IsAlive); }
            }

            public bool Matches(Delegate handler)
            {
                if (_method != handler.Method) return false;
                var target = _targetRef != null ? _targetRef.Target : null;
                return ReferenceEquals(handler.Target, target);
            }

            public bool OwnedBy(object target)
            {
                return _targetRef != null && ReferenceEquals(_targetRef.Target, target);
            }

            public Action<T> GetHandler<T>()
            {
                if (_method.IsStatic)
                    return _method.CreateDelegate(typeof(Action<T>)) as Action<T>;
                var target = _targetRef != null ? _targetRef.Target : null;
                if (target == null) return null;
                return _method.CreateDelegate(typeof(Action<T>), target) as Action<T>;
            }
        }
    }
}
