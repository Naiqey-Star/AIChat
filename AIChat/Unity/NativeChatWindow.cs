using AIChat.Interop;
using AIChat.Core;
using AIChat.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AIChat.Unity
{
    // Reuse loaded game assets without binding the plugin to a particular game assembly version.
    public sealed class NativeChatWindow : IDisposable
    {
        private readonly IAIChatPublicApi _api;
        private readonly Func<bool> _recording;
        private readonly Func<string> _status;
        private readonly List<GameObject> _pages = new List<GameObject>();
        private readonly List<Action> _reloadFields = new List<Action>();
        private readonly List<Image> _tabs = new List<Image>();
        private readonly List<Selectable> _settings = new List<Selectable>();
        private readonly Color _ink = new Color(0.89f, 0.88f, 0.95f);
        private readonly Color _muted = new Color(0.66f, 0.66f, 0.77f);
        private readonly Color _accent = new Color(0.86f, 0.24f, 0.56f);
        private readonly Color _surface = new Color(0.12f, 0.11f, 0.19f, 0.97f);
        private GameObject _root;
        private RectTransform _window;
        private Type _tmpTextType;
        private Type _tmpInputType;
        private object _font;
        private Material _fontMaterial;
        private Sprite _buttonSprite;
        private Sprite _submitSprite;
        private TextHandle _statusText;
        private TextHandle _title;
        private TextHandle _settingsCaption;
        private Button _settingsButton;
        private Image _background;
        private float _configuredWidth = 610;
        private float _configuredHeight = 748;
        private int _lastPointerFrame = -2;
        private bool _resizing;
        private bool _disposing;
        private TextHandle _callText;
        private TextHandle _recordText;
        private InputHandle _input;
        private Button _send;
        private Button _record;
        private Button _call;
        private ScrollRect _history;
        private Transform _messages;
        private bool _visible;
        private int _page;
        private float _scrollNextFrame = -1;
        private bool _replyShown;
        private string _feedback;
        private float _feedbackUntil;

        public NativeChatWindow(IAIChatPublicApi api, Func<bool> recording, Func<string> status)
        {
            _api = api;
            _recording = recording;
            _status = status;
            try
            {
                LoadTheme();
                Build();
                _api.ConversationCompleted += OnConversationCompleted;
            }
            catch { Dispose(); throw; }
        }

        private void LoadTheme()
        {
            _tmpTextType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TMPro.TextMeshProUGUI")).FirstOrDefault(t => t != null);
            _tmpInputType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("TMPro.TMP_InputField")).FirstOrDefault(t => t != null);
            var components = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            var setting = components.FirstOrDefault(c => c != null && c.GetType().FullName == "Bulbul.SettingUI");
            Component text = ReadField(setting, "_generalText") as Component;
            if (text == null && _tmpTextType != null)
                text = Resources.FindObjectsOfTypeAll(_tmpTextType).OfType<Component>().FirstOrDefault(c => c != null);
            if (text != null)
            {
                _font = Get(text, "font");
                _fontMaterial = Get(text, "fontSharedMaterial") as Material;
            }
            var dialogButton = components.FirstOrDefault(c => c != null && c.GetType().FullName == "Bulbul.CommonDialogButtonView");
            _buttonSprite = ReadField(dialogButton, "normalStyle") as Sprite;
            _submitSprite = ReadField(dialogButton, "submitStyle") as Sprite;
            Log.Info("[NativeUI] 游戏主题: font=" + (_font as UnityEngine.Object)?.name +
                ", button=" + _buttonSprite?.name);
        }

        private void Build()
        {
            _root = new GameObject("AIChat.NativeUI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 2500;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = 0.5f;

            _window = Rect("ChatWindow", _root.transform);
            _window.anchorMin = _window.anchorMax = _window.pivot = new Vector2(0, 0.5f);
            _window.anchoredPosition = new Vector2(32, 0);
            _window.sizeDelta = new Vector2(610, 748);
            _background = Paint(_window, null, _surface);

            var header = Region("Header", _window, 20, 16, -20, 68);
            Paint(header, null, new Color(1, 1, 1, 0.03f));
            header.gameObject.AddComponent<NativeWindowDrag>().Target = _window;
            _title = Label(header, "与聪音对话", 28);
            _title.Rect.anchorMax = new Vector2(1, 1);
            _title.Rect.offsetMax = new Vector2(-60, 0);
            var close = Button(header, "×", () => { _api.SetConsoleVisible(false, out _); });
            PlaceRight(close.GetComponent<RectTransform>(), 42, 42);

            var tabRow = Region("Tabs", _window, 20, 78, -20, 42);
            Horizontal(tabRow, 6);
            string[] titles = { "对话", "连接", "语音", "通话", "界面", "人设" };
            for (int i = 0; i < titles.Length; i++)
            {
                int selected = i;
                var button = Button(tabRow, titles[i], () => SelectPage(selected));
                _tabs.Add(button.image);
                Flexible(button.transform);
            }
            var area = Rect("Pages", _window);
            Stretch(area, 20, 130, -20, -76);
            for (int i = 0; i < titles.Length; i++)
            {
                var page = Rect(titles[i], area);
                Stretch(page);
                _pages.Add(page.gameObject);
            }
            BuildChat(_pages[0].transform);
            BuildConnection(_pages[1].transform);
            BuildVoice(_pages[2].transform);
            BuildCall(_pages[3].transform);
            BuildDisplay(_pages[4].transform);
            BuildPersona(_pages[5].transform);

            var footer = Region("Footer", _window, 22, 0, -22, 52);
            footer.anchorMin = footer.anchorMax = new Vector2(0, 0);
            footer.anchorMax = new Vector2(1, 0);
            footer.pivot = new Vector2(0.5f, 0);
            footer.anchoredPosition = new Vector2(0, 12);
            _statusText = Label(footer, "就绪", 17, _muted);
            _statusText.Rect.offsetMax = new Vector2(-125, 0);
            _settingsButton = Button(footer, "设置", () => { if (_page == 0) SelectPage(1); else SaveSettings(); }, out _settingsCaption);
            PlaceRight(_settingsButton.GetComponent<RectTransform>(), 118, 40);
            BuildResizeFrame();
            SelectPage(0);
        }

        private void BuildResizeFrame()
        {
            AddResizeHandle(ResizeEdges.Left, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 16), new Vector2(12, -16));
            AddResizeHandle(ResizeEdges.Right, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-12, 16), new Vector2(0, -16));
            AddResizeHandle(ResizeEdges.Bottom, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 0), new Vector2(-16, 12));
            AddResizeHandle(ResizeEdges.Top, new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -12), new Vector2(-16, 0));
            AddResizeHandle(ResizeEdges.Left | ResizeEdges.Bottom, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(16, 16));
            AddResizeHandle(ResizeEdges.Right | ResizeEdges.Bottom, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-16, 0), new Vector2(0, 16));
            AddResizeHandle(ResizeEdges.Left | ResizeEdges.Top, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -16), new Vector2(16, 0));
            AddResizeHandle(ResizeEdges.Right | ResizeEdges.Top, Vector2.one, Vector2.one, new Vector2(-16, -16), Vector2.zero);
        }

        private void AddResizeHandle(ResizeEdges edges, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var handle = Rect("Resize-" + edges, _window);
            handle.anchorMin = anchorMin;
            handle.anchorMax = anchorMax;
            handle.offsetMin = offsetMin;
            handle.offsetMax = offsetMax;
            Paint(handle, null, new Color(0, 0, 0, 0.01f));
            var indicator = Rect("Border", handle);
            Stretch(indicator, 3, 3, -3, -3);
            bool corner = anchorMin == anchorMax;
            if (!corner && anchorMin.x == anchorMax.x)
            {
                indicator.anchorMin = new Vector2(0.5f, 0);
                indicator.anchorMax = new Vector2(0.5f, 1);
                indicator.sizeDelta = new Vector2(1.5f, -6);
            }
            else if (!corner)
            {
                indicator.anchorMin = new Vector2(0, 0.5f);
                indicator.anchorMax = new Vector2(1, 0.5f);
                indicator.sizeDelta = new Vector2(-6, 1.5f);
            }
            var image = Paint(indicator, null, new Color(0.55f, 0.52f, 0.68f, 0.55f));
            image.raycastTarget = false;
            var resize = handle.gameObject.AddComponent<NativeWindowResize>();
            resize.Target = _window;
            resize.Edges = edges;
            resize.Indicator = image;
            resize.Started = () => _resizing = true;
            resize.Finished = () => {
                _resizing = false;
                if (_disposing) return;
                SetConfig("NativeWindowWidth", _window.rect.width.ToString("0.##"));
                SetConfig("NativeWindowHeight", _window.rect.height.ToString("0.##"));
                ReloadFields();
                if (!_api.TrySaveConfig(out var error)) Feedback("尺寸保存失败：" + error);
            };
        }

        private void BuildChat(Transform page)
        {
            var historyRect = Rect("History", page);
            Stretch(historyRect, 0, 0, 0, -164);
            _history = Scroll(historyRect, out _messages);
            var composer = Rect("Composer", page);
            composer.anchorMin = new Vector2(0, 0);
            composer.anchorMax = new Vector2(1, 0);
            composer.pivot = new Vector2(0.5f, 0);
            composer.offsetMin = new Vector2(0, 62);
            composer.offsetMax = new Vector2(0, 150);
            _input = Input(composer, string.Empty, "说点什么…", true, false, null);
            var actions = Rect("Actions", page);
            actions.anchorMin = Vector2.zero;
            actions.anchorMax = new Vector2(1, 0);
            actions.pivot = new Vector2(0.5f, 0);
            actions.offsetMin = Vector2.zero;
            actions.offsetMax = new Vector2(0, 44);
            Horizontal(actions, 10);
            _record = Button(actions, "开始录音", ToggleRecord, out _recordText);
            Flexible(_record.transform);
            _call = Button(actions, "开始通话", ToggleCall, out _callText);
            Flexible(_call.transform);
            _send = Button(actions, "发送", Send, true);
            Flexible(_send.transform);
        }

        private void BuildConnection(Transform page)
        {
            Scroll(page as RectTransform, out var content);
            Field(content, "API 地址", "API_URL");
            Field(content, "模型", "ModelName");
            Field(content, "API Key", "API_Key", true);
            Switch(content, "JSON 结构化回复", "StructuredResponse");
            Switch(content, "本地 Ollama", "Use_Ollama_API");
            TextRow(content, "思考模式", 20, _muted, 32);
            var modes = Rect("ThinkingMode", content);
            Height(modes, 42);
            Horizontal(modes, 8);
            string[] values = { "Default", "Enable", "Disable" };
            string[] names = { "默认", "开启", "关闭" };
            var buttons = new List<Image>();
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                var button = Button(modes, names[i], () => { SetConfig("ThinkMode", value); ReloadFields(); });
                Flexible(button.transform);
                _settings.Add(button);
                buttons.Add(button.image);
            }
            _reloadFields.Add(() => {
                for (int i = 0; i < values.Length; i++) buttons[i].color = _api.GetConfigValue("ThinkMode") == values[i] ? _accent : new Color(0.24f, 0.23f, 0.34f);
            });
            Switch(content, "修正 Ollama API 路径", "FixApiPathForThinkMode");
            Switch(content, "记录 API 请求正文", "LogApiRequestBody");
        }

        private void BuildVoice(Transform page)
        {
            Scroll(page as RectTransform, out var content);
            Switch(content, "语音合成", "EnableTTS");
            Field(content, "GPT-SoVITS 地址", "TTS_Service_URL");
            Field(content, "启动脚本路径", "TTS_Service_Script_Path");
            Switch(content, "启动时运行语音服务", "LaunchTTSService");
            Switch(content, "退出时关闭语音服务", "QuitTTSServiceOnQuit");
            Range(content, "语音音量", "VoiceVolume", 0, 1);
            Field(content, "语音语言", "TargetLang");
            Field(content, "参考音频", "Audio_File_Path");
            Field(content, "参考音频台词", "Audio_File_Text", multiline: true);
            Field(content, "参考音频语言", "PromptLang");
            Switch(content, "检查参考音频路径", "AudioPathCheck");
            Switch(content, "检测日文语音", "JapaneseCheck");
        }

        private void BuildCall(Transform page)
        {
            Scroll(page as RectTransform, out var content);
            Switch(content, "持续通话", "Enable_VoiceCall");
            Switch(content, "允许语音打断", "VoiceCall_BargeIn");
            Field(content, "VAD 能量阈值", "Vad_EnergyThreshold", numeric: true);
            Field(content, "最短有效语音 / 秒", "Vad_MinSpeechSeconds", numeric: true);
            Field(content, "停顿判定时间 / 秒", "Vad_SilenceSeconds", numeric: true);
            Range(content, "结束阈值比例", "Vad_EndThresholdRatio", 0.1f, 1);
            Field(content, "恢复监听延迟 / 秒", "VoiceCall_ResumeDelay", numeric: true);
        }

        private void BuildDisplay(Transform page)
        {
            Scroll(page as RectTransform, out var content);
            Field(content, "窗口宽度", "NativeWindowWidth", numeric: true);
            Field(content, "窗口高度", "NativeWindowHeight", numeric: true);
            Range(content, "背景不透明度", "BackgroundOpacity", 0.2f, 1);
            Switch(content, "显示标题", "ShowWindowTitle");
            Switch(content, "反转回车发送行为", "ReverseEnterBehavior");
            Switch(content, "静音原生角色", "MuteGameNativeAudio");
            Switch(content, "显示游戏内聊天按钮", "AddChatButton");
        }

        private void BuildPersona(Transform page)
        {
            Scroll(page as RectTransform, out var content);
            Switch(content, "微调模型模式", "UseFinetunedModel");
            Switch(content, "启用记忆", "ExperimentalMemory");
            Field(content, "人设 / System Prompt", "SystemPrompt", multiline: true, height: 260);
            ConfirmedCommand(content, "恢复默认人设", "替换当前人设？", () => {
                if (_api.GetAllConfigDefaultValues().TryGetValue("SystemPrompt", out var prompt)) SetConfig("SystemPrompt", prompt);
                ReloadFields();
            });
            ConfirmedCommand(content, "清除所有记忆", "确认清除所有记忆？", () => {
                Feedback(_api.TryClearMemory(out var error) ? "记忆已清除" : "清除失败：" + error);
            });
        }

        private void ConfirmedCommand(Transform parent, string title, string question, Action action)
        {
            var confirm = Rect("Confirmation", parent);
            Height(confirm, 90);
            var label = Label(confirm, question, 19);
            Stretch(label.Rect, 0, 0, 0, -48);
            var buttons = Region("Actions", confirm, 0, 44, 0, 42);
            Horizontal(buttons, 10);
            var cancel = Button(buttons, "取消", () => confirm.gameObject.SetActive(false));
            Flexible(cancel.transform);
            var apply = Button(buttons, "确认", () => { action(); confirm.gameObject.SetActive(false); }, true);
            Flexible(apply.transform);
            var command = Button(parent, title, () => confirm.gameObject.SetActive(true));
            Height(command.transform, 44);
            confirm.SetAsLastSibling();
            confirm.gameObject.SetActive(false);
            _settings.Add(command);
            _settings.Add(apply);
        }

        private void Range(Transform parent, string title, string key, float minimum, float maximum)
        {
            var caption = TextRow(parent, title, 18, _muted, 28);
            var row = Rect(key, parent);
            Height(row, 34);
            var track = Rect("Track", row);
            track.anchorMin = new Vector2(0, 0.4f);
            track.anchorMax = new Vector2(1, 0.6f);
            track.offsetMin = new Vector2(12, 0);
            track.offsetMax = new Vector2(-12, 0);
            Paint(track, null, new Color(0.34f, 0.32f, 0.44f));
            var handleArea = Rect("HandleArea", row);
            Stretch(handleArea, 12, 0, -12, 0);
            var handle = Rect("Handle", handleArea);
            handle.sizeDelta = new Vector2(18, 0);
            var slider = row.gameObject.AddComponent<Slider>();
            slider.handleRect = handle;
            slider.targetGraphic = Paint(handle, _buttonSprite, _accent);
            slider.minValue = minimum;
            slider.maxValue = maximum;
            slider.onValueChanged.AddListener(value => {
                SetConfig(key, value.ToString("0.###"));
                caption.Text = title + "  " + value.ToString("0.##");
            });
            _reloadFields.Add(() => {
                float value = Number(key, minimum);
                slider.SetValueWithoutNotify(value);
                caption.Text = title + "  " + value.ToString("0.##");
            });
            _settings.Add(slider);
        }

        private void Field(Transform parent, string title, string key, bool password = false, bool multiline = false, bool numeric = false, float height = 100)
        {
            TextRow(parent, title, 18, _muted, 28);
            var holder = Rect(key, parent);
            Height(holder, multiline ? height : 42);
            var input = Input(holder, _api.GetConfigValue(key) ?? string.Empty, string.Empty, multiline, password,
                value => { SetConfig(key, value); ReloadFields(); }, numeric, key == "SystemPrompt");
            _settings.Add(input.Selectable);
            _reloadFields.Add(() => input.SetText(_api.GetConfigValue(key) ?? string.Empty));
        }

        private void Switch(Transform parent, string title, string key)
        {
            var row = Rect(key, parent);
            Height(row, 38);
            var toggle = row.gameObject.AddComponent<Toggle>();
            var box = Rect("Box", row);
            box.anchorMin = box.anchorMax = new Vector2(0, 0.5f);
            box.sizeDelta = new Vector2(25, 25);
            box.anchoredPosition = new Vector2(14, 0);
            toggle.targetGraphic = Paint(box, _buttonSprite, new Color(0.35f, 0.34f, 0.46f));
            var check = Rect("Check", box);
            Stretch(check, 5, 5, -5, -5);
            toggle.graphic = Paint(check, null, _accent);
            var label = Label(row, title, 20);
            label.Rect.offsetMin = new Vector2(42, 0);
            toggle.isOn = string.Equals(_api.GetConfigValue(key), "True", StringComparison.OrdinalIgnoreCase);
            toggle.onValueChanged.AddListener(value => { SetConfig(key, value.ToString()); ReloadFields(); });
            _reloadFields.Add(() => toggle.SetIsOnWithoutNotify(string.Equals(_api.GetConfigValue(key), "True", StringComparison.OrdinalIgnoreCase)));
            _settings.Add(toggle);
        }

        private void SetConfig(string key, string value)
        {
            if (!_api.TrySetConfigValue(key, value, out var error)) Feedback("设置无效：" + error);
            ApplyAppearance();
        }

        private void SaveSettings()
        {
            if (_api.TrySaveConfig(out var error)) Feedback("已保存");
            else Feedback("保存失败：" + error);
        }

        private void Feedback(string text)
        {
            _feedback = text;
            _feedbackUntil = Time.unscaledTime + 4f;
        }

        private float Number(string key, float fallback)
        {
            return float.TryParse(_api.GetConfigValue(key), out var value) && !float.IsNaN(value) && !float.IsInfinity(value) ? value : fallback;
        }

        private void ApplyAppearance()
        {
            _configuredWidth = Mathf.Max(500, Number("NativeWindowWidth", 610));
            _configuredHeight = Mathf.Max(500, Number("NativeWindowHeight", 748));
            _background.color = new Color(_surface.r, _surface.g, _surface.b, Mathf.Clamp01(Number("BackgroundOpacity", 0.95f)));
            _title.Rect.gameObject.SetActive(!string.Equals(_api.GetConfigValue("ShowWindowTitle"), "False", StringComparison.OrdinalIgnoreCase));
        }

        private void ReloadFields() { foreach (var reload in _reloadFields) reload(); ApplyAppearance(); }

        private void SelectPage(int page)
        {
            _page = page;
            _settingsCaption.Text = page == 0 ? "设置" : "保存配置";
            _title.Text = page == 0 ? "与聪音对话" : "设置";
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].SetActive(i == page);
                _tabs[i].color = i == page ? _accent : new Color(0.24f, 0.23f, 0.34f, 0.95f);
            }
            ReloadFields();
        }

        public void Refresh(bool visible)
        {
            if (_root == null) return;
            if (_visible != visible)
            {
                _visible = visible;
                _root.SetActive(visible);
                if (visible) ReloadFields();
            }
            if (!visible) return;
            var canvasRect = _root.GetComponent<RectTransform>().rect;
            // Keep click targets stationary until the pointer-up event has been processed.
            if (UnityEngine.Input.GetMouseButton(0)) _lastPointerFrame = Time.frameCount;
            if (!_resizing && Time.frameCount > _lastPointerFrame + 1)
            {
                float widthLimit = Mathf.Max(1, canvasRect.width - 24);
                float heightLimit = Mathf.Max(1, canvasRect.height - 24);
                _window.sizeDelta = new Vector2(Mathf.Clamp(_configuredWidth, Mathf.Min(500, widthLimit), widthLimit),
                    Mathf.Clamp(_configuredHeight, Mathf.Min(500, heightLimit), heightLimit));
            }
            NativeWindowDrag.Clamp(_window);
            _statusText.Text = Time.unscaledTime < _feedbackUntil ? _feedback : _status();
            _send.interactable = !_api.IsBusy && !_recording() && !string.IsNullOrWhiteSpace(_input.Text);
            _record.interactable = !_api.IsBusy && !_api.IsContinuousCallActive;
            _call.interactable = !_recording() || _api.IsContinuousCallActive;
            _recordText.Text = _recording() && !_api.IsContinuousCallActive ? "结束并发送" : "开始录音";
            _callText.Text = _api.IsContinuousCallActive ? "结束通话" : "开始通话";
            foreach (var control in _settings) control.interactable = !_api.IsBusy;
            _settingsButton.interactable = _page == 0 || !_api.IsBusy;
            if (_page == 0 && _input.IsFocused && InputEnter()) Send();
            if (_scrollNextFrame >= 0 && Time.frameCount > _scrollNextFrame)
            {
                Canvas.ForceUpdateCanvases();
                _history.verticalNormalizedPosition = 0;
                _scrollNextFrame = -1;
            }
        }

        private bool InputEnter()
        {
            if (!UnityEngine.Input.GetKeyDown(KeyCode.Return) && !UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) return false;
            bool ctrl = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (!string.IsNullOrEmpty(UnityEngine.Input.compositionString)) return false;
            bool reverse = string.Equals(_api.GetConfigValue("ReverseEnterBehavior"), "True", StringComparison.OrdinalIgnoreCase);
            return reverse ? shift : !ctrl && !shift;
        }

        private void Send()
        {
            if (_api.IsBusy || _recording()) return;
            string message = _input.Text.Trim();
            if (message.Length == 0) return;
            if (_api.TryStartTextConversation(message, "native-ui", out var error)) _input.SetText(string.Empty);
            else AddMessage("提示", error);
        }

        private void ToggleRecord()
        {
            string error;
            bool ok = _recording() ? _api.TryStopVoiceCaptureAndSend("native-ui", out error) : _api.TryStartVoiceCapture(out error);
            if (!ok) AddMessage("提示", error);
            ReloadFields();
        }

        private void ToggleCall()
        {
            string error;
            bool ok = _api.IsContinuousCallActive ? _api.TryStopContinuousCall(out error) : _api.TryStartContinuousCall(out error);
            if (!ok) AddMessage("提示", error);
            ReloadFields();
        }

        private void OnConversationCompleted(AIChatApiConversationResult result)
        {
            if (result.IsApiError || !_replyShown)
                AddMessage(result.IsApiError ? "请求失败" : "聪音", result.IsApiError ? result.ErrorMessage : result.SubtitleText);
            if (result.TtsAttempted && !result.TtsSucceeded) Feedback("语音合成失败，已显示文字回复");
        }

        public void AddMessage(string speaker, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || _messages == null) return;
            if (speaker == "你") _replyShown = false;
            if (speaker == "聪音") _replyShown = true;
            var row = Rect("Message", _messages);
            Vertical(row, 5, 8);
            var fit = row.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            TextRow(row, speaker, 16, speaker == "你" ? new Color(0.4f, 0.79f, 0.82f) : new Color(0.93f, 0.5f, 0.71f), 24);
            var body = Label(row, message, 21);
            var layout = body.Rect.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 30;
            if (_messages.childCount > 60) UnityEngine.Object.Destroy(_messages.GetChild(0).gameObject);
            _scrollNextFrame = Time.frameCount;
        }

        private InputHandle Input(Transform parent, string value, string placeholder, bool multiline, bool password, Action<string> changed, bool numeric = false, bool unlimited = false)
        {
            var root = Rect("Input", parent);
            Stretch(root);
            var image = Paint(root, _buttonSprite, new Color(0.21f, 0.20f, 0.30f, 0.97f));
            var viewport = Rect("Viewport", root);
            Stretch(viewport, 12, 8, -12, -8);
            viewport.gameObject.AddComponent<RectMask2D>();
            var text = Label(viewport, string.Empty, 20);
            var hint = Label(viewport, placeholder, 20, _muted);
            Component field;
            if (_tmpInputType != null && text.Component.GetType() == _tmpTextType)
            {
                field = root.gameObject.AddComponent(_tmpInputType);
                Set(field, "textViewport", viewport);
                Set(field, "textComponent", text.Component);
                Set(field, "placeholder", hint.Component);
            }
            else
            {
                var legacy = root.gameObject.AddComponent<InputField>();
                legacy.textComponent = text.Component as Text;
                legacy.placeholder = hint.Component as Graphic;
                field = legacy;
            }
            Set(field, "lineType", multiline ? "MultiLineNewline" : "SingleLine");
            if (password) Set(field, "contentType", "Password");
            else if (numeric) Set(field, "contentType", "DecimalNumber");
            Set(field, "characterLimit", unlimited ? 0 : multiline ? 8000 : 4096);
            var selectable = (Selectable)field;
            selectable.targetGraphic = image;
            var handle = new InputHandle(field);
            handle.SetText(value);
            if (changed != null)
            {
                var onEndEdit = Get(field, "onEndEdit") as UnityEvent<string>;
                onEndEdit?.AddListener(textValue => changed(textValue));
            }
            return handle;
        }

        private ScrollRect Scroll(RectTransform parent, out Transform content)
        {
            var scroll = parent.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 30;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = Rect("Viewport", parent);
            Stretch(viewport);
            Paint(viewport, null, new Color(0, 0, 0, 0.01f));
            viewport.gameObject.AddComponent<RectMask2D>();
            var inner = Rect("Content", viewport);
            inner.anchorMin = new Vector2(0, 1);
            inner.anchorMax = new Vector2(1, 1);
            inner.pivot = new Vector2(0.5f, 1);
            inner.sizeDelta = Vector2.zero;
            Vertical(inner, 8, 4);
            inner.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = inner;
            var track = Rect("Scrollbar", parent);
            track.anchorMin = new Vector2(1, 0);
            track.anchorMax = new Vector2(1, 1);
            track.pivot = new Vector2(1, 0.5f);
            track.sizeDelta = new Vector2(8, 0);
            track.anchoredPosition = Vector2.zero;
            Paint(track, null, new Color(0.25f, 0.24f, 0.35f, 0.4f));
            var handle = Rect("Handle", track);
            Stretch(handle);
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.handleRect = handle;
            bar.targetGraphic = Paint(handle, null, new Color(0.56f, 0.53f, 0.69f));
            bar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = bar;
            viewport.offsetMax = new Vector2(-16, 0);
            content = inner;
            return scroll;
        }

        private Button Button(Transform parent, string text, Action clicked, bool primary = false)
        {
            return Button(parent, text, clicked, out _, primary);
        }

        private Button Button(Transform parent, string text, Action clicked, out TextHandle caption, bool primary = false)
        {
            var rect = Rect(text, parent);
            var image = Paint(rect, primary ? _submitSprite : _buttonSprite, primary ? _accent : new Color(0.27f, 0.26f, 0.39f));
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.65f);
            button.colors = colors;
            button.onClick.AddListener(() => clicked());
            caption = Label(rect, text, 19, null, true);
            Stretch(caption.Rect, 8, 3, -8, -3);
            return button;
        }

        private TextHandle TextRow(Transform parent, string text, float size, Color color, float height)
        {
            var result = Label(parent, text, size, color);
            Height(result.Rect, height);
            return result;
        }

        private TextHandle Label(Transform parent, string text, float size, Color? color = null, bool center = false)
        {
            var rect = Rect("Text", parent);
            Stretch(rect);
            Component component;
            if (_tmpTextType != null && _font != null)
            {
                component = rect.gameObject.AddComponent(_tmpTextType);
                Set(component, "font", _font);
                if (_fontMaterial != null) Set(component, "fontSharedMaterial", _fontMaterial);
                Set(component, "fontSize", size);
                Set(component, "richText", false);
                Set(component, "enableWordWrapping", true);
                Set(component, "alignment", center ? "Center" : "TopLeft");
                Set(component, "color", color ?? _ink);
            }
            else
            {
                var label = rect.gameObject.AddComponent<Text>();
                label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                label.fontSize = (int)size;
                label.supportRichText = false;
                label.alignment = center ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
                label.color = color ?? _ink;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                component = label;
            }
            ((Graphic)component).raycastTarget = false;
            var handle = new TextHandle(component);
            handle.Text = text;
            return handle;
        }

        private static object ReadField(object instance, string name) => instance?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
        private static object Get(object instance, string name) => instance?.GetType().GetProperty(name)?.GetValue(instance, null);
        private static void Set(object instance, string name, object value)
        {
            var property = instance.GetType().GetProperty(name);
            if (property == null || !property.CanWrite) return;
            if (property.PropertyType.IsEnum && value is string enumName) value = Enum.Parse(property.PropertyType, enumName);
            property.SetValue(instance, value, null);
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }
        private static void Stretch(RectTransform rect, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, -bottom);
            rect.offsetMax = new Vector2(right, -top);
        }
        private static RectTransform Region(string name, Transform parent, float left, float top, float right, float height)
        {
            var rect = Rect(name, parent);
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.offsetMin = new Vector2(left, -top - height);
            rect.offsetMax = new Vector2(right, -top);
            return rect;
        }
        private static void PlaceRight(RectTransform rect, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(1, 0.5f);
            rect.pivot = new Vector2(1, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = Vector2.zero;
        }
        private static Image Paint(RectTransform rect, Sprite sprite, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            return image;
        }
        private static void Horizontal(Transform transform, float spacing)
        {
            var layout = transform.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }
        private static void Vertical(Transform transform, float spacing, int padding)
        {
            var layout = transform.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
        }
        private static void Height(Transform transform, float height)
        {
            var layout = transform.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = height;
        }
        private static void Flexible(Transform transform) { transform.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1; }

        public void Dispose()
        {
            _disposing = true;
            _api.ConversationCompleted -= OnConversationCompleted;
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }

        private sealed class TextHandle
        {
            public Component Component { get; }
            public RectTransform Rect => Component.transform as RectTransform;
            private string _lastText;
            public TextHandle(Component component) { Component = component; }
            public string Text { set { if (_lastText != value) { Set(Component, "text", value); _lastText = value; } } }
        }
        private sealed class InputHandle
        {
            private readonly Component _field;
            public InputHandle(Component field) { _field = field; }
            public Selectable Selectable => (Selectable)_field;
            public string Text => (string)Get(_field, "text") ?? string.Empty;
            public bool IsFocused => (bool)(Get(_field, "isFocused") ?? false);
            public void SetText(string text)
            {
                var method = _field.GetType().GetMethod("SetTextWithoutNotify", new[] { typeof(string) });
                if (method != null) method.Invoke(_field, new object[] { text });
                else Set(_field, "text", text);
            }
        }
    }
}
