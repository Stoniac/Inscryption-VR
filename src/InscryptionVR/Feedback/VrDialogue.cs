using System;
using System.Reflection;
using BepInEx.Logging;
using I2.TextAnimation;
using UnityEngine;
using UnityEngine.UI;

namespace InscryptionVR
{
    public sealed class VrDialogue : MonoBehaviour
    {
        public const int SubtitleLayer = 30;
        internal const string GameStyle = "Game";
        internal const string PanelStyle = "Panel";
        internal static readonly string[] Styles = { PanelStyle, GameStyle };
        private const float PanelWidth = 1050f, PanelHeight = 220f, TextWidth = 990f, TextHeight = 196f;
        private const int PanelFontSize = 36, PanelMinFontSize = 22;
        private const float GameEmUnits = 36f;
        private const int StageLayer = 31;
        private const int MaxDownsample = 4;
        private const int MaxPixelTexture = 2048;
        private const float FallbackEmPixels = 14f;
        private const float ResolutionBoost = 1.5f;
        private static readonly Vector3 StagePosition = new Vector3(0f, -7000f, 0f);
        private ManualLogSource _logger;
        private FieldInfo _textShadow;
        private bool _gameStyle;
        private bool _styleApplied;
        private Image _background;
        private LetterJitter _jitter;
        private RawImage _pixelImage;
        private GameObject _stage;
        private Canvas _stageCanvas;
        private RectTransform _stageMirror;
        private Text _stageText;
        private Camera _stageCamera;
        private RenderTexture _pixelTexture;
        private readonly System.Collections.Generic.List<Shadow> _stageEffects = new System.Collections.Generic.List<Shadow>();
        private Text _effectsSource;
        private Text _metricsSource;
        private string _metricsText;
        private float _gamePixelsPerUnit;
        private int _downsample = 1;
        private string _metricsDetail;
        private string _loggedMetrics;
        private string _renderedKey;
        private DiskCardGame.TextDisplayer _hooked;
        private Action<DialogueEvent.Line> _onLine;
        private DiskCardGame.TextDisplayer.LetterAnimation _pendingAnimation;
        private bool _hasPendingAnimation;
        private StereoRenderer _renderer;
        private FieldInfo _textMesh;
        private FieldInfo _continuePressed;
        private FieldInfo _currentAudio;
        private DiskCardGame.TextDisplayer _source;
        private float _nextSourceSearch;
        private const float SourceSearchSeconds = 0.25f;
        private GameObject _panel;
        private Text _subtitle;
        private Material _material;
        private string _observedText;
        private bool _observedVisible;
        private bool _observedInput;
        private float _visibleSince;
        private float _visibleSinceRealtime;
        private bool _pulseSent;
        private bool _failed;
        private bool _showSubtitles;
        private float _heightMeters;
        private float _autoAdvancePace;
        private float _readSeconds = -1f;
        private float _elapsedSeconds;
        internal const float MinReadSeconds = 1.5f;
        internal const float FloorReadSeconds = 0.6f;
        internal const float MinAfterTextSeconds = 0.4f;
        private float _afterTextSeconds;
        private const float ReadSecondsPerRootCharacter = 0.45f;
        private const float GazeDegrees = 35f;
        private FieldInfo _textAnimation;
        private bool _revealProven;
        private const float MinShownSeconds = 0.25f;
        private const float AfterAutoWindowSeconds = 1f;
        private const float AfterAutoGuardSeconds = 0.7f;
        private float _autoAdvancedAt = -10f;
        private const float MaxLineSeconds = 10f;
        private bool _placed;
        private bool _placedOverHead;
        private bool _loggedAnchor;
        private const float HeadLockedDistance = 1.226f;
        private const float FollowRate = 10f;

        public void Initialize(ManualLogSource logger, StereoRenderer renderer, bool showSubtitles, float heightMeters, float autoAdvancePace, string style)
        {
            _logger = logger;
            _renderer = renderer;
            _showSubtitles = showSubtitles;
            _heightMeters = heightMeters;
            _autoAdvancePace = Mathf.Max(0f, autoAdvancePace);
            _gameStyle = style == GameStyle;
            _onLine = OnDialogueLine;
            try
            {
                var bindings = new VrDialogueBindings();
                _textMesh = bindings.TextMesh;
                _continuePressed = bindings.ContinuePressed;
                _currentAudio = bindings.CurrentAudio;
                _textShadow = bindings.TextShadow;
                _textAnimation = bindings.TextAnimation;
                if (_textAnimation == null) _logger.LogWarning("TextDisplayer.textAnimation not found; dialogue auto-advance waits for the voice instead of the text.");
                if (_showSubtitles)
                {
                    try { CreatePanel(); }
                    catch (Exception exception) { FailVisual(exception); }
                }
                _logger.LogInfo("VR dialogue input enabled; subtitles " + (_showSubtitles ? "enabled (" + (_gameStyle ? GameStyle : PanelStyle) + " style)" : "disabled") +
                    ". Either trigger advances input dialogue on release; " + AutoAdvanceDescription() + "; automatic messages keep their original timing.");
            }
            catch (Exception exception) { Fail(exception); }
        }

        public void SetAutoAdvance(float pace)
        {
            _autoAdvancePace = Mathf.Max(0f, pace);
            if (_logger != null && !_failed) _logger.LogInfo("Dialogue " + AutoAdvanceDescription() + ".");
        }

        private string AutoAdvanceDescription()
        {
            return _autoAdvancePace > 0f
                ? "auto-advance at " + _autoAdvancePace.ToString("0.00") + "x pace (reading time " + ReadSecondsPerRootCharacter.ToString("0.00") + " s x sqrt(characters), at least " +
                    MinimumReadSeconds(_autoAdvancePace).ToString("0.00") + " s, the finished text stays at least " +
                    AfterTextSeconds(_autoAdvancePace).ToString("0.00") + " s, and it advances only while looking at Leshy or the subtitles)"
                : "auto-advance off";
        }

        internal static float ReadSeconds(string line, float pace)
        {
            int characters = VisibleCharacters(line);
            float seconds = ReadSecondsPerRootCharacter * Mathf.Sqrt(characters) / Mathf.Max(pace, 0.01f);
            return Mathf.Max(MinimumReadSeconds(pace), seconds);
        }

        internal static float MinimumReadSeconds(float pace)
        {
            return Mathf.Max(FloorReadSeconds, MinReadSeconds / Mathf.Max(pace, 0.01f));
        }

        internal static float AfterTextSeconds(float pace)
        {
            return MinAfterTextSeconds / Mathf.Max(pace, 0.01f);
        }

        private static int VisibleCharacters(string line)
        {
            if (String.IsNullOrEmpty(line)) return 0;
            int count = 0;
            char closing = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (closing != '\0')
                {
                    if (c == closing) closing = '\0';
                    continue;
                }
                if (c == '<' && line.IndexOf('>', i + 1) > i) { closing = '>'; continue; }
                if (c == '[' && line.IndexOf(']', i + 1) > i) { closing = ']'; continue; }
                if (!Char.IsControl(c)) count++;
            }
            return count;
        }

        private void Update()
        {
            if (_autoAdvancePace <= 0f || _failed || _logger == null) return;
            try { TickAutoAdvance(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void TickAutoAdvance()
        {
            Text text;
            if (!Observe(out text) || !_observedInput || _pulseSent || MergeLookAway.Holding) return;
            float now = Time.realtimeSinceStartup;
            if (_readSeconds < 0f)
            {
                _readSeconds = ReadSeconds(text.text, _autoAdvancePace);
                _elapsedSeconds = 0f;
                _afterTextSeconds = 0f;
            }
            _elapsedSeconds += Time.unscaledDeltaTime;
            if (now - _visibleSinceRealtime < MinShownSeconds || Time.time - _visibleSince < MinShownSeconds || LineStillShowing(now)) return;
            _afterTextSeconds += Time.unscaledDeltaTime;
            if (_elapsedSeconds < _readSeconds || _afterTextSeconds < AfterTextSeconds(_autoAdvancePace) || !LookingAtLeshy()) return;
            _continuePressed.SetValue(_source, true);
            _pulseSent = true;
            _autoAdvancedAt = now;
            VrLog.Chatter(_logger, "Dialogue auto-advanced after " + _readSeconds.ToString("0.0") + " s of reading (" + VisibleCharacters(text.text) + " characters) once the " +
                (_revealProven ? "text finished" : "voice ended") + ".");
        }

        private bool LookingAtLeshy()
        {
            Vector3 eye;
            Quaternion view;
            float scale;
            if (_renderer == null || !_renderer.TryGetHeadPose(out eye, out view, out scale)) return true;
            DiskCardGame.OpponentAnimationController opponent = DiskCardGame.OpponentAnimationController.Instance;
            Transform head = opponent != null && opponent.isActiveAndEnabled ? opponent.Head : null;
            if (head == null || !head.gameObject.activeInHierarchy) return true;
            Vector3 forward = view * Vector3.forward;
            if (WithinGaze(forward, head.position - eye)) return true;
            return _showSubtitles && _panel != null && _panel.activeInHierarchy && WithinGaze(forward, _panel.transform.position - eye);
        }

        private static bool WithinGaze(Vector3 forward, Vector3 toward)
        {
            return toward.sqrMagnitude > 1e-8f && Vector3.Angle(forward, toward) < GazeDegrees;
        }

        private bool LineStillShowing(float now)
        {
            if (now - _visibleSinceRealtime >= MaxLineSeconds) return false;
            if (TextRevealing()) return true;
            if (_revealProven) return false;
            AudioSource voice = _currentAudio.GetValue(_source) as AudioSource;
            return voice != null && voice.isPlaying;
        }

        private bool TextRevealing()
        {
            if (_textAnimation == null) return false;
            TextAnimation animation = _textAnimation.GetValue(_source) as TextAnimation;
            if (animation == null || animation.mPlayingAnimations == null) return false;
            bool revealing = false;
            for (int i = 0; i < animation.mPlayingAnimations.Count; i++)
            {
                SE_Animation playing = animation.mPlayingAnimations[i];
                if (playing == null || !playing.IsPlaying) continue;
                float end = RevealEnd(playing);
                if (end <= 0f) continue;
                if (!_revealProven)
                {
                    _revealProven = true;
                    _logger.LogInfo("Dialogue text reveal found: '" + playing.Name + "' ends " + end.ToString("0.00") + " s into the line (" + DescribeSequences(playing) + "). Auto-advance now waits for the text.");
                }
                if (playing.mRealTime < end) revealing = true;
            }
            return revealing;
        }

        private static float RevealEnd(SE_Animation playing)
        {
            float end = 0f;
            if (playing._Sequences == null) return end;
            for (int i = 0; i < playing._Sequences.Length; i++)
            {
                SE_AnimSequence sequence = playing._Sequences[i];
                if (sequence == null || !sequence._Enabled || sequence._Separation <= 0f) continue;
                if (sequence._Playback != SE_Animation.ePlayback.Single) continue;
                if (sequence.mTotalTime <= 0f || sequence.mTotalTime > MaxLineSeconds * 3f) continue;
                if (sequence.mTotalTime > end) end = sequence.mTotalTime;
            }
            return end;
        }

        private static string DescribeSequences(SE_Animation playing)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < playing._Sequences.Length; i++)
            {
                SE_AnimSequence sequence = playing._Sequences[i];
                if (sequence == null) continue;
                parts.Add(sequence.GetTypeName() + " '" + sequence._Name + "' " + sequence._Playback + (sequence._Enabled ? "" : " off") +
                    " sep " + sequence._Separation.ToString("0.###") + " total " + (sequence.mTotalTime > 1e6f ? "inf" : sequence.mTotalTime.ToString("0.00")));
            }
            return String.Join("; ", parts.ToArray());
        }

        public void SetSubtitles(bool show)
        {
            if (_failed || _logger == null) return;
            if (show && _panel == null)
            {
                try { CreatePanel(); }
                catch (Exception exception) { FailVisual(exception); return; }
            }
            _showSubtitles = show;
            if (!show) Hide();
        }

        public void SetHeight(float meters) { _heightMeters = meters; }

        public void SetStyle(string style)
        {
            bool game = style == GameStyle;
            if (game == _gameStyle) return;
            _gameStyle = game;
            _styleApplied = false;
            if (_logger != null && !_failed) _logger.LogInfo("Subtitle style: " + (game ? GameStyle : PanelStyle) + ".");
        }

        private void OnDialogueLine(DialogueEvent.Line line)
        {
            if (line == null) return;
            _pendingAnimation = line.letterAnimation;
            _hasPendingAnimation = true;
        }

        private void Hook(DiskCardGame.TextDisplayer source)
        {
            if (source == _hooked) return;
            Unhook();
            _hooked = source;
            if (_hooked != null)
                _hooked.newDialogueEventLine = (Action<DialogueEvent.Line>)Delegate.Combine(_hooked.newDialogueEventLine, _onLine);
        }

        private void Unhook()
        {
            if (_hooked != null)
                _hooked.newDialogueEventLine = (Action<DialogueEvent.Line>)Delegate.Remove(_hooked.newDialogueEventLine, _onLine);
            _hooked = null;
        }

        private void ApplyStyle()
        {
            _subtitle.enabled = !_gameStyle;
            _pixelImage.enabled = _gameStyle;
            _renderedKey = null;
            RectTransform backgroundRect = (RectTransform)_background.transform;
            if (_gameStyle)
            {
                backgroundRect.anchorMin = backgroundRect.anchorMax = new Vector2(0.5f, 0.5f);
                backgroundRect.sizeDelta = Vector2.zero;
            }
            else
            {
                backgroundRect.anchorMin = Vector2.zero; backgroundRect.anchorMax = Vector2.one;
                backgroundRect.offsetMin = Vector2.zero; backgroundRect.offsetMax = Vector2.zero;
                _subtitle.color = Color.white;
                if (_stage != null) _stage.SetActive(false);
            }
            ApplyBackground();
            _styleApplied = true;
        }

        private void ApplyBackground()
        {
            if (!_gameStyle)
            {
                _background.sprite = null;
                _background.type = Image.Type.Simple;
                _background.color = new Color(0.015f, 0.015f, 0.015f, 0.88f);
                _background.enabled = true;
                return;
            }
            Image shadow = _source != null && _textShadow != null ? _textShadow.GetValue(_source) as Image : null;
            if (shadow != null && shadow.sprite != null)
            {
                _background.sprite = shadow.sprite;
                _background.type = Image.Type.Simple;
                _background.color = shadow.color;
                _background.enabled = true;
            }
            else _background.enabled = false;
        }

        private void RenderPixelSubtitle(Text source, Font font, bool newText)
        {
            if (_stage == null) CreateStage();
            if (!_stage.activeSelf) _stage.SetActive(true);
            if (newText || source != _metricsSource || _metricsText != source.text) Measure(source);
            if (source != _effectsSource) CopyEffects(source);
            float p = _gamePixelsPerUnit * ResolutionBoost;
            int em = Mathf.Max(1, source.fontSize);
            float raster = source.pixelsPerUnit > 0f ? Mathf.Clamp(source.pixelsPerUnit, 0.25f, 64f) : 1f;
            string key = source.text + "|" + source.color + "|" + font.GetInstanceID() + "|" + em + "|" + source.fontStyle + "|" +
                p + "|" + _downsample + "|" + raster + "|" + _jitter.Version;
            if (key == _renderedKey && _pixelTexture != null) return;
            _renderedKey = key;

            RectTransform sourceRect = source.rectTransform;
            float wrap = TextWidth * em / GameEmUnits;
            if (sourceRect.rect.width > 0f) wrap = Mathf.Min(wrap, sourceRect.rect.width);
            float tall = Mathf.Max(sourceRect.rect.height, em * 2f);
            _stage.transform.position = StagePosition;
            _stage.transform.rotation = Quaternion.identity;
            _stageCanvas.scaleFactor = raster;
            _stageMirror.localScale = Vector3.one * p;
            RectTransform textRect = _stageText.rectTransform;
            textRect.pivot = sourceRect.pivot;
            textRect.sizeDelta = new Vector2(wrap, tall);
            _stageText.font = font;
            _stageText.fontSize = em;
            _stageText.fontStyle = source.fontStyle;
            _stageText.lineSpacing = source.lineSpacing;
            _stageText.alignment = source.alignment;
            _stageText.alignByGeometry = source.alignByGeometry;
            _stageText.supportRichText = source.supportRichText;
            _stageText.color = source.color;
            _stageText.text = source.text;
            _jitter.Amplitude = 1f / p;
            Canvas.ForceUpdateCanvases();

            Vector2 low, high;
            if (!GlyphBounds(out low, out high)) { _pixelImage.enabled = false; return; }
            _pixelImage.enabled = true;
            int pad = 4 + Mathf.CeilToInt(em * p * 0.15f);
            int minX = Mathf.FloorToInt(low.x * p) - pad, maxX = Mathf.CeilToInt(high.x * p) + pad;
            int minY = Mathf.FloorToInt(low.y * p) - pad, maxY = Mathf.CeilToInt(high.y * p) + pad;
            int width = Mathf.Clamp(maxX - minX, 1, MaxPixelTexture);
            int height = Mathf.Clamp(maxY - minY, 1, MaxPixelTexture);
            int scale = Mathf.Max(1, Mathf.Min(_downsample, MaxPixelTexture * 2 / Mathf.Max(width, height)));
            if (_pixelTexture == null || _pixelTexture.width != width || _pixelTexture.height != height)
            {
                if (_pixelTexture != null) { _pixelTexture.Release(); Destroy(_pixelTexture); }
                _pixelTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                _pixelTexture.name = "InscryptionVR Pixel Subtitle";
                _pixelTexture.filterMode = FilterMode.Point;
                _pixelTexture.wrapMode = TextureWrapMode.Clamp;
                _pixelTexture.useMipMap = false;
                _pixelTexture.Create();
                _pixelImage.texture = _pixelTexture;
            }

            _stageCamera.transform.position = StagePosition + new Vector3((minX + width * 0.5f), (minY + height * 0.5f), -10f);
            _stageCamera.transform.rotation = Quaternion.identity;
            _stageCamera.orthographicSize = height * 0.5f;
            _stageCamera.aspect = width / (float)height;
            Color clear = source.color;
            clear.a = 0f;
            _stageCamera.backgroundColor = clear;
            RenderTexture previous = RenderTexture.active;
            RenderTexture full = RenderTexture.GetTemporary(width * scale, height * scale, 16, RenderTextureFormat.ARGB32);
            full.filterMode = FilterMode.Point;
            _stageCamera.targetTexture = full;
            _stageCamera.aspect = width / (float)height;
            _stageCamera.Render();
            _stageCamera.targetTexture = null;
            Graphics.Blit(full, _pixelTexture);
            RenderTexture.ReleaseTemporary(full);
            RenderTexture.active = previous;

            float unit = Mathf.Min(GameEmUnits / (em * p), PanelWidth / width, PanelHeight / height);
            Vector2 shown = new Vector2(width * unit, height * unit);
            _pixelImage.rectTransform.sizeDelta = shown;
            ((RectTransform)_background.transform).sizeDelta = shown + Vector2.one * (GameEmUnits * 0.5f);
            LogMetrics(source, font, em, p, raster, width, height);
        }

        private bool GlyphBounds(out Vector2 low, out Vector2 high)
        {
            low = new Vector2(float.MaxValue, float.MaxValue);
            high = new Vector2(float.MinValue, float.MinValue);
            TextGenerator generator = _stageText.cachedTextGenerator;
            float perPixel = _stageText.pixelsPerUnit > 0f ? 1f / _stageText.pixelsPerUnit : 1f;
            System.Collections.Generic.IList<UIVertex> verts = generator.verts;
            bool any = false;
            for (int i = 0; i < verts.Count; i++)
            {
                Vector3 position = verts[i].position * perPixel;
                low = Vector2.Min(low, position);
                high = Vector2.Max(high, position);
                any = true;
            }
            return any && high.x > low.x && high.y > low.y;
        }

        private void Measure(Text source)
        {
            _metricsSource = source;
            _metricsText = source.text;
            _downsample = 1;
            float worldPixels = 0f;
            Canvas canvas = source.canvas;
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            Camera camera = null;
            if (root != null && root.renderMode != RenderMode.ScreenSpaceOverlay && root.worldCamera != null &&
                root.worldCamera.GetComponent<PixelCamera>() != null)
                camera = root.worldCamera;
            if (camera == null) camera = FindPixelCamera(source.gameObject.layer);
            if (camera != null && camera.orthographic && camera.orthographicSize > 0f)
            {
                worldPixels = PixelCamera.PIXELS_PER_UNIT;
                int renderHeight = Mathf.Max(2, Mathf.RoundToInt(camera.orthographicSize * 2f * worldPixels));
                _downsample = Mathf.Clamp(camera.pixelHeight / renderHeight, 1, MaxDownsample);
                _metricsDetail = "pixel camera '" + camera.name + "' (" + renderHeight + " px tall, drawn at " + _downsample + "x then point-sampled)";
            }
            else if (root != null && root.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                worldPixels = 1f;
                _metricsDetail = "overlay canvas '" + root.name + "'";
            }
            float p = Mathf.Abs(source.transform.lossyScale.y) * worldPixels;
            float em = Mathf.Max(1, source.fontSize) * p;
            if (worldPixels <= 0f || em < 5f || em > 96f)
            {
                p = FallbackEmPixels / Mathf.Max(1, source.fontSize);
                _downsample = 2;
                _metricsDetail = "fallback (measured " + em.ToString("0.#") + " px per letter" + (camera != null ? " via '" + camera.name + "'" : ", no pixel camera") + ")";
            }
            _gamePixelsPerUnit = p;
        }

        private static Camera FindPixelCamera(int layer)
        {
            Camera best = null;
            PixelCamera[] cameras = UnityEngine.Object.FindObjectsOfType<PixelCamera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i].GetComponent<Camera>();
                if (camera == null) continue;
                if ((camera.cullingMask & (1 << layer)) != 0) return camera;
                if (best == null) best = camera;
            }
            return best;
        }

        private void CopyEffects(Text source)
        {
            _effectsSource = source;
            for (int i = 0; i < _stageEffects.Count; i++)
                if (_stageEffects[i] != null) DestroyImmediate(_stageEffects[i]);
            _stageEffects.Clear();
            Shadow[] effects = source.GetComponents<Shadow>();
            for (int i = 0; i < effects.Length; i++)
            {
                Shadow original = effects[i];
                if (original == null || !original.enabled) continue;
                Shadow copy = original is Outline ? _stageText.gameObject.AddComponent<Outline>() : _stageText.gameObject.AddComponent<Shadow>();
                copy.effectColor = original.effectColor;
                copy.effectDistance = original.effectDistance;
                copy.useGraphicAlpha = original.useGraphicAlpha;
                _stageEffects.Add(copy);
            }
        }

        private void LogMetrics(Text source, Font font, int em, float p, float raster, int width, int height)
        {
            string summary = (font != null ? font.name : "none") + "|" + em + "|" + p.ToString("0.###") + "|" + _downsample + "|" + raster.ToString("0.##");
            if (summary == _loggedMetrics) return;
            bool first = _loggedMetrics == null;
            _loggedMetrics = summary;
            string line = "Game-style subtitles copy the game's pixel text: font '" + (font != null ? font.name : "none") + "' (" +
                (font != null && font.dynamic ? "dynamic, rasterised at " + (em * raster).ToString("0.#") + " px" : "bitmap") + "), size " + em +
                ", " + (em * p).ToString("0.#") + " pixels per letter (" + ResolutionBoost.ToString("0.##") + "x the game's) on " + _metricsDetail + ", " + _stageEffects.Count + " shadow effect(s), first crop " +
                width + "x" + height + " px.";
            if (first) _logger.LogInfo(line); else VrLog.Chatter(_logger, line);
        }

        private void CreateStage()
        {
            _stage = new GameObject("InscryptionVR Pixel Subtitle Stage", typeof(RectTransform), typeof(Canvas));
            _stage.layer = StageLayer;
            _stage.transform.SetParent(transform, false);
            _stage.transform.position = StagePosition;
            ((RectTransform)_stage.transform).sizeDelta = Vector2.zero;
            _stageCanvas = _stage.GetComponent<Canvas>();
            _stageCanvas.renderMode = RenderMode.WorldSpace;
            GameObject mirror = new GameObject("Mirror", typeof(RectTransform));
            mirror.layer = StageLayer;
            mirror.transform.SetParent(_stage.transform, false);
            _stageMirror = (RectTransform)mirror.transform;
            _stageMirror.anchorMin = _stageMirror.anchorMax = new Vector2(0.5f, 0.5f);
            _stageMirror.sizeDelta = Vector2.zero;
            GameObject textObject = new GameObject("Text", typeof(RectTransform));
            textObject.layer = StageLayer;
            textObject.transform.SetParent(mirror.transform, false);
            RectTransform textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
            _stageText = textObject.AddComponent<Text>();
            _stageText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _stageText.verticalOverflow = VerticalWrapMode.Overflow;
            _stageText.resizeTextForBestFit = false;
            _stageText.raycastTarget = false;
            _jitter = textObject.AddComponent<LetterJitter>();
            GameObject cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(_stage.transform, false);
            _stageCamera = cameraObject.AddComponent<Camera>();
            _stageCamera.enabled = false;
            _stageCamera.orthographic = true;
            _stageCamera.clearFlags = CameraClearFlags.SolidColor;
            _stageCamera.cullingMask = 1 << StageLayer;
            _stageCamera.nearClipPlane = 0.1f;
            _stageCamera.farClipPlane = 100f;
            _stageCamera.allowHDR = false;
            _stageCamera.allowMSAA = false;
            _stageCamera.useOcclusionCulling = false;
            _effectsSource = null;
            _renderedKey = null;
        }

        private void DestroyStage()
        {
            if (_stage != null) Destroy(_stage);
            _stage = null;
            _stageEffects.Clear();
            _effectsSource = null;
            if (_pixelTexture != null) { _pixelTexture.Release(); Destroy(_pixelTexture); }
            _pixelTexture = null;
        }

        public bool IsInputDialogue
        {
            get
            {
                if (_failed || _logger == null) return false;
                try { Text text; return Observe(out text) && _observedInput; }
                catch (Exception exception) { Fail(exception); return false; }
            }
        }

        public bool TryAdvance(bool deliberateRelease)
        {
            if (!deliberateRelease || _failed || _logger == null) return false;
            try
            {
                Text text;
                if (!Observe(out text) || !_observedInput || _pulseSent || MergeLookAway.Holding) return false;
                if (Time.time - _visibleSince < MinShownSeconds || Time.realtimeSinceStartup - _visibleSinceRealtime < MinShownSeconds)
                    return false;
                bool afterAuto = _visibleSinceRealtime - _autoAdvancedAt < AfterAutoWindowSeconds;
                if (afterAuto && Time.realtimeSinceStartup - _visibleSinceRealtime < AfterAutoGuardSeconds)
                {
                    _logger.LogInfo("Trigger ignored " + (Time.realtimeSinceStartup - _visibleSinceRealtime).ToString("0.00") +
                        " s into a line that auto-advance had just brought up (it was most likely meant for the line before).");
                    return false;
                }
                _continuePressed.SetValue(_source, true);
                _pulseSent = true;
                return true;
            }
            catch (Exception exception) { Fail(exception); return false; }
        }

        public void RefreshForRender()
        {
            if (_failed || _logger == null) return;
            if (!_showSubtitles) { Hide(); return; }
            try
            {
                Text text;
                Vector3 position;
                Quaternion rotation;
                float scale;
                if (!Observe(out text) || !_renderer.TryGetHeadPose(out position, out rotation, out scale))
                { Hide(); return; }
                Place(position, rotation, scale);
                if (!_styleApplied) ApplyStyle();
                Font font = text.font != null ? text.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
                bool newText = !_panel.activeSelf || _subtitle.text != text.text;
                if (_gameStyle)
                {
                    if (_stage == null) CreateStage();
                    if (newText)
                    {
                        _jitter.Animation = _hasPendingAnimation ? _pendingAnimation : DiskCardGame.TextDisplayer.LetterAnimation.Jitter;
                        _hasPendingAnimation = false;
                        ApplyBackground();
                    }
                    RenderPixelSubtitle(text, font, newText);
                    _subtitle.text = text.text;
                    _panel.SetActive(true);
                    return;
                }
                bool dirty = newText || _subtitle.font != font ||
                    _subtitle.supportRichText != text.supportRichText || _subtitle.fontStyle != text.fontStyle;
                _subtitle.font = font;
                _subtitle.text = text.text;
                _subtitle.supportRichText = text.supportRichText;
                _subtitle.fontStyle = text.fontStyle;
                _panel.SetActive(true);
                if (dirty) Canvas.ForceUpdateCanvases();
            }
            catch (Exception exception) { FailVisual(exception); }
        }

        private void Place(Vector3 eye, Quaternion view, float scale)
        {
            Vector3 anchor;
            bool overHead = TryGetHeadAnchor(eye, scale, out anchor);
            if (!overHead)
            {
                _panel.transform.position = eye + view * new Vector3(0f, 0.25f, 1.2f) * scale;
                _panel.transform.rotation = view;
                _panel.transform.localScale = Vector3.one * (0.001f * scale);
                _placed = _panel.activeSelf;
                _placedOverHead = false;
                return;
            }
            Vector3 target = anchor;
            if (_placed && _placedOverHead && _panel.activeSelf)
                target = Vector3.Lerp(_panel.transform.position, anchor, 1f - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime));
            Vector3 toPanel = target - eye;
            float distance = toPanel.magnitude;
            if (distance < 0.01f * scale) distance = 0.01f * scale;
            _panel.transform.position = target;
            _panel.transform.rotation = Quaternion.LookRotation(toPanel, Vector3.up);
            _panel.transform.localScale = Vector3.one * (0.001f * distance / HeadLockedDistance);
            _placed = true;
            _placedOverHead = true;
        }

        private bool TryGetHeadAnchor(Vector3 eye, float scale, out Vector3 anchor)
        {
            anchor = Vector3.zero;
            if (_renderer != null && _renderer.Walking) return false;
            DiskCardGame.OpponentAnimationController opponent = DiskCardGame.OpponentAnimationController.Instance;
            if (opponent == null || !opponent.isActiveAndEnabled) return false;
            Transform head = opponent.Head;
            if (head == null || !head.gameObject.activeInHierarchy) return false;
            anchor = head.position + Vector3.up * (_heightMeters * scale);
            if ((anchor - eye).sqrMagnitude < 0.04f * scale * scale) return false;
            if (!_loggedAnchor)
            {
                _loggedAnchor = true;
                _logger.LogInfo("Subtitles float above '" + head.name + "', " + ((head.position - eye).magnitude / scale).ToString("0.00") +
                    " m from your eyes and " + ((head.position.y - eye.y) / scale).ToString("+0.00;-0.00") + " m above them.");
            }
            return true;
        }

        private bool Observe(out Text text)
        {
            text = null;
            Vector3 position; Quaternion rotation; float scale;
            if (!VrScenes.InVrScene || Time.timeScale <= 0f || GameHooks.IsPaused ||
                _renderer == null || !_renderer.TryGetHeadPose(out position, out rotation, out scale))
            { ResetObservation(); return false; }
            if ((_source == null || !_source.gameObject.activeInHierarchy) && Time.unscaledTime >= _nextSourceSearch)
            {
                _nextSourceSearch = Time.unscaledTime + SourceSearchSeconds;
                _source = UnityEngine.Object.FindObjectOfType<DiskCardGame.TextDisplayer>();
                Hook(_source);
                ResetObservation();
            }
            if (_source == null || !_source.gameObject.activeInHierarchy) { ResetObservation(); return false; }
            text = _textMesh.GetValue(_source) as Text;
            if (text == null || !text.enabled || !text.gameObject.activeInHierarchy || !_source.Displaying ||
                String.IsNullOrEmpty(text.text))
            { ResetObservation(); return false; }
            bool input = Convert.ToInt32(_source.CurrentAdvanceMode) == 1;
            bool acknowledged = _pulseSent && !(bool)_continuePressed.GetValue(_source);
            if (!_observedVisible || _observedText != text.text || _observedInput != input || acknowledged)
            {
                _visibleSince = Time.time;
                _visibleSinceRealtime = Time.realtimeSinceStartup;
                _pulseSent = false;
                _readSeconds = -1f;
            }
            _observedVisible = true;
            _observedInput = input;
            _observedText = text.text;
            return true;
        }

        private void CreatePanel()
        {
            _panel = new GameObject("InscryptionVR Subtitle Panel", typeof(RectTransform), typeof(Canvas));
            _panel.layer = SubtitleLayer;
            _panel.transform.SetParent(transform, false);
            var rectangle = (RectTransform)_panel.transform;
            rectangle.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            Canvas canvas = _panel.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32000;
            Shader shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("Unity UI shader unavailable");
            _material = new Material(shader);
            _material.name = "InscryptionVR Subtitle Material";
            _material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            GameObject background = MakeUiObject("Background", _panel.transform);
            RectTransform backgroundRect = (RectTransform)background.transform;
            backgroundRect.anchorMin = Vector2.zero; backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero; backgroundRect.offsetMax = Vector2.zero;
            _background = background.AddComponent<Image>();
            _background.color = new Color(0.015f, 0.015f, 0.015f, 0.88f);
            _background.raycastTarget = false;
            _background.material = _material;
            _subtitle = MakeText("Message", Vector2.zero, new Vector2(TextWidth, TextHeight), PanelFontSize);
            GameObject pixels = MakeUiObject("Pixels", _panel.transform);
            _pixelImage = pixels.AddComponent<RawImage>();
            _pixelImage.raycastTarget = false;
            _pixelImage.material = _material;
            _pixelImage.enabled = false;
            _styleApplied = false;
            _panel.SetActive(false);
        }

        private Text MakeText(string name, Vector2 position, Vector2 size, int fontSize)
        {
            GameObject child = MakeUiObject(name, _panel.transform);
            RectTransform rectangle = (RectTransform)child.transform;
            rectangle.anchoredPosition = position;
            rectangle.sizeDelta = size;
            Text text = child.AddComponent<Text>();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = PanelMinFontSize;
            text.resizeTextMaxSize = fontSize;
            text.raycastTarget = false;
            text.material = _material;
            return text;
        }

        private static GameObject MakeUiObject(string name, Transform parent)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = SubtitleLayer;
            child.transform.SetParent(parent, false);
            return child;
        }

        private void ResetObservation()
        { _observedVisible = false; _observedInput = false; _observedText = null; _pulseSent = false; _readSeconds = -1f; }
        private void Hide()
        {
            _placed = false;
            if (_panel != null) _panel.SetActive(false);
            if (_stage != null && _stage.activeSelf) { _stage.SetActive(false); _renderedKey = null; }
        }
        private void Fail(Exception exception)
        {
            _failed = true;
            Hide();
            if (_logger != null) _logger.LogError("VR dialogue adapter disabled: " + exception);
        }
        private void FailVisual(Exception exception)
        {
            _showSubtitles = false;
            Hide();
            if (_logger != null) _logger.LogWarning("VR subtitles disabled; dialogue input remains available: " + exception);
        }
        private void OnDisable() { Hide(); ResetObservation(); }
        private void OnDestroy()
        {
            Unhook();
            DestroyStage();
            if (_panel != null) Destroy(_panel);
            if (_material != null) Destroy(_material);
        }
    }

    internal sealed class VrDialogueBindings
    {
        private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        internal readonly FieldInfo TextMesh, ContinuePressed, CurrentAudio, TextShadow, TextAnimation;
        internal VrDialogueBindings()
        {
            Type type = typeof(DiskCardGame.TextDisplayer);
            TextMesh = RequiredField(type, "textMesh", InstanceMembers);
            ContinuePressed = RequiredField(type, "continuePressed", InstanceMembers);
            CurrentAudio = RequiredField(type, "currentAudio", InstanceMembers);
            TextShadow = type.GetField("textShadow", InstanceMembers);
            TextAnimation = type.GetField("textAnimation", InstanceMembers);
        }
        private static FieldInfo RequiredField(Type type, string name, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            return field;
        }
    }
}
