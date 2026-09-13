using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BigWorldClient.UI.Framework
{
    public static class UiSkin
    {
        public static readonly Color BackgroundTop = Hex("#080B18");
        public static readonly Color BackgroundBottom = Hex("#18132E");
        public static readonly Color TextPrimary = Hex("#F4F7FF");
        public static readonly Color TextSecondary = Hex("#A7B0CC");
        public static readonly Color TextMuted = Hex("#68708E");
        public static readonly Color AccentCyan = Hex("#55D6FF");
        public static readonly Color AccentViolet = Hex("#8B7CFF");
        public static readonly Color AccentWarm = Hex("#FFB86B");
        public static readonly Color ErrorColor = Hex("#FF6B7A");
        public static readonly Color SuccessColor = Hex("#5EE6A8");
        public static readonly Color CardColor = Hex("#101728");
        public static readonly Color InputColor = Hex("#0A0F21");

        static Sprite _backgroundGradient;
        static Sprite _cardPanel;
        static Sprite _cardOutline;
        static Sprite _inputPanel;
        static Sprite _inputOutline;
        static Sprite _buttonPanel;
        static Sprite _accentStripe;
        static Sprite _glow;
        static Sprite _ring;
        static Sprite _arc;
        static Sprite _dot;
        static Sprite _vignette;
        static Sprite _white;
        static Sprite _progressBar;

        public static Sprite BackgroundGradient
        {
            get
            {
                if (_backgroundGradient == null) _backgroundGradient = BuildVerticalGradient(32, 256, BackgroundBottom, BackgroundTop);
                return _backgroundGradient;
            }
        }

        static Sprite CardPanel
        {
            get
            {
                if (_cardPanel == null) _cardPanel = BuildRoundedRect(96, 96, 26, Color.white, Color.white, false);
                return _cardPanel;
            }
        }

        static Sprite CardOutline
        {
            get
            {
                if (_cardOutline == null) _cardOutline = BuildOutline(96, 26, 1.5f);
                return _cardOutline;
            }
        }

        static Sprite InputPanel
        {
            get
            {
                if (_inputPanel == null) _inputPanel = BuildRoundedRect(64, 64, 18, Color.white, Color.white, false);
                return _inputPanel;
            }
        }

        static Sprite InputOutline
        {
            get
            {
                if (_inputOutline == null) _inputOutline = BuildOutline(64, 18, 1.5f);
                return _inputOutline;
            }
        }

        static Sprite ButtonPanel
        {
            get
            {
                if (_buttonPanel == null) _buttonPanel = BuildRoundedRect(400, 64, 16, AccentCyan, AccentViolet, true);
                return _buttonPanel;
            }
        }

        static Sprite AccentStripe
        {
            get
            {
                if (_accentStripe == null) _accentStripe = BuildRoundedRect(128, 8, 4, AccentViolet, AccentCyan, true);
                return _accentStripe;
            }
        }

        static Sprite Glow
        {
            get
            {
                if (_glow == null) _glow = BuildRadialGlow(256, 2.2f);
                return _glow;
            }
        }

        static Sprite Ring
        {
            get
            {
                if (_ring == null) _ring = BuildRing(128, 10f, 360f);
                return _ring;
            }
        }

        static Sprite Arc
        {
            get
            {
                if (_arc == null) _arc = BuildRing(128, 10f, 272f);
                return _arc;
            }
        }

        static Sprite Dot
        {
            get
            {
                if (_dot == null) _dot = BuildRadialGlow(24, 1.6f);
                return _dot;
            }
        }

        static Sprite VignetteSprite
        {
            get
            {
                if (_vignette == null) _vignette = BuildVignette(256);
                return _vignette;
            }
        }

        static Sprite WhiteSprite
        {
            get
            {
                if (_white == null) _white = BuildRoundedRect(4, 4, 0, Color.white, Color.white, false);
                return _white;
            }
        }

        static Sprite ProgressBarSprite
        {
            get
            {
                if (_progressBar == null) _progressBar = BuildRoundedRect(128, 16, 8, Color.white, Color.white, false);
                return _progressBar;
            }
        }

        public static void ApplyLogin(Transform root, TMP_InputField username, TMP_InputField password, Button loginButton, TMP_Text statusText)
        {
            if (root == null) return;

            TMP_Text title = FindText(root, "LoginPanel/Title");
            TMP_FontAsset font = title != null ? title.font : null;
            if (font == null && statusText != null) font = statusText.font;

            Image background = FindImage(root, "Background");
            if (background != null)
            {
                background.sprite = BackgroundGradient;
                background.type = Image.Type.Simple;
                background.color = Color.white;
                Stretch(background.rectTransform);
                background.transform.SetAsFirstSibling();
            }

            Image vignette = AddImage(root, "Vignette", VignetteSprite, WithAlpha(Color.black, 0.55f));
            Stretch(vignette.rectTransform);

            Image glowNorthWest = AddImage(root, "GlowNorthWest", Glow, WithAlpha(AccentCyan, 0.16f));
            SetRect(glowNorthWest.rectTransform, new Vector2(0f, 1f), new Vector2(760f, 760f), new Vector2(180f, -180f));

            Image glowSouthEast = AddImage(root, "GlowSouthEast", Glow, WithAlpha(AccentViolet, 0.20f));
            SetRect(glowSouthEast.rectTransform, new Vector2(1f, 0f), new Vector2(820f, 820f), new Vector2(-220f, 160f));

            Image glowCenter = AddImage(root, "GlowCenter", Glow, WithAlpha(AccentWarm, 0.07f));
            SetRect(glowCenter.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(560f, 560f), new Vector2(210f, 120f));

            RectTransform card = root.Find("LoginPanel") as RectTransform;
            if (card == null) return;

            Image cardImage = card.GetComponent<Image>();
            if (cardImage != null)
            {
                cardImage.sprite = CardPanel;
                cardImage.type = Image.Type.Sliced;
                cardImage.color = WithAlpha(CardColor, 0.965f);
                cardImage.raycastTarget = false;
            }
            SetRect(card, new Vector2(0.5f, 0.5f), new Vector2(460f, 560f), Vector2.zero);

            Image shadow = AddImage(root, "CardShadow", CardPanel, WithAlpha(Color.black, 0.42f));
            SetRect(shadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(478f, 578f), new Vector2(0f, -16f));
            card.SetAsLastSibling();

            Image outline = AddImage(card, "CardOutline", CardOutline, WithAlpha(TextPrimary, 0.10f));
            Stretch(outline.rectTransform);
            outline.type = Image.Type.Sliced;
            outline.transform.SetAsFirstSibling();

            Image accent = AddImage(card, "CardAccent", AccentStripe, Color.white);
            accent.type = Image.Type.Simple;
            SetRect(accent.rectTransform, new Vector2(0.5f, 1f), new Vector2(112f, 5f), new Vector2(0f, -28f));

            Image divider = AddImage(card, "Divider", WhiteSprite, WithAlpha(TextPrimary, 0.06f));
            SetRect(divider.rectTransform, new Vector2(0.5f, 1f), new Vector2(352f, 1f), new Vector2(0f, -146f));

            StyleText(title, "WELCOME BACK", 34f, TextPrimary, TextAlignmentOptions.Center, true, 1.2f, font);
            SetRect(title != null ? title.rectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 52f), new Vector2(0f, -66f));

            TMP_Text subtitle = FindText(card, "Subtitle");
            StyleText(subtitle, "Sign in to continue your adventure", 14f, TextSecondary, TextAlignmentOptions.Center, false, 0.4f, font);
            SetRect(subtitle != null ? subtitle.rectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 24f), new Vector2(0f, -114f));

            TMP_Text usernameLabel = FindText(card, "UsernameLabel");
            StyleText(usernameLabel, "USERNAME", 12f, WithAlpha(AccentCyan, 0.86f), TextAlignmentOptions.Left, false, 7f, font);
            SetRect(usernameLabel != null ? usernameLabel.rectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 20f), new Vector2(0f, -180f));

            TMP_Text passwordLabel = FindText(card, "PasswordLabel");
            StyleText(passwordLabel, "PASSWORD", 12f, WithAlpha(AccentCyan, 0.86f), TextAlignmentOptions.Left, false, 7f, font);
            SetRect(passwordLabel != null ? passwordLabel.rectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 20f), new Vector2(0f, -280f));

            SetRect(username != null ? username.transform as RectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 54f), new Vector2(0f, -210f));
            SetRect(password != null ? password.transform as RectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 54f), new Vector2(0f, -310f));
            StyleInput(username, "Enter username", font);
            StyleInput(password, "Enter password", font);

            SetRect(loginButton != null ? loginButton.transform as RectTransform : null, new Vector2(0.5f, 1f), new Vector2(400f, 54f), new Vector2(0f, -410f));
            StyleButton(loginButton, font);

            if (statusText != null)
            {
                StyleText(statusText, statusText.text, 13f, ErrorColor, TextAlignmentOptions.Center, false, 0.4f, font);
                SetRect(statusText.rectTransform, new Vector2(0.5f, 1f), new Vector2(400f, 30f), new Vector2(0f, -468f));
            }

            TextMeshProUGUI brand = AddText(root, "Brand", "BIGWORLD", 12f, WithAlpha(AccentCyan, 0.85f), TextAlignmentOptions.Left, font);
            brand.characterSpacing = 9f;
            SetRect(brand.rectTransform, new Vector2(0f, 1f), new Vector2(320f, 24f), new Vector2(30f, -28f));

            TextMeshProUGUI footer = AddText(root, "Footer", "ONLINE PROTOTYPE", 11f, WithAlpha(TextMuted, 0.9f), TextAlignmentOptions.Center, font);
            footer.characterSpacing = 6f;
            SetRect(footer.rectTransform, new Vector2(0.5f, 0f), new Vector2(320f, 20f), new Vector2(0f, 26f));
        }

        public static UiLoadingVisual ApplyLoading(Transform root, Text loadingText, Image spinnerImage)
        {
            if (root == null) return null;

            Image background = root.GetComponent<Image>();
            if (background != null)
            {
                background.sprite = BackgroundGradient;
                background.type = Image.Type.Simple;
                background.color = Color.white;
                background.raycastTarget = true;
            }

            Image vignette = AddImage(root, "LoadingVignette", VignetteSprite, WithAlpha(Color.black, 0.5f));
            Stretch(vignette.rectTransform);
            vignette.transform.SetAsFirstSibling();

            RectTransform center = root.Find("CenterGroup") as RectTransform;
            if (center == null)
            {
                GameObject centerObject = new GameObject("CenterGroup", typeof(RectTransform));
                center = (RectTransform)centerObject.transform;
                center.SetParent(root, false);
            }
            SetRect(center, new Vector2(0.5f, 0.5f), new Vector2(480f, 320f), Vector2.zero);

            Image glow = AddImage(center, "SpinnerGlow", Glow, WithAlpha(AccentCyan, 0.16f));
            SetRect(glow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(300f, 300f), new Vector2(0f, 52f));
            glow.transform.SetAsFirstSibling();

            Image ring = AddImage(center, "SpinnerRing", Ring, WithAlpha(TextPrimary, 0.08f));
            SetRect(ring.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(118f, 118f), new Vector2(0f, 52f));

            if (spinnerImage == null)
                spinnerImage = AddImage(center, "SpinnerArc", Arc, AccentCyan);
            else
            {
                spinnerImage.sprite = Arc;
                spinnerImage.type = Image.Type.Simple;
                spinnerImage.color = AccentCyan;
            }
            SetRect(spinnerImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(98f, 98f), new Vector2(0f, 52f));

            Image innerArc = AddImage(center, "SpinnerInner", Arc, WithAlpha(AccentViolet, 0.92f));
            SetRect(innerArc.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(64f, 64f), new Vector2(0f, 52f));
            innerArc.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 140f);

            Image core = AddImage(center, "SpinnerCore", Dot, WithAlpha(AccentWarm, 0.95f));
            SetRect(core.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(10f, 10f), new Vector2(0f, 52f));

            TextMeshProUGUI title = AddText(center, "LoadingTitle", "LOADING", 16f, WithAlpha(TextPrimary, 0.72f), TextAlignmentOptions.Center, null);
            title.characterSpacing = 9f;
            SetRect(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(420f, 24f), new Vector2(0f, 126f));

            if (loadingText != null)
            {
                loadingText.fontSize = 16;
                loadingText.color = TextSecondary;
                loadingText.alignment = TextAnchor.MiddleCenter;
                loadingText.raycastTarget = false;
                SetRect(loadingText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(460f, 28f), new Vector2(0f, -10f));
            }

            TextMeshProUGUI percent = AddText(center, "LoadingPercent", "0%", 13f, AccentCyan, TextAlignmentOptions.Center, null);
            SetRect(percent.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(420f, 22f), new Vector2(0f, -42f));

            Image track = AddImage(center, "ProgressTrack", ProgressBarSprite, WithAlpha(TextPrimary, 0.08f));
            SetRect(track.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(360f, 8f), new Vector2(0f, -76f));
            track.type = Image.Type.Sliced;

            Image fill = AddImage(center, "ProgressFill", ProgressBarSprite, AccentCyan);
            SetRect(fill.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(360f, 8f), new Vector2(0f, -76f));
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;

            UiLoadingVisual visual = new UiLoadingVisual();
            visual.Center = center;
            visual.Spinner = spinnerImage.rectTransform;
            visual.InnerArc = innerArc.rectTransform;
            visual.LoadingText = loadingText;
            visual.PercentText = percent;
            visual.ProgressFill = fill;
            visual.SpinnerGlow = glow;
            visual.Reset();
            return visual;
        }

        static void StyleInput(TMP_InputField field, string placeholder, TMP_FontAsset font)
        {
            if (field == null) return;

            Image image = field.GetComponent<Image>();
            if (image != null)
            {
                image.sprite = InputPanel;
                image.type = Image.Type.Sliced;
                image.color = WithAlpha(InputColor, 0.94f);
            }
            field.transition = Selectable.Transition.None;

            Image outline = AddImage(field.transform, "InputOutline", InputOutline, WithAlpha(TextPrimary, 0.10f));
            Stretch(outline.rectTransform);
            outline.type = Image.Type.Sliced;
            outline.transform.SetAsFirstSibling();

            Image accent = AddImage(field.transform, "InputAccent", AccentStripe, WithAlpha(AccentCyan, 0.85f));
            accent.type = Image.Type.Simple;
            SetRect(accent.rectTransform, new Vector2(0f, 0.5f), new Vector2(3f, 24f), new Vector2(15f, 0f));

            UiInputFocus focus = field.GetComponent<UiInputFocus>();
            if (focus == null) focus = field.gameObject.AddComponent<UiInputFocus>();
            focus.Outline = outline;
            focus.IdleColor = WithAlpha(TextPrimary, 0.10f);
            focus.FocusColor = WithAlpha(AccentCyan, 0.55f);

            if (field.textComponent != null)
            {
                RectTransform textRect = field.textComponent.rectTransform;
                textRect.offsetMin = new Vector2(34f, 8f);
                textRect.offsetMax = new Vector2(-18f, -8f);
                field.textComponent.font = font;
                field.textComponent.fontSize = 16f;
                field.textComponent.color = TextPrimary;
                field.textComponent.alignment = TextAlignmentOptions.Left;
            }

            if (field.placeholder is TMP_Text placeholderText)
            {
                RectTransform placeholderRect = placeholderText.rectTransform;
                placeholderRect.offsetMin = new Vector2(34f, 8f);
                placeholderRect.offsetMax = new Vector2(-18f, -8f);
                placeholderText.font = font;
                placeholderText.fontSize = 16f;
                placeholderText.color = TextMuted;
                placeholderText.alignment = TextAlignmentOptions.Left;
                placeholderText.text = placeholder;
            }

            field.customCaretColor = true;
            field.caretColor = AccentCyan;
            field.selectionColor = WithAlpha(AccentCyan, 0.25f);
        }

        static void StyleButton(Button button, TMP_FontAsset font)
        {
            if (button == null) return;

            Image image = button.GetComponent<Image>();
            if (image != null)
            {
                image.sprite = ButtonPanel;
                image.type = Image.Type.Simple;
                image.color = Color.white;
            }

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Hex("#DDEBFF");
            colors.pressedColor = Hex("#AFC4FF");
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.55f, 0.58f, 0.68f, 0.55f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.transition = Selectable.Transition.ColorTint;

            TMP_Text label = FindText(button.transform, "Text");
            StyleText(label, "SIGN IN", 18f, Color.white, TextAlignmentOptions.Center, true, 7f, font);

            if (button.GetComponent<UiHoverScale>() == null) button.gameObject.AddComponent<UiHoverScale>();
        }

        static void StyleText(TMP_Text text, string value, float size, Color color, TextAlignmentOptions alignment, bool bold, float spacing, TMP_FontAsset font)
        {
            if (text == null) return;
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            text.characterSpacing = spacing;
            text.raycastTarget = false;
            if (font != null) text.font = font;
        }

        static TextMeshProUGUI AddText(Transform parent, string name, string value, float size, Color color, TextAlignmentOptions alignment, TMP_FontAsset font)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            if (font != null) text.font = font;
            return text;
        }

        static Image AddImage(Transform parent, string name, Sprite sprite, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            if (sprite != null && sprite.border != Vector4.zero) image.type = Image.Type.Sliced;
            return image;
        }

        static Image FindImage(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<Image>() : null;
        }

        static TMP_Text FindText(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<TMP_Text>() : null;
        }

        static void Stretch(RectTransform rect)
        {
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        static void SetRect(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position)
        {
            if (rect == null) return;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        static Color Hex(string value)
        {
            ColorUtility.TryParseHtmlString(value, out Color color);
            return color;
        }

        static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        static Texture2D NewTexture(int width, int height)
        {
            return new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        static Sprite CreateSprite(Texture2D texture, Vector4 border)
        {
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        static Sprite BuildVerticalGradient(int width, int height, Color bottom, Color top)
        {
            Texture2D texture = NewTexture(width, height);
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                Color color = Color.Lerp(bottom, top, (y + 0.5f) / height);
                int offset = y * width;
                for (int x = 0; x < width; x++) pixels[offset + x] = ToColor32(color, 1f);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateSprite(texture, Vector4.zero);
        }

        static Sprite BuildRoundedRect(int width, int height, int radius, Color bottom, Color top, bool verticalGradient)
        {
            Texture2D texture = NewTexture(width, height);
            Color32[] pixels = new Color32[width * height];
            float halfWidth = width * 0.5f;
            float halfHeight = height * 0.5f;
            for (int y = 0; y < height; y++)
            {
                Color color = verticalGradient ? Color.Lerp(bottom, top, (y + 0.5f) / height) : Color.white;
                for (int x = 0; x < width; x++)
                {
                    float distance = RoundedRectDistance(x + 0.5f - halfWidth, y + 0.5f - halfHeight, halfWidth, halfHeight, radius);
                    pixels[y * width + x] = ToColor32(color, Mathf.Clamp01(0.5f - distance));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateSprite(texture, new Vector4(radius, radius, radius, radius));
        }

        static Sprite BuildOutline(int size, int radius, float thickness)
        {
            Texture2D texture = NewTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = RoundedRectDistance(x + 0.5f - half, y + 0.5f - half, half, half, radius);
                    pixels[y * size + x] = ToColor32(Color.white, Mathf.Clamp01(thickness * 0.5f + 0.5f - Mathf.Abs(distance)));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateSprite(texture, new Vector4(radius, radius, radius, radius));
        }

        static Sprite BuildRadialGlow(int size, float power)
        {
            Texture2D texture = NewTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    pixels[y * size + x] = ToColor32(Color.white, Mathf.Pow(Mathf.Clamp01(1f - distance), power));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateSprite(texture, Vector4.zero);
        }

        static Sprite BuildRing(int size, float thickness, float degrees)
        {
            Texture2D texture = NewTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            float radius = half - thickness * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f - half;
                    float py = y + 0.5f - half;
                    float distance = Mathf.Sqrt(px * px + py * py);
                    float alpha = Mathf.Clamp01(thickness * 0.5f + 0.5f - Mathf.Abs(distance - radius));
                    if (degrees < 359.9f)
                    {
                        float angle = Mathf.Repeat(Mathf.Atan2(py, px) * Mathf.Rad2Deg + 360f, 360f);
                        if (angle > degrees) alpha = 0f;
                    }
                    pixels[y * size + x] = ToColor32(Color.white, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateSprite(texture, Vector4.zero);
        }

        static Sprite BuildVignette(int size)
        {
            Texture2D texture = NewTexture(size, size);
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01((distance - 0.30f) / 0.70f);
                    pixels[y * size + x] = ToColor32(Color.black, Mathf.Pow(alpha, 1.6f) * 0.92f);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateSprite(texture, Vector4.zero);
        }

        static float RoundedRectDistance(float px, float py, float halfWidth, float halfHeight, float radius)
        {
            float qx = Mathf.Abs(px) - halfWidth + radius;
            float qy = Mathf.Abs(py) - halfHeight + radius;
            float outsideX = Mathf.Max(qx, 0f);
            float outsideY = Mathf.Max(qy, 0f);
            return Mathf.Min(Mathf.Max(qx, qy), 0f) + Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY) - radius;
        }

        static Color32 ToColor32(Color color, float alpha)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255));
        }
    }

    public sealed class UiLoadingVisual
    {
        public RectTransform Center;
        public RectTransform Spinner;
        public RectTransform InnerArc;
        public Text LoadingText;
        public TMP_Text PercentText;
        public Image ProgressFill;
        public Image SpinnerGlow;

        float _targetProgress;
        float _shownProgress;
        float _time;
        string _message = "LOADING";

        public void Reset()
        {
            _targetProgress = 0f;
            _shownProgress = 0f;
            _time = 0f;
            if (ProgressFill != null) ProgressFill.fillAmount = 0f;
            if (PercentText != null) PercentText.text = "0%";
            if (LoadingText != null) LoadingText.text = _message;
            if (Center != null) Center.localScale = Vector3.one;
        }

        public void SetMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _message = message.TrimEnd('.', '。');
            if (LoadingText != null) LoadingText.text = _message;
        }

        public void SetProgress(float progress)
        {
            _targetProgress = Mathf.Clamp01(progress);
        }

        public void Tick(float deltaTime, bool active)
        {
            if (!active) return;
            _time += deltaTime;

            if (Spinner != null) Spinner.Rotate(0f, 0f, -150f * deltaTime, Space.Self);
            if (InnerArc != null) InnerArc.Rotate(0f, 0f, 240f * deltaTime, Space.Self);
            if (SpinnerGlow != null)
            {
                float pulse = 1f + 0.035f * Mathf.Sin(_time * 3.4f);
                SpinnerGlow.rectTransform.localScale = new Vector3(pulse, pulse, 1f);
            }
            if (Center != null)
            {
                float breathe = 1f + 0.005f * Mathf.Sin(_time * 1.8f);
                Center.localScale = new Vector3(breathe, breathe, 1f);
            }

            _shownProgress = Mathf.MoveTowards(_shownProgress, _targetProgress, deltaTime * 0.9f);
            if (ProgressFill != null) ProgressFill.fillAmount = _shownProgress;
            if (PercentText != null) PercentText.text = Mathf.RoundToInt(_shownProgress * 100f) + "%";
            if (LoadingText != null)
            {
                int dots = 1 + (int)(_time * 2.5f) % 3;
                LoadingText.text = _message + new string('.', dots);
            }
        }
    }

    public sealed class UiHoverScale : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public float HoverScale = 1.025f;

        Vector3 _baseScale;
        Vector3 _targetScale;
        bool _ready;

        void Awake()
        {
            _baseScale = transform.localScale;
            _targetScale = _baseScale;
            _ready = true;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _targetScale = _baseScale * HoverScale;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _targetScale = _baseScale;
        }

        void Update()
        {
            if (!_ready) return;
            transform.localScale = Vector3.Lerp(transform.localScale, _targetScale, Time.unscaledDeltaTime * 14f);
        }
    }

    public sealed class UiInputFocus : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        public Image Outline;
        public Color IdleColor = new Color(1f, 1f, 1f, 0.1f);
        public Color FocusColor = new Color(0.33f, 0.85f, 0.98f, 0.55f);

        public void OnSelect(BaseEventData eventData)
        {
            if (Outline != null) Outline.color = FocusColor;
        }

        public void OnDeselect(BaseEventData eventData)
        {
            if (Outline != null) Outline.color = IdleColor;
        }
    }
}
