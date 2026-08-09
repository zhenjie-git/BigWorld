using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigWorldClient.UI.Framework
{
    [CreateAssetMenu(fileName = "PanelRegistry", menuName = "UI/Panel Registry")]
    public class UIPanelRegistry : ScriptableObject
    {
        public List<PanelEntry> entries = new List<PanelEntry>();

        private Dictionary<string, PanelEntry> lookup;

        public void BuildLookup()
        {
            lookup = new Dictionary<string, PanelEntry>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.panelId))
                {
                    Debug.LogWarning("[UIPanelRegistry] Skipping entry with empty panelId");
                    continue;
                }
                if (lookup.ContainsKey(entry.panelId))
                    Debug.LogWarning("[UIPanelRegistry] Duplicate panelId: " + entry.panelId);
                lookup[entry.panelId] = entry;
            }
        }

        public PanelEntry GetEntry(string panelId)
        {
            if (lookup == null) BuildLookup();
            lookup.TryGetValue(panelId, out var entry);
            return entry;
        }

        public bool HasPanel(string panelId)
        {
            if (lookup == null) BuildLookup();
            return lookup.ContainsKey(panelId);
        }

        private void OnEnable()
        {
            BuildLookup();
        }
    }

    [Serializable]
    public class PanelEntry
    {
        [Tooltip("Unique identifier for opening this panel via UIManager")]
        public string panelId;
        [Tooltip("Path under Resources/ folder, e.g. 'UI/Panels/LoginPanel'")]
        public string resourcePath;
        [Tooltip("Which layer this panel renders on")]
        public UILayer layer = UILayer.Normal;
        [Tooltip("Sorting priority within the layer")]
        public PanelPriority priority = PanelPriority.Medium;
        [Tooltip("Cache this panel when hidden for faster re-opening")]
        public bool cacheable = true;
        [Tooltip("Max cached instances")]
        public int maxCacheCount = 1;
    }
}
