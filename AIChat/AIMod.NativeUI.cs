using AIChat.Unity;
using AIChat.Utils;
using System;
using UnityEngine;

namespace ChillAIMod
{
    public partial class AIMod
    {
        private NativeChatWindow _nativeWindow;
        private bool _nativeUiFailed;
        private string _nativeStatus = string.Empty;

        private bool NativeConsoleVisible => _nativeWindow != null && _useNativeUi.Value;

        private void UpdateNativeUI()
        {
            if (_nativeWindow == null && !_nativeUiFailed && _useNativeUi.Value &&
                Time.frameCount % 120 == 0 && GameObject.Find("Canvas") != null)
            {
                try
                {
                    _nativeWindow = new NativeChatWindow(this,
                        () => _isRecording,
                        () => _isProcessing ? _nativeStatus : _continuousCallActive ? "持续通话 · 正在监听" : _isRecording ? "正在录音…" : "就绪");
                }
                catch (Exception ex)
                {
                    _nativeUiFailed = true;
                    Log.Error("[NativeUI] 初始化失败，回退旧界面: " + ex.Message);
                }
            }
            _nativeWindow?.Refresh(_showInputWindow && NativeConsoleVisible);
        }

        private void OnDestroy()
        {
            _nativeWindow?.Dispose();
            _nativeWindow = null;
        }
    }
}
