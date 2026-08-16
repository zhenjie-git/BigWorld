using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient.UI.Events
{
    public static class UIEventBus
    {
        private static readonly Dictionary<Type, List<Subscription>> subscriptions
            = new Dictionary<Type, List<Subscription>>();
        private static readonly object lockObj = new object();

        public static void Subscribe<T>(object listener, Action<T> handler)
        {
            if (listener == null || handler == null) return;
            lock (lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.ContainsKey(type))
                    subscriptions[type] = new List<Subscription>();
                subscriptions[type].Add(new Subscription(listener, handler));
            }
        }

        public static void Unsubscribe<T>(object listener)
        {
            if (listener == null) return;
            lock (lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.TryGetValue(type, out var list)) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!list[i].IsAlive || list[i].Owner == listener)
                        list.RemoveAt(i);
                }
            }
        }

        public static void Publish<T>(T eventData)
        {
            List<Subscription> handlers = null;
            lock (lockObj)
            {
                var type = typeof(T);
                if (!subscriptions.TryGetValue(type, out var list)) return;
                handlers = new List<Subscription>(list);
            }

            bool needsCleanup = false;
            foreach (var sub in handlers)
            {
                if (sub.IsAlive)
                {
                    var handler = sub.GetHandler<T>();
                    if (handler != null)
                    {
                        try { handler(eventData); }
                        catch (Exception) { }
                    }
                }
                else { needsCleanup = true; }
            }
            if (needsCleanup) Cleanup<T>();
        }

        public static void Clear()
        {
            lock (lockObj) { subscriptions.Clear(); }
        }

        private static void Cleanup<T>()
        {
            lock (lockObj)
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
            private readonly WeakReference ownerRef;
            private readonly Delegate handler;

            public object Owner
            {
                get
                {
                    try { return ownerRef.Target; }
                    catch { return null; }
                }
            }

            public bool IsAlive
            {
                get
                {
                    try { return ownerRef.IsAlive && ownerRef.Target != null; }
                    catch { return false; }
                }
            }

            public Subscription(object owner, Delegate handler)
            {
                this.ownerRef = new WeakReference(owner);
                this.handler = handler;
            }

            public Action<T> GetHandler<T>() { return IsAlive ? handler as Action<T> : null; }
        }
    }
}
