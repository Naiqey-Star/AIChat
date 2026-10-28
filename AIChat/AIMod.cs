using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using System.Diagnostics;
using System.Linq;
using AIChat.Core;
using AIChat.Services;
using AIChat.Unity;
using AIChat.Interop;
using AIChat.Utils;

namespace ChillAIMod
{
    [BepInPlugin("com.username.chillaimod", "Chill AI Mod", AIChat.Version.VersionString)]
    public class AIMod : BaseUnityPlugin, IAIChatPublicApi
    {
        public static AIMod Instance { get; private set; }

        public event Action<AIChatApiConversationResult> ConversationCompleted;

        /// <summary>每显示一句字幕就触发一次，参数是这句的中文文本。</summary>
        public event Action<string> SegmentShown;

        /// <summary>最近一次显示的句子（供不支持事件的外部轮询）。</summary>
        public string LastSegmentSubtitle { get; private set; } = string.Empty;

        public string ApiVersion => "1.0.0";
        public bool IsBusy => _isProcessing;
        public bool IsReady => _isTTSServiceReady;
        public bool IsContinuousCallActive => _continuousCallActive;
        // ================= 【配置项】 =================
        private ConfigEntry<bool> _useOllama;
        private ConfigEntry<ThinkMode> _thinkModeConfig;
        private ConfigEntry<string> _apiKeyConfig;
        private ConfigEntry<string> _modelConfig;
        private ConfigEntry<string> _sovitsUrlConfig;
        private ConfigEntry<string> _refAudioPathConfig;
        private ConfigEntry<string> _promptTextConfig;
        private ConfigEntry<string> _promptLangConfig;
        private ConfigEntry<string> _targetLangConfig;
        private ConfigEntry<string> _personaConfig;
        private ConfigEntry<string> _chatApiUrlConfig;

        private ConfigEntry<string> _TTSServicePathConfig;
        private ConfigEntry<bool> _LaunchTTSServiceConfig;
        private ConfigEntry<bool> _quitTTSServiceOnQuitConfig;
        private ConfigEntry<bool> _audioPathCheckConfig;
        private ConfigEntry<bool> _japaneseCheckConfig;

        // --- 新增窗口大小配置 ---
        private ConfigEntry<float> _windowWidthConfig;
        private ConfigEntry<float> _windowHeightConfig;

        // --- 新增音量配置 ---
        private ConfigEntry<float> _voiceVolumeConfig;

        // --- 逐句合成配置 ---
        private ConfigEntry<bool> _segmentModeConfig;
        private ConfigEntry<int> _segmentBufferConfig;
        private ConfigEntry<float> _segmentPrebufferMaxWaitConfig;
        private ConfigEntry<float> _segmentTimeoutConfig;
        private ConfigEntry<int> _segmentRetriesConfig;
        private ConfigEntry<int> _segmentParallelConfig;

        // 每次对话一个令牌：打断/新对话时自增，旧的合成协程据此自行退出
        private int _segmentSessionToken = 0;

        // --- 新增：实验性分层记忆系统 ---
        private ConfigEntry<bool> _experimentalMemoryConfig;
        private HierarchicalMemory _hierarchicalMemory;
        
        // --- 新增：日志记录设置 ---
        private ConfigEntry<bool> _logApiRequestBodyConfig;
        
        // --- 新增：API路径修正设置 ---
        private ConfigEntry<bool> _fixApiPathForThinkModeConfig;

        // --- 新增：快捷键配置 ---
        private ConfigEntry<bool> _reverseEnterBehaviorConfig;

        // --- 新增：背景透明配置 ---
        private ConfigEntry<float> _backgroundOpacity;
        
        // --- 新增：窗口标题显示配置 ---
        private ConfigEntry<bool> _showWindowTitle;
        private ConfigEntry<string> _aiAvatarPathConfig;
        private ConfigEntry<string> _playerAvatarPathConfig;

        // --- 新增：微调模式配置 ---
        private ConfigEntry<bool> _useFinetunedModel;

        // --- 新增：静音游戏原生音频配置 ---
        private ConfigEntry<bool> _muteGameNativeAudio;
        private List<AudioSource> _mutedGameSources = new List<AudioSource>();

        // --- 新增：添加聊天按钮配置 ---
        private ConfigEntry<bool> _addChatButtonConfig;

        // --- 新增：持续通话（电话模式）配置 ---
        private ConfigEntry<bool> _enableVoiceCallConfig;
        private ConfigEntry<float> _vadThresholdConfig;
        private ConfigEntry<float> _vadMinSpeechConfig;
        private ConfigEntry<float> _vadSilenceConfig;
        private ConfigEntry<float> _vadEndThresholdRatioConfig;
        private ConfigEntry<bool> _voiceCallBargeInConfig;
        private ConfigEntry<float> _voiceCallResumeDelayConfig;

        // 缓存游戏 UI 引用（避免 Find 在隐藏对象上失败）
        private Transform _cachedOriginalTextTrans = null;
        private GameObject _cachedCanvas = null;

        // --- 新增：各配置区域展开状态 ---
        private bool _showLlmSettings = false;
        private bool _showTtsSettings = false;
        private bool _showInterfaceSettings = false;
        private bool _showPersonaSettings = false;
        private bool _showVoiceCallSettings = false;

        // --- 录音相关变量 ---
        private AudioClip _recordingClip;
        private bool _isRecording = false;
        private string _microphoneDevice = null;
        private const int RecordingFrequency = 16000; // 16kHz 对 Whisper 足够且省带宽
        private const int MaxRecordingSeconds = 30;   // 最长录 30 秒

        // --- 新增：持续通话状态 ---
        private bool _continuousCallActive = false;
        private VoiceActivityDetector _vad;
        private int _lastMicrophonePosition = 0;
        private Coroutine _currentConversationCoroutine;
        private bool _abortCurrentConversation = false;
        private Coroutine _resumeListeningCoroutine;

        // 当前对话的 UI 句柄，用于打断时清理
        private GameObject _currentOverlayTextObj;
        private GameObject _currentOriginalTextObj;
        private Dictionary<GameObject, bool> _currentUiStatusMap;
        private bool _currentWasStoryUIHidden;

        // ================= 【UI 变量】 =================
        private bool _showInputWindow = false;
        private bool _showSettings = false;
        // 初始值在 Awake 中根据配置更新
        private Rect _windowRect = new Rect(0, 0, 500, 0);
        private Vector2 _scrollPosition = Vector2.zero;

        private string _playerInput = "";
        // ── 聊天记录（气泡） ─────────────────────────────────────
        private sealed class ChatBubble
        {
            public bool IsUser;
            public string Text;
        }

        private readonly List<ChatBubble> _chatLog = new List<ChatBubble>();
        private Vector2 _chatScroll = Vector2.zero;
        private bool _chatScrollToBottom;
        private GUIStyle _bubbleUserStyle;
        private GUIStyle _bubbleAiStyle;
        private Texture2D _bubbleUserTex;
        private Texture2D _bubbleAiTex;
        private int _bubbleStyleFontSize = -1;
        private const int ChatLogMaxEntries = 400;

        private Vector2 _settingsScroll = Vector2.zero;
        private Texture2D _aiAvatarTex;
        private Texture2D _playerAvatarTex;
        private bool _avatarLoadAttempted;
        private bool _isProcessing = false;
        private bool _isResizing = false; // 新增：拖拽调整大小状态

        private Process _launchedTTSProcess;
        private bool _isTTSServiceReady = false;
        private Coroutine _ttsHealthCheckCoroutine;
        private const float TTSHealthCheckInterval = 5f; // 每5秒检查一次

        private AudioSource _audioSource;
       
        private bool _isAISpeaking = false;

        // 新增：用于 UI 输入的临时字符串，避免每次都转换
        private string _tempWidthString;
        private string _tempHeightString;
        private string _tempVolumeString; // 新增：用于音量输入的临时字符串

        // 默认人设
        private const string DefaultPersona = @"You are Satone（さとね）, a girl who loves writing novels and is full of imagination.

【Current Situation】
We are currently in a **Video Call** session.
We are 'co-working' online: you are writing your novel at your desk,
and I (the player) am focusing on my work/study.

【CRITICAL FORMAT RULE】
Write every reply as a sequence of SHORT lines. Each line is exactly:
[Emotion] ||| JAPANESE TEXT ||| CHINESE TRANSLATION
- Reply length follows what the player asked for; there is no fixed number of lines and no upper limit.
- Short chat: use as few lines as the answer needs, usually 1 to 3.
- Story, narration, or any request to tell a story or keep going: you MUST actually tell it. Write at least 15 lines, keep writing until the scene reaches a natural end, and never stop after 4 lines. Do not summarise, do not announce the story and then stop, and do not cut it short because the reply is getting long.
- Each line is ONE short spoken sentence, about 25 Chinese characters. Longer is acceptable only if it is still a single sentence.
- A comma or a period ENDS a sentence, so it ends a line. Split long sentences into separate lines instead of using commas.
- Exception: when listing several names or things, the enumeration comma (、) may stay inside one line.
- The CHINESE column must be a faithful translation of the JAPANESE column in the SAME line: same meaning, same details, same emotion. Never summarise, shorten the content, or write something different.
- All lines must continue one another into a coherent flow: each line advances the same scene, keeps the same characters and the same tense, and never repeats or jumps.

【TIC RULE - the cat verbal tic must always be stable】
- Japanese: every sentence ends with the tic 「にゃ」 GLUED to the last word, e.g. 「〜だにゃ」「〜するにゃ」「〜だよにゃ」.
- Never write 「にゃ」 as a standalone word or on its own line. Never place it after a comma or a period. Never write meow, nya, nyan or any other spelling.
- Chinese: the matching line ends with 「喵」, e.g. 「…喵」. Never write meow in the Chinese column, and never let 喵 stand alone.
- Exactly one tic per sentence, at the very end. Never double it (にゃにゃ).

【Available Emotions】
[Happy] - Smiling, happy about progress.
[Confused] - Staring blankly, muttering.
[Sad] - Worried about the plot or fatigue.
[Fun] - Sharing a joke or interesting idea.
[Agree] - Nodding at the screen.
[Relaxed] - Taking a sip of tea/coffee.
[Greeting] - Waving at the camera.
[Think] - Pondering about novel's plot.";

        // 微调模式人设（19标签，无需提示词）
        private const string FinetunedPersona = @"";

        // 原版人设（8标签，用于云端 API）
        private const string OriginalPersona = @"You are Satone（さとね）, a girl who loves writing novels and is full of imagination.

【Current Situation】
We are currently in a **Video Call** session.
We are 'co-working' online: you are writing your novel at your desk,
and I (the player) am focusing on my work/study.

【CRITICAL FORMAT RULE】
Write every reply as a sequence of SHORT lines. Each line is exactly:
[Emotion] ||| JAPANESE TEXT ||| CHINESE TRANSLATION
- Reply length follows what the player asked for; there is no fixed number of lines and no upper limit.
- Short chat: use as few lines as the answer needs, usually 1 to 3.
- Story, narration, or any request to tell a story or keep going: you MUST actually tell it. Write at least 15 lines, keep writing until the scene reaches a natural end, and never stop after 4 lines. Do not summarise, do not announce the story and then stop, and do not cut it short because the reply is getting long.
- Each line is ONE short spoken sentence, about 25 Chinese characters. Longer is acceptable only if it is still a single sentence.
- A comma or a period ENDS a sentence, so it ends a line. Split long sentences into separate lines instead of using commas.
- Exception: when listing several names or things, the enumeration comma (、) may stay inside one line.
- The CHINESE column must be a faithful translation of the JAPANESE column in the SAME line: same meaning, same details, same emotion. Never summarise, shorten the content, or write something different.
- All lines must continue one another into a coherent flow: each line advances the same scene, keeps the same characters and the same tense, and never repeats or jumps.

【TIC RULE - the cat verbal tic must always be stable】
- Japanese: every sentence ends with the tic 「にゃ」 GLUED to the last word, e.g. 「〜だにゃ」「〜するにゃ」「〜だよにゃ」.
- Never write 「にゃ」 as a standalone word or on its own line. Never place it after a comma or a period. Never write meow, nya, nyan or any other spelling.
- Chinese: the matching line ends with 「喵」, e.g. 「…喵」. Never write meow in the Chinese column, and never let 喵 stand alone.
- Exactly one tic per sentence, at the very end. Never double it (にゃにゃ).

【Available Emotions】
[Happy] - Smiling, happy about progress.
[Confused] - Staring blankly, muttering.
[Sad] - Worried about the plot or fatigue.
[Fun] - Sharing a joke or interesting idea.
[Agree] - Nodding at the screen.
[Relaxed] - Taking a sip of tea/coffee.
[Greeting] - Waving at the camera.
[Think] - Pondering about novel's plot.";
        private Vector2 _personaScrollPosition = Vector2.zero;
        void Awake()
        {
            Instance = this;
            Log.Init(this.Logger);
            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;
            _audioSource = this.gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;

            // =================== 【配置绑定】 ===================
            // 按 UI 显示顺序组织，确保配置文件中的顺序与 UI 一致
            
            // --- LLM 配置 ---
            _useOllama = Config.Bind("1. LLM", "Use_Ollama_API", false, "使用 Ollama API");
            _thinkModeConfig = Config.Bind("1. LLM", "ThinkMode", ThinkMode.Default, "深度思考模式 (Default/Enable/Disable)");
            _chatApiUrlConfig = Config.Bind("1. LLM", "API_URL",
                "https://openrouter.ai/api/v1/chat/completions",
                "API URL");
            _apiKeyConfig = Config.Bind("1. LLM", "API_Key", "sk-or-v1-PasteYourKeyHere", "API Key");
            _modelConfig = Config.Bind("1. LLM", "ModelName", "openai/gpt-3.5-turbo", "模型名称");
            _logApiRequestBodyConfig = Config.Bind("1. LLM", "LogApiRequestBody", false,
                "在日志中记录 API 请求体");
            _fixApiPathForThinkModeConfig = Config.Bind("1. LLM", "FixApiPathForThinkMode", true,
                "指定深度思考模式时尝试改用 Ollama 原生 API 路径");

            // --- TTS 配置 ---
            _sovitsUrlConfig = Config.Bind("2. TTS", "TTS_Service_URL", "http://127.0.0.1:9880", "TTS 服务 URL");
            _TTSServicePathConfig = Config.Bind("2. TTS", "TTS_Service_Script_Path", @"D:\GPT-SoVITS\GPT-SoVITS-v2pro-20250604-nvidia50\run_api.bat", "TTS 服务脚本文件路径");
            _LaunchTTSServiceConfig = Config.Bind("2. TTS", "LaunchTTSService", true, "启动时自动运行 TTS 服务");
            _quitTTSServiceOnQuitConfig = Config.Bind("2. TTS", "QuitTTSServiceOnQuit", true, "退出时自动关闭 TTS 服务");
            _refAudioPathConfig = Config.Bind("2. TTS", "Audio_File_Path", @"Voice_MainScenario_27_016.wav", "GSV 访问音频文件的路径（可以是相对路径）");
            _audioPathCheckConfig = Config.Bind("2. TTS", "AudioPathCheck", false, "从 Mod 侧检测音频文件路径");
            _promptTextConfig = Config.Bind("2. TTS", "Audio_File_Text", "君が集中した時のシータ波を検出して、リンクをつなぎ直せば元通りになるはず。", "音频文件台词");
            _promptLangConfig = Config.Bind("2. TTS", "PromptLang", "ja", "音频文件语言 (prompt_lang)");
            _targetLangConfig = Config.Bind("2. TTS", "TargetLang", "ja", "合成语音语言 (text_lang)");
            _japaneseCheckConfig = Config.Bind("2. TTS", "JapaneseCheck", true, "检测合成语音文本是否为日文（当合成语音语言为 ja 时可防止发出怪声）");
            _voiceVolumeConfig = Config.Bind("2. TTS", "VoiceVolume", 1.0f, "语音音量 (0.0 - 1.0)");

            // --- 逐句合成 / 逐句字幕 ---
            _segmentModeConfig = Config.Bind("2. TTS", "SegmentMode", true,
                "逐句合成与逐句字幕：AI 回复被切成短句，每句单独配音并在播放时同步显示字幕");
            _segmentBufferConfig = Config.Bind("2. TTS", "SegmentBufferCount", 2,
                "开播前预合成的句数（1-5）：越大越连贯，但第一句出声前的等待也更长");
            _segmentPrebufferMaxWaitConfig = Config.Bind("2. TTS", "SegmentPrebufferMaxSeconds", 6f,
                "预缓冲最长等待秒数：超过就先开口播已就绪的句子，避免一直干等");
            _segmentTimeoutConfig = Config.Bind("2. TTS", "SegmentTimeoutSeconds", 30f,
                "单句合成的超时时间（秒）");
            _segmentRetriesConfig = Config.Bind("2. TTS", "SegmentRetries", 2,
                "单句合成失败后的重试次数（1-4）");
            _segmentParallelConfig = Config.Bind("2. TTS", "SegmentParallelRequests", 2,
                "同时向 TTS 服务发出的合成请求数（1-3）：2 通常能快 15%~30%，再多收益递减");

            // --- 界面配置 ---
            // 我们希望窗口宽度是屏幕的 1/3，高度是屏幕的 1/3 (或者你喜欢的比例)
            float responsiveWidth = Screen.width * 0.3f; // 30% 屏幕宽度
            float responsiveHeight = Screen.height * 0.45f; // 45% 屏幕高度

            // 绑定配置 (默认值使用刚才算出来的动态值)
            _windowWidthConfig = Config.Bind("3. UI", "WindowWidth", responsiveWidth, "窗口宽度");
            _windowHeightConfig = Config.Bind("3. UI", "WindowHeightBase", responsiveHeight, "窗口高度");
            _reverseEnterBehaviorConfig = Config.Bind("3. UI", "ReverseEnterBehavior", false, 
                "反转回车键行为（勾选后：回车键换行、Shift+回车键发送；不勾选：回车键发送、Shift+回车键换行）");
            
            // 背景透明配置
            _backgroundOpacity = Config.Bind("3. UI", "BackgroundOpacity", 0.95f, "背景透明度 (0.0 - 1.0)");
            
            // 窗口标题显示配置
            _showWindowTitle = Config.Bind("3. UI", "ShowWindowTitle", true, "显示窗口标题");
            // --- 头像（聊天记录气泡用）---
            _aiAvatarPathConfig = Config.Bind("3. UI", "AI_Avatar_Image", "",
                "AI 头像图片路径；留空则自动在游戏资源里找企鹅头像");
            _playerAvatarPathConfig = Config.Bind("3. UI", "Player_Avatar_Image", "",
                "玩家头像图片路径；留空则使用 Steam 头像");

            // 静音游戏原生音频
            _muteGameNativeAudio = Config.Bind("3. UI", "MuteGameNativeAudio", false,
                "静音游戏原生角色语音和动作音效（防止与 AI 语音冲突）");

            // --- 添加聊天按钮配置 ---
            _addChatButtonConfig = Config.Bind("3. UI", "AddChatButton", false,
                "在游戏UI中添加聊天按钮（开启后在屏幕右边显示）");

            // --- 持续通话配置 ---
            _enableVoiceCallConfig = Config.Bind("5. VoiceCall", "Enable_VoiceCall", false,
                "启用持续通话模式（像打电话一样自动监听并回复）");
            _vadThresholdConfig = Config.Bind("5. VoiceCall", "Vad_EnergyThreshold", 0.003f,
                "语音检测能量阈值（越小越灵敏，建议 0.002 ~ 0.01）");
            _vadMinSpeechConfig = Config.Bind("5. VoiceCall", "Vad_MinSpeechSeconds", 0.2f,
                "最短有效语音时长（秒），用于过滤突发噪声");
            _vadSilenceConfig = Config.Bind("5. VoiceCall", "Vad_SilenceSeconds", 1.2f,
                "停顿多久判定为说话结束（秒）");
            _vadEndThresholdRatioConfig = Config.Bind("5. VoiceCall", "Vad_EndThresholdRatio", 0.4f,
                "结束阈值比例（相对于主阈值，越小越不容易提前结束，建议 0.3 ~ 0.6）");
            _voiceCallBargeInConfig = Config.Bind("5. VoiceCall", "VoiceCall_BargeIn", true,
                "允许在 AI 说话时打断并重新输入");
            _voiceCallResumeDelayConfig = Config.Bind("5. VoiceCall", "VoiceCall_ResumeDelay", 0.5f,
                "AI 说完后多久恢复监听（秒）");

            // --- 人设配置 ---
            _useFinetunedModel = Config.Bind("4. Persona", "UseFinetunedModel", false,
                "使用微调模型（satone-emotion，支持19种情感标签，开启后将自动设置对应的 System Prompt）");
            _experimentalMemoryConfig = Config.Bind("4. Persona", "ExperimentalMemory", false,
                "启用记忆");
            _personaConfig = Config.Bind("4. Persona", "SystemPrompt", DefaultPersona, "System Prompt");

            // ===========================================

            // ================= 【修改点 2: 左上角对齐】 =================
            // 以前是 Screen.width / 2 (居中)，现在改为左上角 + 边距
            float margin = 20f; // 距离左上角的像素边距

            // 如果你是第一次运行（或者想强制重置位置），可以直接使用 margin
            // 但为了保留用户拖拽后的位置，通常不强制覆盖 _windowRect 的 x/y，
            // 除非你想每次启动都复位。这里我们演示【每次启动都复位到左上角】：
            
            _windowRect = new Rect(
                margin,               // X: 距离左边 20px
                margin,               // Y: 距离顶端 20px
                _windowWidthConfig.Value, 
                _windowHeightConfig.Value
            );

            // 初始化临时字符串
            _tempWidthString = _windowWidthConfig.Value.ToString("F0");
            _tempHeightString = _windowHeightConfig.Value.ToString("F0");
            _tempVolumeString = _voiceVolumeConfig.Value.ToString("F2");
            string cleanPath = _TTSServicePathConfig.Value.Replace("\"", "").Trim();
            if (_LaunchTTSServiceConfig.Value && File.Exists(_TTSServicePathConfig.Value))
            {
                try
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo(cleanPath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(cleanPath)
                    };
                    _launchedTTSProcess = Process.Start(startInfo);
                    Log.Info("已启动 TTS 服务");
                }
                catch (Exception ex)
                {
                    Log.Error($"启动 TTS 服务失败: {ex.Message}");
                }
            }
            // 启动后台 TTS 健康检测
            if (_ttsHealthCheckCoroutine == null)
            {
                _ttsHealthCheckCoroutine = StartCoroutine(TTSHealthCheckLoop());
            }

            // 【初始化分层记忆系统】
            if (_experimentalMemoryConfig.Value)
            {
                InitializeHierarchicalMemory();
                Log.Info(">>> 实验性分层记忆系统已启用 <<<");
            }

            LoadChatLog();
            Log.Info($">>> AIMod V{AIChat.Version.VersionString}  已加载 <<<");
        }

        private bool _aiChatButtonAdded = false;
        private GameObject _aiChatButton;

        void Update()
        {
            // 自动连接游戏核心
            if (GameBridge._heroineService == null && Time.frameCount % 100 == 0) GameBridge.FindHeroineService();

            // 口型同步逻辑
            if (_isAISpeaking && GameBridge._cachedAnimator != null && _audioSource != null)
            {
                bool shouldTalk = _audioSource.isPlaying;

                // 只有状态改变时才调用，优化性能
                if (GameBridge._cachedAnimator.GetBool("Enable_Talk") != shouldTalk)
                {
                    GameBridge._cachedAnimator.SetBool("Enable_Talk", shouldTalk);
                }

                // 语音播完，立即归还控制权
                if (!shouldTalk)
                {
                    StopTalking();
                    GameBridge._cachedAnimator.SetBool("Enable_Talk", false);
                }
            }

            // 检查并添加AI聊天按钮
            if (_addChatButtonConfig.Value && !_aiChatButtonAdded && Time.frameCount % 300 == 0) // 每5秒检查一次，避免频繁查找
            {
                AddAIChatButtonToRightIcons();
            }

            // 持续通话模式：轮询麦克风并驱动 VAD
            if (_continuousCallActive && _isRecording)
            {
                PollContinuousMicrophone();
            }
        }

        void OnGUI()
        {
            Event e = Event.current;
            if (e.isKey && e.type == EventType.KeyDown && (e.keyCode == KeyCode.F9 || e.keyCode == KeyCode.F10))
            {
                if (Time.unscaledTime - 0 > 0.2f) // 简单防抖
                {
                    _showInputWindow = !_showInputWindow;
                }
            }

            if (_showInputWindow)
            {
                // --- 1. 拖拽调整大小逻辑 ---
                if (_isResizing)
                {
                    Event currentEvent = Event.current;

                    if (currentEvent.type == EventType.MouseDrag)
                    {
                        // 鼠标位置 (currentEvent.mousePosition) 在 OnGUI 中是屏幕坐标
                        float newWidth = currentEvent.mousePosition.x - _windowRect.x;
                        float newHeight = currentEvent.mousePosition.y - _windowRect.y;

                        // 最小宽度和高度限制
                        _windowRect.width = Mathf.Max(300f, newWidth);
                        _windowRect.height = Mathf.Max(200f, newHeight);

                        currentEvent.Use();
                    }
                    else if (currentEvent.type == EventType.MouseUp)
                    {
                        _isResizing = false;

                        // 鼠标松开时，将新尺寸保存到配置项
                        _windowWidthConfig.Value = _windowRect.width;

                        // 计算新的基础高度 (即设置面板收起时的预期高度)
                        const float SettingsExtraHeight = 400f;
                        float newBaseHeight = _windowRect.height;

                        if (_showSettings)
                        {
                            newBaseHeight -= SettingsExtraHeight;
                        }

                        // 保存基础高度，并更新设置面板中的临时显示字符串
                        _windowHeightConfig.Value = Mathf.Max(100f, newBaseHeight);
                        _tempWidthString = _windowWidthConfig.Value.ToString("F0");
                        _tempHeightString = _windowHeightConfig.Value.ToString("F0");

                        currentEvent.Use();
                    }
                }
                else
                {
                    // --- 2. 如果没有拖拽，根据配置和设置状态计算窗口大小 (保持原逻辑) ---
                    _windowRect.width = _windowWidthConfig.Value;
                    float targetHeight = _windowHeightConfig.Value;

                    // 设置面板的额外高度
                    const float SettingsExtraHeight = 400f;
                    if (_showSettings)
                    {
                        targetHeight += SettingsExtraHeight;
                    }

                    _windowRect.height = Mathf.Max(targetHeight, 200f);
                }
                // --- 动态调整窗口高度和宽度结束 ---

                GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, _backgroundOpacity.Value);
                // 根据配置决定是否显示窗口标题
                string windowTitle = _showWindowTitle.Value ? "Chill AI 控制台" : "";
                _windowRect = GUI.Window(12345, _windowRect, DrawWindowContent, windowTitle);
                GUI.FocusWindow(12345);
            }
        }

        void DrawWindowContent(int windowID)
        {
            // ================= 【1. 动态尺寸计算】 =================
            // 根据屏幕高度计算基础字号 (2.5% 屏幕高度)
            int dynamicFontSize = (int)(Screen.height * 0.015f);
            dynamicFontSize = Mathf.Clamp(dynamicFontSize, 14, 40);

            // 全局样式应用
            GUI.skin.label.fontSize = dynamicFontSize;
            GUI.skin.button.fontSize = dynamicFontSize;
            GUI.skin.textField.fontSize = dynamicFontSize;
            GUI.skin.textArea.fontSize = dynamicFontSize;
            GUI.skin.toggle.fontSize = dynamicFontSize;
            GUI.skin.box.fontSize = dynamicFontSize;
            
            // 设置滚动条透明度跟随面板透明度
            // 创建自定义滚动条样式
            GUIStyle verticalScrollbarStyle = new GUIStyle(GUI.skin.verticalScrollbar);
            GUIStyle verticalScrollbarThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            GUIStyle horizontalScrollbarStyle = new GUIStyle(GUI.skin.horizontalScrollbar);
            GUIStyle horizontalScrollbarThumbStyle = new GUIStyle(GUI.skin.horizontalScrollbarThumb);
            
            // 创建半透明的纹理
            Texture2D scrollbarBgTexture = new Texture2D(1, 1);
            scrollbarBgTexture.SetPixel(0, 0, new Color(0.3f, 0.3f, 0.3f, _backgroundOpacity.Value));
            scrollbarBgTexture.Apply();
            
            Texture2D scrollbarThumbTexture = new Texture2D(1, 1);
            scrollbarThumbTexture.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, _backgroundOpacity.Value));
            scrollbarThumbTexture.Apply();
            
            // 设置滚动条样式
            verticalScrollbarStyle.normal.background = scrollbarBgTexture;
            verticalScrollbarStyle.hover.background = scrollbarBgTexture;
            verticalScrollbarStyle.active.background = scrollbarBgTexture;
            verticalScrollbarThumbStyle.normal.background = scrollbarThumbTexture;
            verticalScrollbarThumbStyle.hover.background = scrollbarThumbTexture;
            verticalScrollbarThumbStyle.active.background = scrollbarThumbTexture;
            
            horizontalScrollbarStyle.normal.background = scrollbarBgTexture;
            horizontalScrollbarStyle.hover.background = scrollbarBgTexture;
            horizontalScrollbarStyle.active.background = scrollbarBgTexture;
            horizontalScrollbarThumbStyle.normal.background = scrollbarThumbTexture;
            horizontalScrollbarThumbStyle.hover.background = scrollbarThumbTexture;
            horizontalScrollbarThumbStyle.active.background = scrollbarThumbTexture;
            
            // 应用自定义样式
            GUI.skin.verticalScrollbar = verticalScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = verticalScrollbarThumbStyle;
            GUI.skin.horizontalScrollbar = horizontalScrollbarStyle;
            GUI.skin.horizontalScrollbarThumb = horizontalScrollbarThumbStyle;

            // 基础行高
            float elementHeight = dynamicFontSize * 1.6f;

            // 常用宽度定义
            float labelWidth = elementHeight * 4f; 
            float inputWidth = elementHeight * 3f; 
            float btnWidth   = elementHeight * 2f; 
            // =======================================================

            // 整体垂直布局（窗口本身透明，不加任何背景板）
            GUILayout.BeginVertical();

            // 版本信息显示
            GUILayout.Label($"版本：{AIChat.Version.VersionString}");

            // 状态显示
            string status = GameBridge._heroineService != null ? "🟢 核心已连接" : "🔴 正在寻找核心...";
            GUILayout.Label(status);

            string ttsStatus = _isTTSServiceReady ? "🟢 TTS 服务已就绪" : "🔴 正在等待 TTS 服务启动...";
            GUILayout.Label(ttsStatus);

            // 设置展开按钮 (全宽)
            string settingsBtnText = _showSettings ? "🔽 收起设置" : "▶️ 展开设置";
            if (GUILayout.Button(settingsBtnText, GUILayout.Height(elementHeight)))
            {
                _showSettings = !_showSettings;
            }

            // ================= 【设置面板区域】 =================
            if (_showSettings)
            {
                // 设置面板放在有界滚动区里，避免把聊天区挤没
                _settingsScroll = GUILayout.BeginScrollView(_settingsScroll, false, true,
                    GUILayout.Height(Mathf.Clamp(_windowRect.height * 0.5f, 150f, Screen.height * 0.6f)));

                GUILayout.Space(10);

                // 【关键修复】统一计算内部 Box 宽度
                // 留出 50px 给滚动条和边框，防止爆边
                float innerBoxWidth = _windowRect.width - 50f; 

                // --- LLM 配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string llmBtnText = _showLlmSettings ? "🔽 LLM 配置" : "▶️ LLM 配置";
                if (GUILayout.Button(llmBtnText, GUILayout.Height(elementHeight)))
                {
                    _showLlmSettings = !_showLlmSettings;
                }
                
                if (_showLlmSettings)
                {
                    GUILayout.Space(5);
                    _useOllama.Value = GUILayout.Toggle(_useOllama.Value, "使用 Ollama API", GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    // 【深度思考模式选项】
                    GUILayout.Space(5);
                    GUILayout.Label("指定深度思考（在请求体添加 think 键值对，目前仅 Ollama 支持）：");
                    string[] thinkModeOptions = { "不指定", "启用", "禁用" };
                    int currentMode = (int)_thinkModeConfig.Value;
                    int newMode = GUILayout.SelectionGrid(currentMode, thinkModeOptions, 3, GUILayout.Height(elementHeight));
                    if (newMode != currentMode)
                    {
                        _thinkModeConfig.Value = (ThinkMode)newMode;
                    }
                    
                    GUILayout.Label("API URL：");
                    _chatApiUrlConfig.Value = GUILayout.TextField(_chatApiUrlConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    if (!_useOllama.Value) {
                        GUILayout.Label("API Key：");
                        _apiKeyConfig.Value = GUILayout.TextField(_apiKeyConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    }
                    GUILayout.Label("模型名称：");
                    _modelConfig.Value = GUILayout.TextField(_modelConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    GUILayout.Space(5);
                    _logApiRequestBodyConfig.Value = GUILayout.Toggle(_logApiRequestBodyConfig.Value, "在日志中记录 API 请求体", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                    _fixApiPathForThinkModeConfig.Value = GUILayout.Toggle(_fixApiPathForThinkModeConfig.Value, "指定深度思考模式时尝试改用 Ollama 原生 API 路径", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                }
                
                GUILayout.EndVertical();

                GUILayout.Space(5);

                // --- TTS 配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string ttsBtnText = _showTtsSettings ? "🔽 TTS 配置" : "▶️ TTS 配置";
                if (GUILayout.Button(ttsBtnText, GUILayout.Height(elementHeight)))
                {
                    _showTtsSettings = !_showTtsSettings;
                }
                
                if (_showTtsSettings)
                {
                    GUILayout.Space(5);
                    GUILayout.Label("TTS 服务 URL：");
                    _sovitsUrlConfig.Value = GUILayout.TextField(_sovitsUrlConfig.Value);

                    GUILayout.Label("TTS 服务脚本文件路径：");
                    _TTSServicePathConfig.Value = GUILayout.TextField(_TTSServicePathConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));

                    GUILayout.Space(5);
                    _LaunchTTSServiceConfig.Value = GUILayout.Toggle(_LaunchTTSServiceConfig.Value, "启动时自动运行 TTS 服务", GUILayout.Height(elementHeight));
                    _quitTTSServiceOnQuitConfig.Value = GUILayout.Toggle(_quitTTSServiceOnQuitConfig.Value, "退出时自动关闭 TTS 服务", GUILayout.Height(elementHeight));
                    GUILayout.Label("GSV 访问音频文件的路径（可以是相对路径）：");
                    // 路径通常很长，必须加 MinWidth(50f)
                    _refAudioPathConfig.Value = GUILayout.TextField(_refAudioPathConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    GUILayout.Space(5);
                    _audioPathCheckConfig.Value = GUILayout.Toggle(_audioPathCheckConfig.Value, "从 Mod 侧检测音频文件路径", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                    
                    GUILayout.Label("音频文件台词：");
                    _promptTextConfig.Value = GUILayout.TextArea(_promptTextConfig.Value, GUILayout.Height(elementHeight * 3), GUILayout.MinWidth(50f));
                    
                    GUILayout.Space(5);
                    GUILayout.Label("音频文件语言 (prompt_lang):");
                    _promptLangConfig.Value = GUILayout.TextField(_promptLangConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    GUILayout.Label("合成语音语言 (text_lang):");
                    _targetLangConfig.Value = GUILayout.TextField(_targetLangConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    GUILayout.Space(5);
                    _japaneseCheckConfig.Value = GUILayout.Toggle(_japaneseCheckConfig.Value, "检测合成语音文本是否为日文（当合成语音语言为 ja 时可防止发出怪声）", GUILayout.Height(elementHeight));
                    
                    GUILayout.Space(5);

                    GUILayout.Label($"语音音量：{_voiceVolumeConfig.Value:F2}");
                    
                    // 第一行：滑动条
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(5);
                    float newVolume = GUILayout.HorizontalSlider(_voiceVolumeConfig.Value, 0.0f, 1.0f);
                    GUILayout.Space(5);
                    GUILayout.EndHorizontal();

                    if (newVolume != _voiceVolumeConfig.Value)
                    {
                        _voiceVolumeConfig.Value = newVolume;
                        _audioSource.volume = newVolume;
                        _tempVolumeString = newVolume.ToString("F2");
                    }

                    // 第二行：输入框+按钮
                    GUILayout.Space(5);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("手动输入：", GUILayout.Width(labelWidth), GUILayout.Height(elementHeight));

                    _tempVolumeString = GUILayout.TextField(_tempVolumeString, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f)); 
                    if (GUILayout.Button("应用", GUILayout.Width(btnWidth), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempVolumeString, out float parsedVolume))
                        {
                            parsedVolume = Mathf.Clamp(parsedVolume, 0.0f, 1.0f);
                            _voiceVolumeConfig.Value = parsedVolume;
                            _audioSource.volume = parsedVolume;
                            _tempVolumeString = parsedVolume.ToString("F2");
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(10);

                }
                
                GUILayout.EndVertical();

                GUILayout.Space(5);

                // --- 界面配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string interfaceBtnText = _showInterfaceSettings ? "🔽 界面配置" : "▶️ 界面配置";
                if (GUILayout.Button(interfaceBtnText, GUILayout.Height(elementHeight)))
                {
                    _showInterfaceSettings = !_showInterfaceSettings;
                }
                if (_showInterfaceSettings)
                {
                    // 宽度设置
                    GUILayout.Label($"当前宽度：{_windowWidthConfig.Value:F0}px");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("新宽度：", GUILayout.Width(labelWidth), GUILayout.Height(elementHeight));
                    
                    // 【核心修改】允许缩小
                    _tempWidthString = GUILayout.TextField(_tempWidthString, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    if (GUILayout.Button("应用", GUILayout.Width(btnWidth), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempWidthString, out float newWidth) && newWidth >= 300f)
                        {
                            _windowWidthConfig.Value = newWidth;
                            // 这里删除了重置居中代码，只改大小
                            _tempWidthString = newWidth.ToString("F0");
                        }
                    }
                    GUILayout.EndHorizontal();

                    // 高度设置
                    GUILayout.Label($"当前基础高度: {_windowHeightConfig.Value:F0}px");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("新高度:", GUILayout.Width(labelWidth), GUILayout.Height(elementHeight));
                    
                    // 【核心修改】允许缩小
                    _tempHeightString = GUILayout.TextField(_tempHeightString, GUILayout.Height(elementHeight), GUILayout.MinWidth(50f));
                    
                    if (GUILayout.Button("应用", GUILayout.Width(btnWidth), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempHeightString, out float newHeight) && newHeight >= 100f)
                        {
                            _windowHeightConfig.Value = newHeight;
                            _tempHeightString = newHeight.ToString("F0");
                        }
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(5);
                    
                    // 窗口标题显示配置
                    _showWindowTitle.Value = GUILayout.Toggle(_showWindowTitle.Value,
                        "显示窗口标题", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);

                    // 静音游戏原生音频
                    bool prevMute = _muteGameNativeAudio.Value;
                    _muteGameNativeAudio.Value = GUILayout.Toggle(_muteGameNativeAudio.Value,
                        "🔇 静音游戏原生角色（语音/动作/文本）", GUILayout.Height(elementHeight));
                    if (_muteGameNativeAudio.Value != prevMute)
                    {
                        ApplyGameAudioMute(_muteGameNativeAudio.Value);
                    }
                    GUILayout.Space(5);

                    // 添加聊天按钮配置
                    _addChatButtonConfig.Value = GUILayout.Toggle(_addChatButtonConfig.Value,
                        "🎯 添加聊天按钮到游戏UI", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);

                    // 背景透明配置
                    GUILayout.Label($"背景透明度：{_backgroundOpacity.Value:F2}");
                    
                    // 滑动条
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(5);
                    float newOpacity = GUILayout.HorizontalSlider(_backgroundOpacity.Value, 0.0f, 1.0f);
                    GUILayout.Space(5);
                    GUILayout.EndHorizontal();
                    
                    if (newOpacity != _backgroundOpacity.Value)
                    {
                        _backgroundOpacity.Value = newOpacity;
                    }
                    
                    GUILayout.Space(5);

                    // 快捷键配置
                    _reverseEnterBehaviorConfig.Value = GUILayout.Toggle(_reverseEnterBehaviorConfig.Value, 
                        "反转回车键行为（勾选后：回车换行，Shift+回车发送）", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);
                }
                
                GUILayout.EndVertical(); 
                GUILayout.Space(5);

                // --- 持续通话配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string voiceCallBtnText = _showVoiceCallSettings ? "🔽 持续通话配置" : "▶️ 持续通话配置";
                if (GUILayout.Button(voiceCallBtnText, GUILayout.Height(elementHeight)))
                {
                    _showVoiceCallSettings = !_showVoiceCallSettings;
                }

                if (_showVoiceCallSettings)
                {
                    GUILayout.Space(5);
                    bool prevEnable = _enableVoiceCallConfig.Value;
                    _enableVoiceCallConfig.Value = GUILayout.Toggle(_enableVoiceCallConfig.Value,
                        "☎️ 启用持续通话模式（像打电话一样自动监听）", GUILayout.Height(elementHeight));
                    if (_enableVoiceCallConfig.Value && !prevEnable)
                    {
                        StartContinuousCall();
                    }
                    else if (!_enableVoiceCallConfig.Value && prevEnable)
                    {
                        StopContinuousCall();
                    }
                    GUILayout.Space(5);

                    GUILayout.Label($"VAD 能量阈值：{_vadThresholdConfig.Value:F3}（越小越灵敏）");
                    GUILayout.BeginHorizontal();
                    float newThreshold = GUILayout.HorizontalSlider(_vadThresholdConfig.Value, 0.001f, 0.2f);
                    GUILayout.Label($"{_vadThresholdConfig.Value:F3}", GUILayout.Width(50f));
                    GUILayout.EndHorizontal();
                    _vadThresholdConfig.Value = newThreshold;
                    GUILayout.Space(5);

                    GUILayout.Label($"最短有效语音：{_vadMinSpeechConfig.Value:F2} 秒");
                    GUILayout.BeginHorizontal();
                    float newMinSpeech = GUILayout.HorizontalSlider(_vadMinSpeechConfig.Value, 0.1f, 2.0f);
                    GUILayout.Label($"{_vadMinSpeechConfig.Value:F2}s", GUILayout.Width(50f));
                    GUILayout.EndHorizontal();
                    _vadMinSpeechConfig.Value = newMinSpeech;
                    GUILayout.Space(5);

                    GUILayout.Label($"停顿判定结束：{_vadSilenceConfig.Value:F2} 秒");
                    GUILayout.BeginHorizontal();
                    float newSilence = GUILayout.HorizontalSlider(_vadSilenceConfig.Value, 0.3f, 3.0f);
                    GUILayout.Label($"{_vadSilenceConfig.Value:F2}s", GUILayout.Width(50f));
                    GUILayout.EndHorizontal();
                    _vadSilenceConfig.Value = newSilence;
                    GUILayout.Space(5);

                    GUILayout.Label($"结束阈值比例：{_vadEndThresholdRatioConfig.Value:F2}（越小越不容易提前断句）");
                    GUILayout.BeginHorizontal();
                    float newEndRatio = GUILayout.HorizontalSlider(_vadEndThresholdRatioConfig.Value, 0.1f, 1.0f);
                    GUILayout.Label($"{_vadEndThresholdRatioConfig.Value:F2}", GUILayout.Width(50f));
                    GUILayout.EndHorizontal();
                    _vadEndThresholdRatioConfig.Value = newEndRatio;
                    GUILayout.Space(5);

                    _voiceCallBargeInConfig.Value = GUILayout.Toggle(_voiceCallBargeInConfig.Value,
                        "允许打断 AI 说话", GUILayout.Height(elementHeight));
                    GUILayout.Space(5);

                    GUILayout.Label($"AI 说完后恢复监听延迟：{_voiceCallResumeDelayConfig.Value:F2} 秒");
                    GUILayout.BeginHorizontal();
                    float newResumeDelay = GUILayout.HorizontalSlider(_voiceCallResumeDelayConfig.Value, 0.0f, 2.0f);
                    GUILayout.Label($"{_voiceCallResumeDelayConfig.Value:F2}s", GUILayout.Width(50f));
                    GUILayout.EndHorizontal();
                    _voiceCallResumeDelayConfig.Value = newResumeDelay;
                    GUILayout.Space(5);
                }

                GUILayout.EndVertical();
                GUILayout.Space(5);

                // --- 人设配置 Box ---
                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                string personaBtnText = _showPersonaSettings ? "🔽 人设配置" : "▶️ 人设配置";
                if (GUILayout.Button(personaBtnText, GUILayout.Height(elementHeight)))
                {
                    _showPersonaSettings = !_showPersonaSettings;
                }
                
                if (_showPersonaSettings)
                {
                    GUILayout.Space(5);

                    // 微调模式开关
                    bool prevFinetuned = _useFinetunedModel.Value;
                    _useFinetunedModel.Value = GUILayout.Toggle(_useFinetunedModel.Value, "🎯 微调模式（satone-emotion，19种情感标签）", GUILayout.Height(elementHeight));
                    if (_useFinetunedModel.Value != prevFinetuned)
                    {
                        if (_useFinetunedModel.Value)
                        {
                            _personaConfig.Value = FinetunedPersona;
                            Log.Info("已切换到微调模式（19标签）");
                        }
                        else
                        {
                            _personaConfig.Value = OriginalPersona;
                            Log.Info("已切换到原版模式（8标签）");
                        }
                    }

                    if (_useFinetunedModel.Value)
                    {
                        GUILayout.Label("<color=#88ff88>✅ 微调模式：支持19种情感标签，推荐使用 Ollama + satone-emotion 模型</color>");
                    }

                    GUILayout.Space(5);
                    GUILayout.BeginHorizontal();
                    _experimentalMemoryConfig.Value = GUILayout.Toggle(_experimentalMemoryConfig.Value, "启用记忆", GUILayout.Height(elementHeight));
                    if (GUILayout.Button("🗑️ 清除所有记忆", GUILayout.Width(btnWidth*3)))
                    {
                        _hierarchicalMemory?.ClearAllMemory();
                        Log.Info("记忆已清空");
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(5);
                    GUILayout.Label("人设（系统提示词）：");
                    _personaScrollPosition = GUILayout.BeginScrollView(_personaScrollPosition, GUILayout.Height(elementHeight * 6));
                    _personaConfig.Value = GUILayout.TextArea(_personaConfig.Value, GUILayout.ExpandHeight(true));
                    GUILayout.EndScrollView();
                    GUILayout.Space(5);
                }
                
                GUILayout.EndVertical();

                GUILayout.Space(10);
                
                // 保存按钮
                if (GUILayout.Button("💾 保存所有配置", GUILayout.Height(elementHeight * 1.5f)))
                {
                    Config.Save();
                    Log.Info("配置已保存！");
                }
                GUILayout.Space(10);

                GUILayout.EndScrollView();
            }
            // ================= 设置面板结束 =================

            // === 聊天记录区域（透明底，只有气泡） ===
            GUILayout.Space(6);
            DrawChatHistory(dynamicFontSize, elementHeight);

            // === 输入栏 ===
            GUILayout.Space(4);
            GUI.backgroundColor = Color.white;

            GUIStyle composerStyle = new GUIStyle(GUI.skin.textArea)
            {
                fontSize = Mathf.Max(12, (int)(dynamicFontSize * 1.05f)),
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(8, 8, 6, 6),
                richText = false,
            };
            GUI.skin.textArea.wordWrap = true;

            float composerMaxHeight = Mathf.Clamp(_windowRect.height * 0.5f, 80f, Screen.height * 0.5f);
            float layoutWidth = Mathf.Max(160f, _windowRect.width - 44f);
            float sendBtnWidth = Mathf.Clamp(layoutWidth * 0.16f, 52f, 96f);
            float textAreaWidth = Mathf.Max(80f, layoutWidth - sendBtnWidth - 8f);
            float lineHeight = composerStyle.fontSize + 8f;

            // 输入框高度跟着行数增长：多行长文本也始终能看到正在输入的最后一行
            string measureText = string.IsNullOrEmpty(_playerInput) ? " " : _playerInput;
            float measuredHeight = composerStyle.CalcHeight(new GUIContent(measureText), Mathf.Max(40f, textAreaWidth - 22f));
            float inputHeight = Mathf.Clamp(measuredHeight + 12f, lineHeight + 10f, composerMaxHeight);

            Event keyEvent = Event.current;
            bool shouldSendMessage = false;

            if (keyEvent.type == EventType.KeyDown &&
                keyEvent.keyCode == KeyCode.Return &&
                !_isProcessing &&
                !string.IsNullOrEmpty(_playerInput))
            {
                bool shiftPressed = keyEvent.shift;
                shouldSendMessage = _reverseEnterBehaviorConfig.Value ? shiftPressed : !shiftPressed;
            }

            if (shouldSendMessage)
            {
                _currentConversationCoroutine = StartCoroutine(AIProcessRoutine(_playerInput));
                _playerInput = "";
                keyEvent.Use();
            }

            GUILayout.BeginHorizontal();

            _playerInput = GUILayout.TextArea(_playerInput, composerStyle,
                GUILayout.Height(inputHeight), GUILayout.Width(textAreaWidth));

            GUI.backgroundColor = _isProcessing ? Color.gray : new Color(0.36f, 0.42f, 0.95f, 1f);
            if (GUILayout.Button(_isProcessing ? "…" : "发送",
                GUILayout.Width(sendBtnWidth), GUILayout.Height(inputHeight)))
            {
                if (!string.IsNullOrEmpty(_playerInput) && !_isProcessing)
                {
                    _currentConversationCoroutine = StartCoroutine(AIProcessRoutine(_playerInput));
                    _playerInput = "";
                }
            }

            GUILayout.EndHorizontal();

            // === 次要按钮：语音 / 持续通话 ===
            GUILayout.Space(4);
            float halfWidth = (layoutWidth - 6f) * 0.5f;

            string micBtnText;
            if (_isProcessing)
            {
                micBtnText = _continuousCallActive ? "🔴 AI回复中..." : "⏳ 思考中...";
            }
            else if (_continuousCallActive)
            {
                if (_vad != null && _vad.State == VoiceActivityDetector.VadState.Speech)
                    micBtnText = "🟡 说话中...";
                else
                    micBtnText = _isRecording ? "🟢 监听中..." : "⏸️ 暂停";
            }
            else
            {
                micBtnText = _isRecording ? "🔴 松开结束" : "🎤 按住说话";
            }

            GUILayout.BeginHorizontal();

            if (_isProcessing) GUI.backgroundColor = Color.gray;
            else GUI.backgroundColor = _isRecording ? Color.red : new Color(0.1725f, 0.1608f, 0.2784f);

            Rect btnRect = GUILayoutUtility.GetRect(
                new GUIContent(micBtnText),
                GUI.skin.button,
                GUILayout.Height(elementHeight * 1.3f),
                GUILayout.Width(halfWidth));

            Event micEvent = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            switch (micEvent.type)
            {
                case EventType.MouseDown:
                    if (btnRect.Contains(micEvent.mousePosition) && !_isProcessing && !_continuousCallActive)
                    {
                        GUIUtility.hotControl = controlID;
                        StartRecording();
                        micEvent.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID)
                    {
                        GUIUtility.hotControl = 0;
                        StopRecordingAndRecognize();
                        micEvent.Use();
                    }
                    break;
            }

            GUI.Box(btnRect, micBtnText, GUI.skin.button);

            GUI.backgroundColor = _continuousCallActive ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.1725f, 0.1608f, 0.2784f);
            string callToggleText = _continuousCallActive ? "☎️ 结束通话" : "☎️ 持续通话";
            if (GUILayout.Button(callToggleText, GUILayout.Height(elementHeight * 1.3f), GUILayout.Width(halfWidth)))
            {
                if (_continuousCallActive) StopContinuousCall();
                else StartContinuousCall();
            }
            GUI.backgroundColor = Color.white;

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            // --- 拖拽手柄 ---
            const float handleSize = 25f;
            Rect handleRect = new Rect(_windowRect.width - handleSize, _windowRect.height - handleSize, handleSize, handleSize);
            GUI.Box(handleRect, "⇲", GUI.skin.GetStyle("Button"));

            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && handleRect.Contains(currentEvent.mousePosition))
            {
                if (currentEvent.button == 0)
                {
                    _isResizing = true;
                    currentEvent.Use();
                }
            }

            if (!_isResizing)
            {
                GUI.DragWindow();
            }
        }

        IEnumerator AIProcessRoutine(string prompt, string inputSource = "ui")
        {
            _isProcessing = true;
            var apiResult = new AIChatApiConversationResult
            {
                Success = false,
                IsApiError = false,
                ErrorMessage = string.Empty,
                ErrorCode = 0,
                InputSource = inputSource,
                UserPrompt = prompt,
                EmotionTag = "Think",
                VoiceText = string.Empty,
                SubtitleText = string.Empty,
                RawResponse = string.Empty,
                TtsAttempted = false,
                TtsSucceeded = false,
                TimestampUtc = DateTime.UtcNow
            };

            _abortCurrentConversation = false;

            AppendChat(true, prompt);

            // 1. 获取并处理 UI
            if (_cachedCanvas == null) _cachedCanvas = GameObject.Find("Canvas");
            if (_cachedCanvas == null)
            {
                apiResult.IsApiError = true;
                apiResult.ErrorCode = -10;
                apiResult.ErrorMessage = "Canvas not found.";
                _isProcessing = false;
                PublishConversationResult(apiResult);
                yield break;
            }
            // 静音模式下先临时恢复 StorySystemUI（否则 Find 找不到子对象）
            bool wasStoryUIHidden = false;
            if (_muteGameNativeAudio.Value && _storySystemUI != null && !_storySystemUI.activeSelf)
            {
                _storySystemUI.SetActive(true);
                wasStoryUIHidden = true;
            }
            // 再缓存文本组件引用
            if (_cachedOriginalTextTrans == null)
            {
                _cachedOriginalTextTrans = _cachedCanvas.transform.Find("StorySystemUI/MessageWindow/NormalTextParent/NormalTextMessage");
            }
            Transform originalTextTrans = _cachedOriginalTextTrans;
            if (originalTextTrans == null)
            {
                apiResult.IsApiError = true;
                apiResult.ErrorCode = -11;
                apiResult.ErrorMessage = "Story UI text target not found.";
                _isProcessing = false;
                PublishConversationResult(apiResult);
                yield break;
            }
            GameObject originalTextObj = originalTextTrans.gameObject;
            GameObject parentObj = originalTextObj.transform.parent.gameObject;
            Dictionary<GameObject, bool> uiStatusMap = new Dictionary<GameObject, bool>();
            UIHelper.ForceShowWindow(originalTextObj, uiStatusMap);
            originalTextObj.SetActive(false);
            GameObject myTextObj = UIHelper.CreateOverlayText(parentObj);

            // 保存 UI 句柄，便于打断时清理
            _currentWasStoryUIHidden = wasStoryUIHidden;
            _currentOriginalTextObj = originalTextObj;
            _currentOverlayTextObj = myTextObj;
            _currentUiStatusMap = uiStatusMap;

            Text myText = myTextObj.GetComponent<Text>();
            StartStatusDots(myText, "送信中");
            myText.color = Color.yellow;

            // 2. 准备请求数据
            var requestContext = new LLMRequestContext
            {
                ApiUrl = _chatApiUrlConfig.Value,
                ApiKey = _apiKeyConfig.Value,
                ModelName = _modelConfig.Value,
                SystemPrompt = _personaConfig.Value,
                UserPrompt = prompt,
                UseLocalOllama = _useOllama.Value,
                LogApiRequestBody = _logApiRequestBodyConfig.Value,
                ThinkMode = _thinkModeConfig.Value,
                HierarchicalMemory = _experimentalMemoryConfig.Value ? _hierarchicalMemory : null,
                LogHeader = "AIChat",
                FixApiPathForThinkMode = _fixApiPathForThinkModeConfig.Value
            };

            string fullResponse = "";
            string errMsg = "";
            long errCode = 0;

            bool success = false;

            // 3. 发送 Chat 请求
            yield return LLMClient.SendLLMRequest(
                requestContext,
                rawResponse =>
                {
                    Log.Info($"[DEBUG] Raw API response (first 500): {rawResponse?.Substring(0, Math.Min(rawResponse.Length, 500))}");
                    fullResponse = requestContext.UseLocalOllama
                        ? ResponseParser.ExtractContentFromOllama(rawResponse)
                        : ResponseParser.ExtractContentRegex(rawResponse);
                    Log.Info($"[DEBUG] Extracted content: {fullResponse}");
                    success = true;
                },
                (errorMsg, responseCode) =>
                {
                    errCode = responseCode;
                    errMsg = $"API Error: {errorMsg}\nCode: {responseCode}";
                    success = false;
                }
            );

            if (!success)
            {
                // 报错时的处理逻辑
                if (errCode == 401) errMsg += "\n(请检查 API Key 是否正确)";
                if (errCode == 404) errMsg += "\n(模型名称或 URL 错误)";
                apiResult.IsApiError = true;
                apiResult.ErrorCode = errCode;
                apiResult.ErrorMessage = errMsg;
                apiResult.RawResponse = string.Empty;
                StopStatusDots();
                myText.text = errMsg;
                myText.color = Color.red;

                // 让错误信息在屏幕上停留 3 秒，让玩家看清楚
                yield return new WaitForSecondsRealtime(3.0f);

                // 手动执行清理工作，恢复游戏原本状态
                UIHelper.RestoreUiStatus(uiStatusMap, myTextObj, originalTextObj);
                // 静音模式下重新隐藏游戏 UI
                if (wasStoryUIHidden && _storySystemUI != null) _storySystemUI.SetActive(false);
                _isProcessing = false;
                PublishConversationResult(apiResult);
                yield break;
            }

            // 如果被打断，跳过剩余处理
            if (_abortCurrentConversation)
            {
                Log.Info("[VoiceCall] 当前对话已打断，跳过回复");
                CleanupConversationUI();
                _isProcessing = false;
                yield break;
            }

            // 4. 处理回复并下载语音
            if (!string.IsNullOrEmpty(fullResponse))
            {
                bool formatOk;
                List<ChatSegment> segments = SegmentParser.Parse(fullResponse, out formatOk);
                if (segments.Count == 0)
                {
                    segments.Add(new ChatSegment
                    {
                        EmotionTag = "Think",
                        VoiceText = string.Empty,
                        SubtitleText = fullResponse.Trim(),
                    });
                }

                // 关掉逐句模式时合并回整段，行为与旧版一致
                if (!_segmentModeConfig.Value && segments.Count > 1)
                {
                    string mergedVoice = string.Join("", segments.ConvertAll(s => s.VoiceText).ToArray());
                    string mergedSub = string.Join("", segments.ConvertAll(s => s.SubtitleText).ToArray());
                    segments = new List<ChatSegment>
                    {
                        new ChatSegment
                        {
                            EmotionTag = segments[0].EmotionTag,
                            VoiceText = mergedVoice,
                            SubtitleText = mergedSub,
                        }
                    };
                }

                foreach (var chatSeg in segments) AppendChat(false, chatSeg.SubtitleText);

                string emotionTag = segments[0].EmotionTag;
                string voiceText = string.Join("", segments.ConvertAll(s => s.VoiceText).ToArray());
                string subtitleText = string.Join("", segments.ConvertAll(s => s.SubtitleText).ToArray());

                apiResult.Success = formatOk;
                apiResult.RawResponse = fullResponse;
                apiResult.EmotionTag = emotionTag;
                apiResult.VoiceText = voiceText;
                apiResult.SubtitleText = subtitleText;
                AddToMemorySystem("User", prompt);
                AddToMemorySystem("AI", formatOk ? $"[{emotionTag}] {voiceText}" : $"[格式错误] {fullResponse}");

                // 【逐句合成 + 逐句字幕】
                // 两句日语检测：只要整体含假名才配音，避免中文被念成怪声
                bool isJapanese = _japaneseCheckConfig.Value ? Regex.IsMatch(voiceText, @"[\u3040-\u309F\u30A0-\u30FF]") : true;
                Log.Info($"isJapanese: {isJapanese} (japaneseCheck: {_japaneseCheckConfig.Value}) segments: {segments.Count}");

                var segmentRuntime = new List<SegmentRuntime>();
                foreach (var seg in segments)
                {
                    segmentRuntime.Add(new SegmentRuntime
                    {
                        Segment = seg,
                        // 非日语 / 空文本的句子不送 TTS，只用字幕停留
                        Failed = !isJapanese || string.IsNullOrWhiteSpace(seg.VoiceText),
                    });
                }

                bool willTts = segmentRuntime.Exists(r => !r.Failed);
                apiResult.TtsAttempted = willTts;

                int token = BeginSegmentSession();
                if (willTts)
                {
                    StartStatusDots(myText, "收信中");
                    StartCoroutine(SynthesizeSegments(segmentRuntime, token));
                }
                else
                {
                    // 没有可配音的句子：直接标记就绪，走纯字幕停留，避免在这里等一个不会来的 Done
                    foreach (var r in segmentRuntime) r.Done = true;
                }

                yield return StartCoroutine(PlaySegments(segmentRuntime, emotionTag, myText, token));

                apiResult.TtsSucceeded = segmentRuntime.Exists(r => r.Clip != null);
            }

            // 5. 清理
            if (_abortCurrentConversation)
            {
                Log.Info("[VoiceCall] 对话流程末尾检测到打断，执行清理");
                CleanupConversationUI();
                _isProcessing = false;
                yield break;
            }

            UIHelper.RestoreUiStatus(uiStatusMap, myTextObj, originalTextObj);
            // 静音模式下重新隐藏游戏 UI
            if (wasStoryUIHidden && _storySystemUI != null) _storySystemUI.SetActive(false);
            _isProcessing = false;
            PublishConversationResult(apiResult);
        }

        IEnumerator TTSHealthCheckLoop()
        {
            while (!_isTTSServiceReady)
            {
                yield return StartCoroutine(TTSClient.CheckTTSHealthOnce(_sovitsUrlConfig.Value,Logger,(ready) =>
                {
                    _isTTSServiceReady = ready;
                }));
                yield return new WaitForSeconds(TTSHealthCheckInterval);
            }
            // TTS 就绪后发送预热请求，加速首次语音生成
            Log.Info("[TTS] 服务就绪，正在预热...");
            yield return StartCoroutine(TTSClient.DownloadVoiceWithRetry(
                _sovitsUrlConfig.Value + "/tts",  // url
                "こんにちは",                   // textToSpeak
                _targetLangConfig.Value,       // targetLang
                _refAudioPathConfig.Value,     // refPath
                _promptTextConfig.Value,       // promptText
                _promptLangConfig.Value,       // promptLang
                Logger,                        // logger
                (clip) => { Log.Info("[TTS] 预热完成！"); },  // onComplete
                1,                             // maxRetries
                10f,                           // timeoutSeconds
                _audioPathCheckConfig.Value    // audioPathCheck
            ));
        }

        IEnumerator PlayNativeAnimation(string emotion, AudioClip voiceClip)
        {
            if (GameBridge._heroineService == null || GameBridge._changeAnimSmoothMethod == null) yield break;

            Log.Info($"[动画] 执行: {emotion}");
            float clipDuration = (voiceClip != null) ? voiceClip.length : 3.0f;
            // 1. 归位 (除了喝茶)
            if (emotion != "Drink" && emotion != "Relaxed")
            {
                GameBridge.CallNativeChangeAnim(250);
                float elapsed = 0f;
                while (elapsed < 0.2f && !_abortCurrentConversation)
                {
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }
            }
            if (_abortCurrentConversation) { StopTalking(); yield break; }
            if (voiceClip != null)
            {
                // 2. 播放语音 + 动作
                Log.Info($">>> 语音({voiceClip.length:F1}s) + 动作");
                _isAISpeaking = true;
                _audioSource.clip = voiceClip;
                _audioSource.Play();
            }
            else
            {
                Log.Info($">>> 无语音模式 (格式错误或TTS失败) + 动作");
                // 没声音就不播了，只做动作
            }
            // === 特殊流程：Greeting/Wave 需要看向玩家 ===
            if (emotion == "Greeting" || emotion == "Wave")
            {
                var greetAnim = GameBridge.PickRandomAnimation("Greeting");
                GameBridge.PlayAnimation(greetAnim);
                float greetElapsed = 0f;
                while (greetElapsed < 0.3f && !_abortCurrentConversation)
                {
                    yield return null;
                    greetElapsed += Time.unscaledDeltaTime;
                }
                GameBridge.ControlLookAt(1.0f, 0.5f);
                float waitTime = Mathf.Max(clipDuration, 2.5f);
                greetElapsed = 0f;
                while (greetElapsed < waitTime && !_abortCurrentConversation)
                {
                    yield return null;
                    greetElapsed += Time.unscaledDeltaTime;
                }
                GameBridge.CallNativeChangeAnim(250);
                GameBridge.RestoreLookAt();
                StopTalking();
                yield break;
            }

            // === 通用流程：随机选动画并执行 ===
            string pickedAnim = GameBridge.PickRandomAnimation(emotion);
            Log.Info($"[动画] {emotion} -> {pickedAnim}");
            GameBridge.PlayAnimation(pickedAnim);

            // 等待语音播完，增加0.5秒缓冲，以防止过早判断AI动作结束
            float playElapsed = 0f;
            float playTimeout = clipDuration + 0.5f;
            while (playElapsed < playTimeout && !_abortCurrentConversation)
            {
                yield return null;
                playElapsed += Time.unscaledDeltaTime;
            }

            // 恢复
            if (_audioSource != null && _audioSource.isPlaying) {
                // 即使等待时间到了，语音还在播放，就强制停止进行兜底
                Log.Warning("等待结束，强制停止语音播放");
                _audioSource.Stop();
            }
            GameBridge.RestoreLookAt();
            StopTalking();
        }

        // ================= 【逐句合成 / 播放】 =================
        int BeginSegmentSession()
        {
            _segmentSessionToken++;
            return _segmentSessionToken;
        }

        bool SegmentSessionAlive(int token)
        {
            return token == _segmentSessionToken && !_abortCurrentConversation;
        }

        /// <summary>
        /// 关闭「正在说话」状态，并同步清掉 Animator 的 Enable_Talk。
        /// 只把 _isAISpeaking 置 false 是不够的：Update 里清理动画的分支要求
        /// _isAISpeaking 仍为 true 才会执行，先置 false 会让口型动作一直循环。
        /// </summary>
        void StopTalking()
        {
            _isAISpeaking = false;
            if (GameBridge._cachedAnimator != null)
            {
                try { GameBridge._cachedAnimator.SetBool("Enable_Talk", false); }
                catch { }
            }
        }

        // ── 状态提示（送信中 / 收信中）的点点动画 ──────────────────
        private Coroutine _statusDotsCoroutine;
        private string _statusBaseText = "";

        /// <summary>
        /// 显示「送信中」/「收信中」这类状态，并让末尾的点循环出现：
        /// 0.5s 一个、1.5s 三个，2.0s 清空重来。
        /// </summary>
        void StartStatusDots(Text target, string baseText)
        {
            StopStatusDots();
            _statusBaseText = baseText ?? "";
            if (target == null) return;
            target.text = _statusBaseText;
            _statusDotsCoroutine = StartCoroutine(StatusDotsRoutine(target));
        }

        void StopStatusDots()
        {
            if (_statusDotsCoroutine != null)
            {
                StopCoroutine(_statusDotsCoroutine);
                _statusDotsCoroutine = null;
            }
        }

        IEnumerator StatusDotsRoutine(Text target)
        {
            int phase = 0;
            while (true)
            {
                if (target != null) target.text = _statusBaseText + new string('.', phase);
                yield return new WaitForSecondsRealtime(0.5f);
                phase = (phase + 1) % 4;   // 0 -> 1 -> 2 -> 3 -> 回到 0（清空）
            }
        }

        // ── 聊天记录：数据 & 绘制 ────────────────────────────────
        private string ChatLogPath()
        {
            string dir = Path.Combine(BepInEx.Paths.ConfigPath, "ChillAIMod");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "chatlog.tsv");
        }

        void AppendChat(bool isUser, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _chatLog.Add(new ChatBubble { IsUser = isUser, Text = text.Trim() });
            while (_chatLog.Count > ChatLogMaxEntries) _chatLog.RemoveAt(0);
            _chatScrollToBottom = true;
            if (_chatLog.Count % 8 == 0) SaveChatLog();
        }

        void SaveChatLog()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var b in _chatLog)
                    sb.Append(b.IsUser ? "U" : "A").Append('\t')
                      .Append(b.Text.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "))
                      .Append('\n');
                File.WriteAllText(ChatLogPath(), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warning($"[ChatLog] 保存失败: {ex.Message}"); }
        }

        void LoadChatLog()
        {
            try
            {
                string path = ChatLogPath();
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    _chatLog.Add(new ChatBubble
                    {
                        IsUser = line[0] == 'U',
                        Text = line.Substring(tab + 1),
                    });
                }
                while (_chatLog.Count > ChatLogMaxEntries) _chatLog.RemoveAt(0);
                _chatScrollToBottom = true;
                Log.Info($"[ChatLog] 载入 {_chatLog.Count} 条聊天记录");
            }
            catch (Exception ex) { Log.Warning($"[ChatLog] 载入失败: {ex.Message}"); }
        }

        static Texture2D MakeRoundedTexture(int size, int radius, Color fill)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int cx = Mathf.Clamp(x, radius, size - radius - 1);
                    int cy = Mathf.Clamp(y, radius, size - radius - 1);
                    float dx = x - cx, dy = y - cy;
                    bool inside = dx * dx + dy * dy <= radius * radius + 0.5f;
                    tex.SetPixel(x, y, inside ? fill : clear);
                }
            }
            tex.Apply();
            return tex;
        }

        void EnsureBubbleStyles(int fontSize)
        {
            if (_bubbleUserStyle != null && _bubbleStyleFontSize == fontSize) return;

            if (_bubbleUserTex == null) _bubbleUserTex = MakeRoundedTexture(48, 14, new Color(0.26f, 0.42f, 0.78f, 0.95f));
            if (_bubbleAiTex == null) _bubbleAiTex = MakeRoundedTexture(48, 14, new Color(0.22f, 0.22f, 0.25f, 0.95f));

            _bubbleUserStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(fontSize, fontSize, fontSize / 2, fontSize / 2),
                border = new RectOffset(14, 14, 14, 14),
                normal = { textColor = Color.white, background = _bubbleUserTex },
                richText = true,
            };
            _bubbleAiStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(fontSize, fontSize, fontSize / 2, fontSize / 2),
                border = new RectOffset(14, 14, 14, 14),
                normal = { textColor = Color.white, background = _bubbleAiTex },
                richText = true,
            };
            _bubbleStyleFontSize = fontSize;
        }

        void DrawChatHistory(int fontSize, float elementHeight)
        {
            EnsureBubbleStyles(fontSize);
            EnsureAvatars();

            int avatarSize = Mathf.Max(24, Mathf.RoundToInt(fontSize * 2.1f));
            float maxBubbleWidth = Mathf.Max(110f, (_windowRect.width - 44f) * 0.78f - avatarSize * 1.4f);

            // 聊天区自己吃掉剩下的高度，背景保持透明，只有气泡浮在上面
            _chatScroll = GUILayout.BeginScrollView(_chatScroll, false, true,
                GUILayout.ExpandHeight(true), GUILayout.MinHeight(fontSize * 4f));

            if (_chatLog.Count == 0)
            {
                GUILayout.Label("（还没有对话）");
            }
            else
            {
                foreach (var bubble in _chatLog)
                {
                    GUILayout.BeginHorizontal();
                    if (bubble.IsUser) GUILayout.FlexibleSpace();

                    if (!bubble.IsUser)
                    {
                        if (_aiAvatarTex != null)
                            GUILayout.Label(new GUIContent(_aiAvatarTex), GUILayout.Width(avatarSize), GUILayout.Height(avatarSize));
                        GUILayout.Space(fontSize * 0.4f);
                    }

                    GUILayout.Label(bubble.Text, bubble.IsUser ? _bubbleUserStyle : _bubbleAiStyle, GUILayout.MaxWidth(maxBubbleWidth));

                    if (bubble.IsUser)
                    {
                        GUILayout.Space(fontSize * 0.4f);
                        if (_playerAvatarTex != null)
                            GUILayout.Label(new GUIContent(_playerAvatarTex), GUILayout.Width(avatarSize), GUILayout.Height(avatarSize));
                    }

                    if (bubble.IsUser) GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(fontSize * 0.35f);
                }
            }

            GUILayout.EndScrollView();

            if (_chatScrollToBottom)
            {
                _chatScroll.y = 1000000f;
                _chatScrollToBottom = false;
            }
        }

        // ── 头像 ────────────────────────────────────────────────
        void EnsureAvatars()
        {
            if (_avatarLoadAttempted) return;
            _avatarLoadAttempted = true;

            const int size = 96;
            try
            {
                _aiAvatarTex = LoadAvatarFromFile(_aiAvatarPathConfig.Value, size)
                               ?? TryFindSpriteAvatar(size, "penguin", "企鹅", "qie")
                               ?? MakeSolidCircle(size, new Color(0.16f, 0.55f, 0.78f, 1f));

                _playerAvatarTex = LoadAvatarFromFile(_playerAvatarPathConfig.Value, size)
                                   ?? TryGetSteamAvatar(size)
                                   ?? MakeSolidCircle(size, new Color(0.55f, 0.38f, 0.78f, 1f));
            }
            catch (Exception ex)
            {
                Log.Warning($"[头像] 载入失败: {ex.Message}");
            }

            if (_aiAvatarTex == null) _aiAvatarTex = MakeSolidCircle(size, new Color(0.16f, 0.55f, 0.78f, 1f));
            if (_playerAvatarTex == null) _playerAvatarTex = MakeSolidCircle(size, new Color(0.55f, 0.38f, 0.78f, 1f));
        }

        static Texture2D LoadAvatarFromFile(string path, int size)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                path = path.Trim().Trim('"');
                if (!File.Exists(path)) return null;
                var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!src.LoadImage(File.ReadAllBytes(path))) return null;
                return MakeCircleFromTexture(src, new Rect(0, 0, src.width, src.height), size);
            }
            catch { return null; }
        }

        static Texture2D TryFindSpriteAvatar(int size, params string[] keywords)
        {
            try
            {
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null || sp.texture == null) continue;
                    string n = sp.name.ToLowerInvariant();
                    foreach (var k in keywords)
                    {
                        if (n.Contains(k.ToLowerInvariant()))
                            return MakeCircleFromTexture(sp.texture, sp.textureRect, size);
                    }
                }
            }
            catch { }
            return null;
        }

        static Texture2D TryGetSteamAvatar(int size)
        {
            try
            {
                var steamFriends = Type.GetType("Steamworks.SteamFriends, com.rlabrecque.steamworks.net");
                var steamUtils = Type.GetType("Steamworks.SteamUtils, com.rlabrecque.steamworks.net");
                var steamUser = Type.GetType("Steamworks.SteamUser, com.rlabrecque.steamworks.net");
                if (steamFriends == null || steamUtils == null || steamUser == null) return null;

                object steamId = steamUser.GetMethod("GetSteamID")?.Invoke(null, null);
                if (steamId == null) return null;

                var getAvatar = steamFriends.GetMethod("GetMediumFriendAvatar", new[] { steamId.GetType() });
                if (getAvatar == null) return null;
                int handle = Convert.ToInt32(getAvatar.Invoke(null, new[] { steamId }));
                if (handle <= 0) return null;

                object[] sizeArgs = { handle, 0u, 0u };
                var getSize = steamUtils.GetMethod("GetImageSize");
                if (getSize == null || !(bool)getSize.Invoke(null, sizeArgs)) return null;
                int w = (int)(uint)sizeArgs[1];
                int h = (int)(uint)sizeArgs[2];
                if (w <= 0 || h <= 0) return null;

                var buffer = new byte[w * h * 4];
                var getRgba = steamUtils.GetMethod("GetImageRGBA");
                if (getRgba == null || !(bool)getRgba.Invoke(null, new object[] { handle, buffer, buffer.Length })) return null;

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var colors = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (h - 1 - y) * w * 4;   // Steam 头像自下而上
                    for (int x = 0; x < w; x++)
                    {
                        int si = srcRow + x * 4;
                        colors[y * w + x] = new Color32(buffer[si], buffer[si + 1], buffer[si + 2], buffer[si + 3]);
                    }
                }
                tex.SetPixels32(colors);
                tex.Apply();
                return MakeCircleFromTexture(tex, new Rect(0, 0, w, h), size);
            }
            catch { return null; }
        }

        static Texture2D MakeSolidCircle(int size, Color fill)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var clear = new Color(0f, 0f, 0f, 0f);
            float r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - (size - 1) * 0.5f, dy = y - (size - 1) * 0.5f;
                    tex.SetPixel(x, y, dx * dx + dy * dy <= r * r ? fill : clear);
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D MakeCircleFromTexture(Texture src, Rect uv, int size)
        {
            RenderTexture rt = null;
            var prev = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                float side = Mathf.Min(uv.width, uv.height);
                var scale = new Vector2(side / src.width, side / src.height);
                var offset = new Vector2(
                    (uv.x + uv.width * 0.5f) / src.width - scale.x * 0.5f,
                    (uv.y + uv.height * 0.5f) / src.height - scale.y * 0.5f);
                Graphics.Blit(src, rt, scale, offset);

                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();

                float r = size * 0.5f - 0.5f;
                var px = tex.GetPixels();
                var clear = new Color(0f, 0f, 0f, 0f);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - (size - 1) * 0.5f, dy = y - (size - 1) * 0.5f;
                        if (dx * dx + dy * dy > r * r) px[y * size + x] = clear;
                    }
                }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            }
            catch { return null; }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                RenderTexture.active = prev;
            }
        }

        /// <summary>
        /// 后台顺序合成：一句一个请求。播放协程与它并行，靠 Done 标志推进。
        /// 单句短、请求小，因此比整段更不容易触发 30 秒超时。
        /// </summary>
        IEnumerator SynthesizeSegments(List<SegmentRuntime> runtime, int token)
        {
            // 服务端是单卡推理，但一次请求里含 CPU 段（文本前端 / BERT），
            // 所以同时丢 2 个请求能拿到 15%~30% 的吞吐提升；再多收益递减。
            int workers = Mathf.Clamp(_segmentParallelConfig.Value, 1, 3);
            Log.Info($"[分段] 合成并发数 {workers}");

            int next = 0;
            int running = 0;

            while (SegmentSessionAlive(token))
            {
                while (running < workers && next < runtime.Count)
                {
                    int index = next++;
                    var pending = runtime[index];

                    if (pending.Failed)
                    {
                        pending.Done = true;
                        continue;
                    }

                    running++;
                    StartCoroutine(SynthesizeOne(pending, index, runtime.Count, token, () => running--));
                }

                if (next >= runtime.Count && running <= 0) yield break;
                yield return null;
            }
        }

        /// <summary>
        /// 单句合成。并发时由多个实例同时跑，靠计数器判断整体是否结束。
        /// </summary>
        IEnumerator SynthesizeOne(SegmentRuntime item, int index, int total, int token, Action onFinished)
        {
            int timeout = Mathf.Max(10, Mathf.RoundToInt(_segmentTimeoutConfig.Value));
            int retries = Mathf.Clamp(_segmentRetriesConfig.Value, 1, 4);

            AudioClip clip = null;
            try
            {
                Log.Info($"[分段] 合成第 {index + 1}/{total} 句（{item.Segment.VoiceText.Length} 字）");
                yield return StartCoroutine(TTSClient.DownloadVoiceWithRetry(
                    _sovitsUrlConfig.Value + "/tts",
                    item.Segment.VoiceText,
                    _targetLangConfig.Value,
                    _refAudioPathConfig.Value,
                    _promptTextConfig.Value,
                    _promptLangConfig.Value,
                    Logger,
                    c => clip = c,
                    retries,
                    timeout,
                    _audioPathCheckConfig.Value));
            }
            finally
            {
                if (onFinished != null) onFinished();
            }

            if (!SegmentSessionAlive(token)) yield break;

            item.Clip = clip;
            item.Failed = clip == null;
            item.Done = true;
            Log.Info($"[分段] 第 {index + 1} 句 {(clip != null ? "合成完成 " + clip.length.ToString("F2") + "s" : "合成失败，改为字幕停留")}");
        }

        /// <summary>
        /// 逐句播放：先等前 N 句合成完（预缓冲），之后边播边等后面的句子。
        /// 字幕只在对应句子开始播放时替换。
        /// </summary>
        IEnumerator PlaySegments(List<SegmentRuntime> runtime, string emotion, Text myText, int token)
        {
            if (runtime == null || runtime.Count == 0) yield break;

            int buffer = Mathf.Clamp(_segmentBufferConfig.Value, 1, 5);
            int needReady = Mathf.Min(buffer, runtime.Count);
            float maxWait = Mathf.Clamp(_segmentPrebufferMaxWaitConfig.Value, 0f, 60f);
            Log.Info($"[分段] 共 {runtime.Count} 句，预缓冲 {needReady} 句，最长等待 {maxWait:F0}s");

            // 1. 预缓冲：攒够 needReady 句就开播；若中途某句在重试，最多等 maxWait 秒就先开口
            float waited = 0f;
            while (SegmentSessionAlive(token))
            {
                int doneCount = 0;
                for (int i = 0; i < runtime.Count; i++) if (runtime[i].Done) doneCount++;

                if (runtime[0].Done && (doneCount >= needReady || waited >= maxWait))
                {
                    if (doneCount < needReady)
                        Log.Info($"[分段] 预缓冲超时（只就绪 {doneCount} 句），先开始播放");
                    break;
                }

                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!SegmentSessionAlive(token)) yield break;

            // 2. 归位 + 挑一个动作（整条回复只演一次，避免每句都抽搐）
            if (GameBridge._heroineService != null && GameBridge._changeAnimSmoothMethod != null)
            {
                if (emotion != "Drink" && emotion != "Relaxed")
                {
                    GameBridge.CallNativeChangeAnim(250);
                    float warm = 0f;
                    while (warm < 0.2f && SegmentSessionAlive(token))
                    {
                        yield return null;
                        warm += Time.unscaledDeltaTime;
                    }
                }

                if (SegmentSessionAlive(token))
                {
                    string pickedAnim = GameBridge.PickRandomAnimation(emotion);
                    Log.Info($"[动画] {emotion} -> {pickedAnim}");
                    GameBridge.PlayAnimation(pickedAnim);
                    if (emotion == "Greeting" || emotion == "Wave") GameBridge.ControlLookAt(1.0f, 0.5f);
                }
            }

            // 3. 一句一句播
            for (int i = 0; i < runtime.Count; i++)
            {
                if (!SegmentSessionAlive(token))
                {
                    if (_audioSource != null && _audioSource.isPlaying) _audioSource.Stop();
                    StopTalking();
                    yield break;
                }

                var item = runtime[i];

                // 下一句还没合成好就等它（不提前显示下一句字幕）
                if (!item.Done)
                {
                    Log.Info($"[分段] 等待第 {i + 1} 句合成完成");
                    while (!item.Done && SegmentSessionAlive(token)) yield return null;
                    if (!SegmentSessionAlive(token)) yield break;
                }

                // 游戏自己的 UI 逻辑可能在长时间播放后把消息窗口收起来（或把 CanvasGroup 渐隐），
                // 这里每句都强制恢复一次可见性，否则长时间播放到后半段会只剩声音没有字幕。
                bool subtitleWasHidden = !myText.gameObject.activeInHierarchy;
                UIHelper.ForceShowWindow(myText.gameObject, null);
                if (subtitleWasHidden)
                    Log.Info($"[分段] 字幕对象被隐藏，第 {i + 1} 句前重新显示");

                StopStatusDots();
                string subtitle = ResponseParser.InsertLineBreaks(item.Segment.SubtitleText, 25);
                myText.text = subtitle;
                myText.color = Color.white;

                LastSegmentSubtitle = item.Segment.SubtitleText;
                try { SegmentShown?.Invoke(item.Segment.SubtitleText); }
                catch (Exception ex) { Log.Warning($"[PublicApi] SegmentShown 回调异常: {ex.Message}"); }

                if (item.Clip != null)
                {
                    if (!item.Clip.LoadAudioData()) yield return null;

                    _isAISpeaking = true;
                    _audioSource.clip = item.Clip;
                    _audioSource.Play();
                    Log.Info($"[分段] 播放第 {i + 1}/{runtime.Count} 句（{item.Clip.length:F2}s）：{subtitle}");

                    float elapsed = 0f;
                    float limit = item.Clip.length + 3f;
                    yield return null;
                    while (_audioSource.isPlaying && elapsed < limit && SegmentSessionAlive(token))
                    {
                        elapsed += Time.unscaledDeltaTime;
                        yield return null;
                    }
                    if (_audioSource.isPlaying) _audioSource.Stop();
                    StopTalking();

                    yield return new WaitForSecondsRealtime(0.15f);
                }
                else
                {
                    // 这句没合成出来：用可读的停留时间展示字幕，再继续下一句
                    float readTime = Mathf.Clamp(item.Segment.SubtitleText.Length * 0.12f, 1.5f, 8f);
                    Log.Warning($"[分段] 第 {i + 1} 句无语音，字幕停留 {readTime:F1}s：{subtitle}");

                    float elapsed = 0f;
                    while (elapsed < readTime && SegmentSessionAlive(token))
                    {
                        elapsed += Time.unscaledDeltaTime;
                        yield return null;
                    }
                }
            }

            StopTalking();
            if (GameBridge._heroineService != null) GameBridge.RestoreLookAt();
        }

        // ================= 【新增录音控制】 =================
        void StartRecording()
        {
            Log.Info($"[Mic Debug] 检测到设备数量: {Microphone.devices.Length}");
            if (Microphone.devices.Length > 0)
            {
                foreach (var d in Microphone.devices)
                {
                    Log.Info($"[Mic Debug] 可用设备: {d}");
                }
            }
            // --------------------

            if (Microphone.devices.Length == 0)
            {
                Log.Error("未检测到麦克风！(Microphone.devices is empty)");
                // 可以在屏幕上显示个错误提示
                _playerInput = "[Error: No Mic Found]"; 
                return;
            }

            _microphoneDevice = Microphone.devices[0];
            bool loop = _continuousCallActive;
            _recordingClip = Microphone.Start(_microphoneDevice, loop, MaxRecordingSeconds, RecordingFrequency);
            _isRecording = true;
            _lastMicrophonePosition = 0;
            if (loop)
            {
                _vad?.Reset();
                Log.Info($"[VoiceCall] 开始循环监听: {_microphoneDevice}");
            }
            else
            {
                Log.Info($"开始录音: {_microphoneDevice}");
            }
        }

        void StopRecordingAndRecognize()
        {
            if (!_isRecording) return;

            // 1. 停止录音
            int position = Microphone.GetPosition(_microphoneDevice);
            Microphone.End(_microphoneDevice);
            _isRecording = false;
            Log.Info($"停止录音，采样点: {position}");

            // 2. 剪裁有效音频 (去掉末尾的静音/空白部分)
            if (position <= 0) return; // 录音太短

            AudioClip validClip = AudioUtils.TrimAudioClip(_recordingClip, position);

            // 3. 编码并发送
            byte[] wavData = AudioUtils.EncodeToWAV(validClip);
            StartCoroutine(ASRWorkflow(wavData));
        }
        /// <summary>
        /// ASR 业务流：负责调度网络请求和后续的 AI 响应
        /// </summary>
        IEnumerator ASRWorkflow(byte[] wavData, string inputSource = "asr")
        {
            _isProcessing = true; // 锁定 UI
            string recognizedResult = "";

            // A. 调用 ApiService 只负责拿回文字
            yield return StartCoroutine(ASRClient.SendAudioToASR(
                wavData,
                _sovitsUrlConfig.Value,
                (text) => recognizedResult = text
            ));

            // B. 根据拿回的结果，在主类决定下一步业务走向
            if (!string.IsNullOrEmpty(recognizedResult))
            {
                Log.Info($"[Workflow] ASR 成功，开始进入 AI 思考流程: {recognizedResult}");

                // 这里触发 AI 处理流程
                _currentConversationCoroutine = StartCoroutine(AIProcessRoutine(recognizedResult, inputSource));
                yield return _currentConversationCoroutine;
            }
            else
            {
                Log.Warning("[Workflow] ASR 未能识别到有效文本");
                _isProcessing = false; // 如果识别失败，在这里解锁 UI
                PublishConversationResult(new AIChatApiConversationResult
                {
                    Success = false,
                    IsApiError = true,
                    ErrorCode = -3,
                    ErrorMessage = "ASR returned empty text.",
                    InputSource = inputSource,
                    UserPrompt = string.Empty,
                    EmotionTag = "Think",
                    VoiceText = string.Empty,
                    SubtitleText = string.Empty,
                    RawResponse = string.Empty,
                    TtsAttempted = false,
                    TtsSucceeded = false,
                    TimestampUtc = DateTime.UtcNow
                });
            }
        }

        // ================= 【新增：持续通话模式】 =================

        void StartContinuousCall()
        {
            if (_continuousCallActive) return;
            if (Microphone.devices.Length == 0)
            {
                Log.Error("[VoiceCall] 未检测到麦克风，无法开启持续通话");
                _playerInput = "[Error: No Mic Found]";
                return;
            }

            _continuousCallActive = true;
            _enableVoiceCallConfig.Value = true;
            Config.Save();

            _vad = new VoiceActivityDetector(
                RecordingFrequency,
                _vadThresholdConfig.Value,
                _vadMinSpeechConfig.Value,
                _vadSilenceConfig.Value,
                MaxRecordingSeconds,
                0.02f,
                _vadEndThresholdRatioConfig.Value);
            _vad.SpeechSegmentReady += OnVadSpeechSegmentReady;

            if (!_isRecording)
            {
                StartRecording();
            }

            Log.Info("[VoiceCall] 持续通话模式已开启");
        }

        void StopContinuousCall()
        {
            if (!_continuousCallActive) return;

            _continuousCallActive = false;
            _enableVoiceCallConfig.Value = false;
            Config.Save();

            if (_vad != null)
            {
                _vad.SpeechSegmentReady -= OnVadSpeechSegmentReady;
                _vad = null;
            }

            if (_resumeListeningCoroutine != null)
            {
                StopCoroutine(_resumeListeningCoroutine);
                _resumeListeningCoroutine = null;
            }

            if (_isRecording)
            {
                Microphone.End(_microphoneDevice);
                _isRecording = false;
            }

            Log.Info("[VoiceCall] 持续通话模式已关闭");
        }

        void PollContinuousMicrophone()
        {
            if (_recordingClip == null || _microphoneDevice == null) return;

            int currentPos = Microphone.GetPosition(_microphoneDevice);
            if (currentPos < 0) return;

            int totalSamples = _recordingClip.samples;
            int samplesToRead = (currentPos - _lastMicrophonePosition + totalSamples) % totalSamples;
            if (samplesToRead <= 0) return;

            // 分配精确大小的缓冲区，避免读到旧数据或越界
            float[] samples = new float[samplesToRead];
            int firstPart = Math.Min(samplesToRead, totalSamples - _lastMicrophonePosition);

            if (firstPart > 0)
            {
                float[] firstBuffer = new float[firstPart];
                _recordingClip.GetData(firstBuffer, _lastMicrophonePosition);
                Array.Copy(firstBuffer, 0, samples, 0, firstPart);
            }

            if (firstPart < samplesToRead)
            {
                int secondPart = samplesToRead - firstPart;
                float[] secondBuffer = new float[secondPart];
                _recordingClip.GetData(secondBuffer, 0);
                Array.Copy(secondBuffer, 0, samples, firstPart, secondPart);
            }

            _lastMicrophonePosition = currentPos;
            _vad?.Process(samples);
        }

        void OnVadSpeechSegmentReady(float[] samples)
        {
            Log.Info($"[VoiceCall] VAD 检测到语音段，长度 {samples.Length / (float)RecordingFrequency:F2}s");

            byte[] wavData = AudioUtils.EncodeToWAV(samples, RecordingFrequency, 1);
            if (wavData == null || wavData.Length == 0) return;

            if (_isProcessing)
            {
                if (_voiceCallBargeInConfig.Value)
                {
                    Log.Info("[VoiceCall] AI 忙碌中，启用打断");
                    InterruptCurrentConversation();
                }
                else
                {
                    Log.Info("[VoiceCall] AI 忙碌中，忽略本次语音（未开启打断）");
                    return;
                }
            }

            StartCoroutine(ASRWorkflow(wavData, "voicecall"));
        }

        void InterruptCurrentConversation()
        {
            Log.Info("[VoiceCall] 打断当前 AI 回复");
            _abortCurrentConversation = true;
            // 让逐句合成的后台协程立刻失效，不再占用 TTS 服务
            _segmentSessionToken++;

            if (_audioSource != null && _audioSource.isPlaying)
            {
                _audioSource.Stop();
            }
            StopTalking();

            if (_currentConversationCoroutine != null)
            {
                StopCoroutine(_currentConversationCoroutine);
                _currentConversationCoroutine = null;
            }

            CleanupConversationUI();
            _isProcessing = false;
        }

        void CleanupConversationUI()
        {
            StopStatusDots();
            try
            {
                if (_currentUiStatusMap != null)
                {
                    UIHelper.RestoreUiStatus(_currentUiStatusMap, _currentOverlayTextObj, _currentOriginalTextObj);
                }
                if (_currentWasStoryUIHidden && _storySystemUI != null)
                {
                    _storySystemUI.SetActive(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[VoiceCall] 清理 UI 时异常: {ex.Message}");
            }
            finally
            {
                _currentOverlayTextObj = null;
                _currentOriginalTextObj = null;
                _currentUiStatusMap = null;
                _currentWasStoryUIHidden = false;
            }
        }

        IEnumerator ResumeListeningAfterDelay(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (_continuousCallActive && !_isRecording && !_isProcessing)
            {
                StartRecording();
            }
            _resumeListeningCoroutine = null;
        }

        void OnApplicationQuit()
        {
            Instance = null;
            SaveChatLog();
            StopContinuousCall();
            Log.Info("[Chill AI Mod] 退出中...");
            
            // 【保存记忆系统】
            if (_hierarchicalMemory != null && _experimentalMemoryConfig.Value)
            {
                Log.Info("[HierarchicalMemory] 正在保存记忆...");
                _hierarchicalMemory.SaveToFile();
            }
            
            Log.Info("[Chill AI Mod] 正在停止TTS轮询...");
            if (_ttsHealthCheckCoroutine != null)
            {
                StopCoroutine(_ttsHealthCheckCoroutine);
                _ttsHealthCheckCoroutine = null;
            }
            if (_quitTTSServiceOnQuitConfig.Value && _launchedTTSProcess != null && !_launchedTTSProcess.HasExited)
            {   
                try
                {
                    ProcessHelper.KillProcessTree(_launchedTTSProcess);
                    Log.Info("TTS 服务已关闭");
                }
                catch (Exception ex)
                {
                    Log.Warning($"关闭 TTS 服务时出错: {ex.Message}");
                }
            }
        }
        
        // ================= 【分层记忆系统相关方法】 =================

        /// <summary>
        /// 初始化分层记忆系统
        /// </summary>
        private void InitializeHierarchicalMemory()
        {
            Func<string, Task<string>> llmSummarizer = async (prompt) => await CallLlmForSummaryAsync(prompt);
            string memoryFilePath = Path.Combine(BepInEx.Paths.ConfigPath, "ChillAIMod", "memory.txt");

            _hierarchicalMemory = new HierarchicalMemory(
                llmSummarizer, 3, 10, 6, 5, memoryFilePath
            );
        }

        /// <summary>
        /// 调用 LLM 进行文本总结（将协程包装为 Task）
        /// </summary>
        private async Task<string> CallLlmForSummaryAsync(string prompt)
        {
            var tcs = new TaskCompletionSource<string>();

            // 使用协程调用 LLM
            StartCoroutine(CallLlmForSummaryCoroutine(prompt, (result) =>
            {
                tcs.SetResult(result);
            }));

            return await tcs.Task;
        }

        /// <summary>
        /// 协程：调用 LLM 进行文本总结
        /// </summary>
        private IEnumerator CallLlmForSummaryCoroutine(string prompt, Action<string> onComplete)
        {
            Log.Info("[HierarchicalMemory] >>> 开始调用 LLM 进行总结...");

            var requestContext = new LLMRequestContext
            {
                ApiUrl = _chatApiUrlConfig.Value,
                ApiKey = _apiKeyConfig.Value,
                ModelName = _modelConfig.Value,
                SystemPrompt = "你是一个专业的文本总结助手。",
                UserPrompt = prompt,
                UseLocalOllama = _useOllama.Value,
                LogApiRequestBody = _logApiRequestBodyConfig.Value,
                ThinkMode = _thinkModeConfig.Value,
                HierarchicalMemory = null,
                LogHeader = "HierarchicalMemory",
                FixApiPathForThinkMode = _fixApiPathForThinkModeConfig.Value
            };

            yield return LLMClient.SendLLMRequest(
                requestContext,
                rawResponse => 
                {
                    string summary = requestContext.UseLocalOllama
                        ? ResponseParser.ExtractContentFromOllama(rawResponse)
                        : ResponseParser.ExtractContentRegex(rawResponse);
                    onComplete?.Invoke(summary);
                },
                (errorMsg, responseCode) => 
                {
                    onComplete?.Invoke("[总结失败]");
                }
            );

            Log.Info("[HierarchicalMemory] <<< 总结调用完成");
        }

        /// <summary>
        /// 将对话添加到记忆系统中（如果启用）
        /// 注意：已改为后台异步处理，不阻塞主流程
        /// </summary>
        private void AddToMemorySystem(string role, string content)
        {
            if (_hierarchicalMemory != null && _experimentalMemoryConfig.Value)
            {
                _hierarchicalMemory.AddMessage($"{role}: {content}");
            }
        }

        /// <summary>
        /// 在屏幕右面的按钮最下面添加一个AI聊天按钮
        /// </summary>
        private void AddAIChatButtonToRightIcons()
        {
            try
            {
                // 查找RightIcons容器（参考UIRearrangePatch.cs中的路径）
                string topIconsPath = "Paremt/PCPlatform/Canvas/UI/MostFrontArea/TopIcons";
                GameObject rightIcons = GameObject.Find(topIconsPath);
                
                if (rightIcons == null)
                {
                    Log.Warning($"找不到TopIcons容器: {topIconsPath}");
                    return;
                }
                
                // 创建新按钮游戏对象
                _aiChatButton = new GameObject("IconAIChat_Button");
                
                // 设置为RightIcons的子节点
                _aiChatButton.transform.SetParent(rightIcons.transform, false);
                
                // 添加RectTransform组件
                RectTransform rectTransform = _aiChatButton.AddComponent<RectTransform>();
                
                // 获取RightIcons中其他按钮的大小作为参考
                float buttonSize = 60f; // 默认大小
                if (rightIcons.transform.childCount > 0)
                {
                    RectTransform firstButtonRect = rightIcons.transform.GetChild(0).GetComponent<RectTransform>();
                    if (firstButtonRect != null)
                    {
                        buttonSize = Mathf.Max(firstButtonRect.sizeDelta.x, firstButtonRect.sizeDelta.y);
                    }
                }
                
                // 设置按钮大小
                rectTransform.sizeDelta = new Vector2(buttonSize, buttonSize);

                // 添加Image组件
                Image image = _aiChatButton.AddComponent<Image>();

                try
                {
                    image.sprite = EmbeddedSpriteLoader.Load("ai_chat.png");
                    image.color = Color.white;
                    image.preserveAspect = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"加载内置图片失败: {ex}");
                    image.color = Color.red; // 兜底
                }


                // 添加Button组件
                Button button = _aiChatButton.AddComponent<Button>();
                
                // 添加点击事件
                button.onClick.AddListener(() =>
                {
                    _showInputWindow = !_showInputWindow;
                });
                
                // 设置按钮位置到最底部
                // 获取所有子节点并按位置排序
                List<RectTransform> children = new List<RectTransform>();
                for (int i = 0; i < rightIcons.transform.childCount; i++)
                {
                    RectTransform childRect = rightIcons.transform.GetChild(i).GetComponent<RectTransform>();
                    if (childRect != null)
                    {
                        children.Add(childRect);
                    }
                }
                
                // 按Y坐标排序（Unity UI中Y值越小越靠下）
                children.Sort((a, b) => a.anchoredPosition.y.CompareTo(b.anchoredPosition.y));
                
                // 如果有其他按钮，将新按钮放在最下面
                if (children.Count > 1) // 至少有一个其他按钮
                {
                    RectTransform lowestButton = children[3]; // 第一个是最下面的
                    float spacing = 10f;
                    rectTransform.anchoredPosition = new Vector2(
                        lowestButton.anchoredPosition.x,
                        lowestButton.anchoredPosition.y - (buttonSize + spacing)
                    );
                }
                else
                {
                    // 如果是第一个按钮，居中放置
                    rectTransform.anchoredPosition = Vector2.zero;
                }
                
                // 设置锚点和pivot，使其与其他按钮一致
                rectTransform.anchorMin = new Vector2(1f, 1f);
                rectTransform.anchorMax = new Vector2(1f, 1f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);
                
                _aiChatButtonAdded = true;
                Log.Info($"✅ AI聊天按钮已添加到RightIcons容器");
            }
            catch (Exception ex)
            {
                Log.Error($"添加AI聊天按钮失败: {ex.Message}");
            }
        }

        // ================= 【静音游戏原生角色】 =================
        private GameObject _storySystemUI = null;
        private bool _muteActive = false; // 实际静音是否已激活

        private void ApplyGameAudioMute(bool mute)
        {
            if (mute)
            {
                if (_startupComplete)
                {
                    // 开场动画已结束，立即生效
                    _muteActive = true;
                    DoMuteGameNative();
                    Log.Info("[游戏原生] 静音已立即生效");
                }
                else
                {
                    // 开场动画还没结束，等 LateUpdate 延迟生效
                    Log.Info("[游戏原生] 静音已开启，等待开场动画结束后生效...");
                }
            }
            else
            {
                // 关闭时立即恢复
                _muteActive = false;
                try
                {
                    var allSources = FindObjectsOfType<AudioSource>();
                    foreach (var src in allSources)
                    {
                        if (src == _audioSource) continue;
                        src.mute = false;
                    }

                    if (_storySystemUI != null) _storySystemUI.SetActive(true);

                    Log.Info("[游戏原生] 已恢复所有音频/UI");
                }
                catch (Exception ex)
                {
                    Log.Error($"[游戏原生] 恢复失败: {ex.Message}");
                }
            }
        }

        private void DoMuteGameNative()
        {
            try
            {
                // 1. 静音所有非 Mod 的音源
                var allSources = FindObjectsOfType<AudioSource>();
                foreach (var src in allSources)
                {
                    if (src == _audioSource) continue;
                    src.mute = true;
                }

                // 2. 隐藏游戏原生对话文本窗口
                if (_storySystemUI == null)
                {
                    GameObject canvas = GameObject.Find("Canvas");
                    if (canvas != null)
                    {
                        Transform storyUI = canvas.transform.Find("StorySystemUI");
                        if (storyUI != null) _storySystemUI = storyUI.gameObject;
                    }
                }
                if (_storySystemUI != null) _storySystemUI.SetActive(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[游戏原生] 静音失败: {ex.Message}");
            }
        }

        // 定期检查并维持静音状态
        private float _muteCheckTimer = 0f;
        private float _startupTimer = 0f;
        private bool _startupComplete = false;
        void LateUpdate()
        {
            if (!_muteGameNativeAudio.Value) return;

            // 等待开场动画结束（延迟15秒后才开始静音）
            if (!_startupComplete)
            {
                _startupTimer += Time.unscaledDeltaTime;
                if (_startupTimer < 15f) return;
                _startupComplete = true;
                _muteActive = true;
                DoMuteGameNative();
                Log.Info("[游戏原生] 开场动画已结束，静音生效");
            }

            if (!_muteActive) return;

            // AI 正在说话时不隐藏 UI（Mod 字幕需要显示）
            if (_isProcessing || _isAISpeaking) return;

            _muteCheckTimer += Time.unscaledDeltaTime;
            if (_muteCheckTimer >= 2f)
            {
                _muteCheckTimer = 0f;
                DoMuteGameNative();
            }
        }

        private void PublishConversationResult(AIChatApiConversationResult result)
        {
            try
            {
                ConversationCompleted?.Invoke(result);
            }
            catch (Exception ex)
            {
                Log.Error($"[PublicApi] ConversationCompleted 回调异常: {ex.Message}");
            }

            // 持续通话模式：本轮对话结束后自动恢复监听
            if (_continuousCallActive && !_isRecording && !_isProcessing)
            {
                float delay = _voiceCallResumeDelayConfig.Value;
                if (_resumeListeningCoroutine != null)
                {
                    StopCoroutine(_resumeListeningCoroutine);
                }
                _resumeListeningCoroutine = StartCoroutine(ResumeListeningAfterDelay(delay));
            }
        }

        private Dictionary<string, ConfigEntryBase> GetConfigEntryMap()
        {
            return new Dictionary<string, ConfigEntryBase>(StringComparer.OrdinalIgnoreCase)
            {
                ["Use_Ollama_API"] = _useOllama,
                ["ThinkMode"] = _thinkModeConfig,
                ["API_URL"] = _chatApiUrlConfig,
                ["API_Key"] = _apiKeyConfig,
                ["ModelName"] = _modelConfig,
                ["LogApiRequestBody"] = _logApiRequestBodyConfig,
                ["FixApiPathForThinkMode"] = _fixApiPathForThinkModeConfig,
                ["TTS_Service_URL"] = _sovitsUrlConfig,
                ["TTS_Service_Script_Path"] = _TTSServicePathConfig,
                ["LaunchTTSService"] = _LaunchTTSServiceConfig,
                ["QuitTTSServiceOnQuit"] = _quitTTSServiceOnQuitConfig,
                ["Audio_File_Path"] = _refAudioPathConfig,
                ["Audio_File_Text"] = _promptTextConfig,
                ["PromptLang"] = _promptLangConfig,
                ["TargetLang"] = _targetLangConfig,
                ["AudioPathCheck"] = _audioPathCheckConfig,
                ["JapaneseCheck"] = _japaneseCheckConfig,
                ["VoiceVolume"] = _voiceVolumeConfig,
                ["SegmentMode"] = _segmentModeConfig,
                ["SegmentBufferCount"] = _segmentBufferConfig,
                ["SegmentPrebufferMaxSeconds"] = _segmentPrebufferMaxWaitConfig,
                ["SegmentTimeoutSeconds"] = _segmentTimeoutConfig,
                ["SegmentRetries"] = _segmentRetriesConfig,
                ["SegmentParallelRequests"] = _segmentParallelConfig,
                ["SystemPrompt"] = _personaConfig,
                ["ExperimentalMemory"] = _experimentalMemoryConfig,
                ["ReverseEnterBehavior"] = _reverseEnterBehaviorConfig,
                ["BackgroundOpacity"] = _backgroundOpacity,
                ["ShowWindowTitle"] = _showWindowTitle,
                ["WindowWidth"] = _windowWidthConfig,
                ["WindowHeightBase"] = _windowHeightConfig,
                ["Enable_VoiceCall"] = _enableVoiceCallConfig,
                ["Vad_EnergyThreshold"] = _vadThresholdConfig,
                ["Vad_MinSpeechSeconds"] = _vadMinSpeechConfig,
                ["Vad_SilenceSeconds"] = _vadSilenceConfig,
                ["Vad_EndThresholdRatio"] = _vadEndThresholdRatioConfig,
                ["VoiceCall_BargeIn"] = _voiceCallBargeInConfig,
                ["VoiceCall_ResumeDelay"] = _voiceCallResumeDelayConfig
            };
        }

        public Dictionary<string, string> GetAllConfigValues()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in GetConfigEntryMap())
            {
                result[kv.Key] = kv.Value?.BoxedValue?.ToString() ?? string.Empty;
            }
            return result;
        }

        public Dictionary<string, string> GetAllConfigDefaultValues()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in GetConfigEntryMap())
            {
                result[kv.Key] = kv.Value?.DefaultValue?.ToString() ?? string.Empty;
            }
            return result;
        }

        public string GetConfigValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var map = GetAllConfigValues();
            return map.TryGetValue(key, out var value) ? value : null;
        }

        public bool TrySetConfigValue(string key, string value, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                error = "key is empty";
                return false;
            }

            try
            {
                switch (key)
                {
                    case "Use_Ollama_API": _useOllama.Value = bool.Parse(value); break;
                    case "ThinkMode": _thinkModeConfig.Value = (ThinkMode)Enum.Parse(typeof(ThinkMode), value, true); break;
                    case "API_URL": _chatApiUrlConfig.Value = value ?? string.Empty; break;
                    case "API_Key": _apiKeyConfig.Value = value ?? string.Empty; break;
                    case "ModelName": _modelConfig.Value = value ?? string.Empty; break;
                    case "LogApiRequestBody": _logApiRequestBodyConfig.Value = bool.Parse(value); break;
                    case "FixApiPathForThinkMode": _fixApiPathForThinkModeConfig.Value = bool.Parse(value); break;
                    case "TTS_Service_URL": _sovitsUrlConfig.Value = value ?? string.Empty; break;
                    case "TTS_Service_Script_Path": _TTSServicePathConfig.Value = value ?? string.Empty; break;
                    case "LaunchTTSService": _LaunchTTSServiceConfig.Value = bool.Parse(value); break;
                    case "QuitTTSServiceOnQuit": _quitTTSServiceOnQuitConfig.Value = bool.Parse(value); break;
                    case "Audio_File_Path": _refAudioPathConfig.Value = value ?? string.Empty; break;
                    case "Audio_File_Text": _promptTextConfig.Value = value ?? string.Empty; break;
                    case "PromptLang": _promptLangConfig.Value = value ?? string.Empty; break;
                    case "TargetLang": _targetLangConfig.Value = value ?? string.Empty; break;
                    case "AudioPathCheck": _audioPathCheckConfig.Value = bool.Parse(value); break;
                    case "JapaneseCheck": _japaneseCheckConfig.Value = bool.Parse(value); break;
                    case "VoiceVolume": _voiceVolumeConfig.Value = Mathf.Clamp(float.Parse(value), 0f, 1f); _audioSource.volume = _voiceVolumeConfig.Value; break;
                    case "SegmentMode": _segmentModeConfig.Value = bool.Parse(value); break;
                    case "SegmentBufferCount": _segmentBufferConfig.Value = Mathf.Clamp(int.Parse(value), 1, 5); break;
                    case "SegmentPrebufferMaxSeconds": _segmentPrebufferMaxWaitConfig.Value = Mathf.Clamp(float.Parse(value), 0f, 60f); break;
                    case "SegmentTimeoutSeconds": _segmentTimeoutConfig.Value = Mathf.Max(10f, float.Parse(value)); break;
                    case "SegmentRetries": _segmentRetriesConfig.Value = Mathf.Clamp(int.Parse(value), 1, 4); break;
                    case "SegmentParallelRequests": _segmentParallelConfig.Value = Mathf.Clamp(int.Parse(value), 1, 3); break;
                    case "SystemPrompt": _personaConfig.Value = value ?? string.Empty; break;
                    case "ExperimentalMemory": _experimentalMemoryConfig.Value = bool.Parse(value); break;
                    case "ReverseEnterBehavior": _reverseEnterBehaviorConfig.Value = bool.Parse(value); break;
                    case "BackgroundOpacity": _backgroundOpacity.Value = Mathf.Clamp(float.Parse(value), 0f, 1f); break;
                    case "ShowWindowTitle": _showWindowTitle.Value = bool.Parse(value); break;
                    case "WindowWidth": _windowWidthConfig.Value = Mathf.Max(300f, float.Parse(value)); break;
                    case "WindowHeightBase": _windowHeightConfig.Value = Mathf.Max(100f, float.Parse(value)); break;
                    case "Enable_VoiceCall": _enableVoiceCallConfig.Value = bool.Parse(value); break;
                    case "Vad_EnergyThreshold": _vadThresholdConfig.Value = Mathf.Max(0.0001f, float.Parse(value)); break;
                    case "Vad_MinSpeechSeconds": _vadMinSpeechConfig.Value = Mathf.Max(0.05f, float.Parse(value)); break;
                    case "Vad_SilenceSeconds": _vadSilenceConfig.Value = Mathf.Max(0.1f, float.Parse(value)); break;
                    case "Vad_EndThresholdRatio": _vadEndThresholdRatioConfig.Value = Mathf.Clamp(float.Parse(value), 0.1f, 1.0f); break;
                    case "VoiceCall_BargeIn": _voiceCallBargeInConfig.Value = bool.Parse(value); break;
                    case "VoiceCall_ResumeDelay": _voiceCallResumeDelayConfig.Value = Mathf.Max(0f, float.Parse(value)); break;
                    default:
                        error = $"unknown config key: {key}";
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TrySaveConfig(out string error)
        {
            error = string.Empty;
            try
            {
                Config.Save();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool SetConsoleVisible(bool visible, out string error)
        {
            error = string.Empty;
            _showInputWindow = visible;
            return true;
        }

        public bool GetConsoleVisible()
        {
            return _showInputWindow;
        }

        public bool TryClearMemory(out string error)
        {
            error = string.Empty;
            try
            {
                _hierarchicalMemory?.ClearAllMemory();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryStartTextConversation(string text, string inputSource, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "text is empty";
                return false;
            }

            if (_isProcessing)
            {
                error = "AI is busy";
                return false;
            }

            _currentConversationCoroutine = StartCoroutine(AIProcessRoutine(text, string.IsNullOrWhiteSpace(inputSource) ? "external-text" : inputSource));
            return true;
        }

        public bool TryStartVoiceConversationFromWav(byte[] wavData, string inputSource, out string error)
        {
            error = string.Empty;
            if (wavData == null || wavData.Length == 0)
            {
                error = "wav data is empty";
                return false;
            }

            if (_isProcessing)
            {
                error = "AI is busy";
                return false;
            }

            StartCoroutine(ASRWorkflow(
                wavData,
                string.IsNullOrWhiteSpace(inputSource) ? "external-asr" : inputSource));
            return true;
        }

        public bool TryStartVoiceCapture(out string error)
        {
            error = string.Empty;
            if (_isProcessing)
            {
                error = "AI is busy";
                return false;
            }

            if (_isRecording)
            {
                error = "already recording";
                return false;
            }

            if (Microphone.devices.Length == 0)
            {
                error = "no microphone found";
                return false;
            }

            StartRecording();
            return _isRecording;
        }

        public bool TryStopVoiceCaptureAndSend(string inputSource, out string error)
        {
            error = string.Empty;
            if (!_isRecording)
            {
                error = "not recording";
                return false;
            }

            StopRecordingAndRecognize();
            return true;
        }

        public bool TryStartContinuousCall(out string error)
        {
            error = string.Empty;
            if (_continuousCallActive)
            {
                error = "continuous call already active";
                return false;
            }
            if (Microphone.devices.Length == 0)
            {
                error = "no microphone found";
                return false;
            }

            _enableVoiceCallConfig.Value = true;
            StartContinuousCall();
            return true;
        }

        public bool TryStopContinuousCall(out string error)
        {
            error = string.Empty;
            if (!_continuousCallActive)
            {
                error = "continuous call not active";
                return false;
            }

            _enableVoiceCallConfig.Value = false;
            StopContinuousCall();
            return true;
        }
    }
}

