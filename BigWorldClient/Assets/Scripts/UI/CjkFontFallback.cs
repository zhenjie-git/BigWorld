using TMPro;
using UnityEngine;

namespace BigWorldClient.UI
{

    public static class CjkFontFallback
    {
        private static bool _registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRegister()
        {

            Ensure();
        }

        public static void Ensure()
        {
            if (_registered) return;
            _registered = true;

            var font = Resources.Load<Font>("Fonts/SimHei");
            if (font == null)
            {
                {}
                return;
            }

            var cjkFontAsset = TMP_FontAsset.CreateFontAsset(font);
            if (cjkFontAsset == null)
            {
                {}
                return;
            }

            var fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null || fallbacks.Contains(cjkFontAsset)) return;

            fallbacks.Add(cjkFontAsset);
            {}
        }
    }
}
