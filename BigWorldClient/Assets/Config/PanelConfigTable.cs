using System;
using System.Collections.Generic;
using BigWorldClient.Network.Protocol;
using BigWorldClient.UI.Framework;
using Google.FlatBuffers;
using UnityEngine;

namespace BigWorldClient
{

    public sealed class PanelEntry
    {
        public string panelId;
        public string resourcePath;
        public UILayer layer;
        public bool cacheable;
        public int maxCacheCount;
        public bool closeByBack;
    }

    public sealed class PanelConfigTable
    {
        private static PanelConfigTable _cached;

        private readonly Dictionary<string, PanelEntry> _entries = new();

        public static PanelConfigTable Instance => _cached ??= Load();

        public PanelEntry GetEntry(string panelId)
        {
            return _entries.TryGetValue(panelId, out PanelEntry entry) ? entry : null;
        }

        public bool TryGetEntry(string panelId, out PanelEntry entry) => _entries.TryGetValue(panelId, out entry);

        public bool HasPanel(string panelId) => _entries.ContainsKey(panelId);

        static PanelConfigTable Load()
        {
            var table = new PanelConfigTable();
            TextAsset asset = Resources.Load<TextAsset>("Config/PanelConfig");
            if (asset == null)
            {
                Debug.LogError("[UI] Config/PanelConfig.bytes 不存在，请先在配置导出中心导出面板配置");
                return table;
            }

            PanelConfigMsg msg = PanelConfigMsg.GetRootAsPanelConfigMsg(new ByteBuffer(asset.bytes));
            for (int i = 0; i < msg.EntriesLength; i++)
            {
                var e = msg.Entries(i);
                if (!e.HasValue) continue;
                string id = e.Value.PanelId;
                if (string.IsNullOrEmpty(id)) continue;
                table._entries[id] = new PanelEntry
                {
                    panelId = id,
                    resourcePath = "UI/Panels/" + id,
                    layer = Enum.TryParse<UILayer>(e.Value.Layer, out UILayer layer) ? layer : UILayer.Normal,
                    cacheable = e.Value.Cacheable,
                    maxCacheCount = Math.Max(1, (int)e.Value.MaxCacheCount),
                    closeByBack = e.Value.CloseByBack,
                };
            }

            return table;
        }
    }
}
