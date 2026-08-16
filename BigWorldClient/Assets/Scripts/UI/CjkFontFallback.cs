using TMPro;
using UnityEngine;

namespace BigWorldClient.UI
{
    /// <summary>
    /// Registers a CJK-capable dynamic font as a global TMP fallback so Chinese UI
    /// strings render instead of missing-glyph boxes. The default LiberationSans SDF
    /// font has no CJK glyphs. Requires Resources/Fonts/SimHei.ttf.
    /// </summary>
    public static class CjkFontFallback
    {
        private static bool registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRegister()
        {
            // Safety net in case the Game object isn't present; Game.Awake also calls Ensure().
            Ensure();
        }

        /// <summary>Idempotent. Call early in startup before any Chinese text renders.</summary>
        public static void Ensure()
        {
            if (registered) return;
            registered = true;

            var font = Resources.Load<Font>("Fonts/SimHei");
            if (font == null)
            {
                {}
                return;
            }

            // Creates a DYNAMIC font asset: glyphs are rasterized on demand at runtime,
            // so the whole CJK range works without pre-baking an atlas.
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
