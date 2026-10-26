using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using ChillAIMod;
using AIChat.Core;

namespace AIChat.Utils
{
    public enum ThinkMode { Default, Enable, Disable }

    public struct LLMRequestContext
    {
        public string ApiUrl;
        public string ApiKey;
        public string ModelName;
        public string SystemPrompt;
        public string UserPrompt;
        public bool UseLocalOllama;
        public bool LogApiRequestBody;
        public bool FixApiPathForThinkMode;
        public bool UseStructuredResponse;
        public ThinkMode ThinkMode;
        public HierarchicalMemory HierarchicalMemory;
        public string LogHeader;

        public LLMRequestContext(
            string apiUrl = "",
            string apiKey = "",
            string modelName = "",
            string systemPrompt = "",
            string userPrompt = "",
            bool useLocalOllama = false,
            bool logApiRequestBody = false,
            ThinkMode thinkMode = ThinkMode.Default,
            HierarchicalMemory hierarchicalMemory = null,
            string logHeader = "LLMRequest",
            bool fixApiPathForThinkMode = false,
            bool useStructuredResponse = false
        )
        {
            ApiUrl = apiUrl;
            ApiKey = apiKey;
            ModelName = modelName;
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            UseLocalOllama = useLocalOllama;
            LogApiRequestBody = logApiRequestBody;
            ThinkMode = thinkMode;
            HierarchicalMemory = hierarchicalMemory;
            LogHeader = logHeader;
            FixApiPathForThinkMode = fixApiPathForThinkMode;
            UseStructuredResponse = useStructuredResponse;
        }
    }

    public struct LLMStandardResponse
    {
        public bool Success;
        public string EmotionTag;  // 动作标签，如 [Happy], [Think] 等
        public string VoiceText;   // 用于 TTS 的文本
        public string SubtitleText;// 用于字幕显示的文本

        public LLMStandardResponse(bool success, string emotionTag, string voiceText, string subtitleText)
        {
            Success = success;
            EmotionTag = emotionTag;
            VoiceText = voiceText;
            SubtitleText = subtitleText;
        }
    }

    public static class LLMUtils
    {
        public static LLMStandardResponse ParseStandardResponse(string response)
        {
            LLMStandardResponse ret = new LLMStandardResponse(false, "Think", "", response);
            if (string.IsNullOrWhiteSpace(response)) return ret;
            string json = response.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal) && json.EndsWith("```", StringComparison.Ordinal))
            {
                int firstLine = json.IndexOf('\n');
                if (firstLine >= 0) json = json.Substring(firstLine + 1, json.Length - firstLine - 4).Trim();
            }
            if (json.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var reply = JObject.Parse(json);
                    if (reply["emotion"]?.Type != JTokenType.String ||
                        reply["voice_text"]?.Type != JTokenType.String ||
                        reply["subtitle_text"]?.Type != JTokenType.String) return ret;
                    string emotion = ((string)reply["emotion"]).Trim().Trim('[', ']');
                    string voice = ((string)reply["voice_text"]).Trim();
                    string subtitle = ((string)reply["subtitle_text"]).Trim();
                    if (!Regex.IsMatch(emotion, @"^[A-Za-z][A-Za-z0-9_]*$") || string.IsNullOrEmpty(subtitle)) return ret;
                    return new LLMStandardResponse(true, emotion, voice, subtitle);
                }
                catch (JsonException) { return ret; }
            }
            // 按 ||| 分割（注意：有些模型可能会用单个 | ）
            string[] parts = response.Split(new string[] { "|||" }, StringSplitOptions.None);

            // 如果不是 |||，尝试单个 |
            if (parts.Length < 3)
            {
                parts = response.Split(new string[] { "|" }, StringSplitOptions.None);
            }

            // 【核心修改：严格的格式检查 + 正则容错】
            if (parts.Length >= 3)
            {
                // 用正则提取标签名，容错处理多余括号等情况
                var tagMatch = Regex.Match(parts[0], @"\[(\w+)\]");
                if (tagMatch.Success)
                {
                    ret.EmotionTag = tagMatch.Groups[1].Value;
                }
                else
                {
                    // fallback：去掉所有非字母字符
                    ret.EmotionTag = Regex.Replace(parts[0].Trim(), @"[^\w]", "");
                    if (string.IsNullOrEmpty(ret.EmotionTag)) ret.EmotionTag = "Idle";
                }
                ret.VoiceText = parts[1].Trim();
                ret.SubtitleText = parts[2].Trim();

                ret.Success = true;
            }

            // 兜底：格式不完整时尝试智能解析
            if (!ret.Success)
            {
                // 情况1: [Tag] 日文 ||| 中文（只有一个 |||）
                var twoPartMatch = Regex.Match(response, @"\[(\w+)\]\s*(.+?)\s*\|\|\|\s*(.+)");
                if (twoPartMatch.Success)
                {
                    ret.EmotionTag = twoPartMatch.Groups[1].Value;
                    ret.VoiceText = twoPartMatch.Groups[2].Value.Trim();
                    ret.SubtitleText = twoPartMatch.Groups[3].Value.Trim();
                    ret.Success = true;
                    Log.Warning($"[格式兜底-两段] [{ret.EmotionTag}] voice={ret.VoiceText} sub={ret.SubtitleText}");
                }
                // 情况2: [Tag] 纯内容（没有 |||）
                else
                {
                    var fallbackMatch = Regex.Match(response, @"\[(\w+)\]\s*(.+)");
                    if (fallbackMatch.Success)
                    {
                        ret.EmotionTag = fallbackMatch.Groups[1].Value;
                        ret.VoiceText = fallbackMatch.Groups[2].Value.Trim();
                        ret.SubtitleText = ret.VoiceText;
                        ret.Success = true;
                        Log.Warning($"[格式兜底-单段] [{ret.EmotionTag}] {ret.VoiceText}");
                    }
                    else
                    {
                        Log.Warning($"[格式错误] AI 回复不符合格式: {response}");
                    }
                }
            }

            return ret;
        }

        public static string BuildRequestBody(LLMRequestContext requestContext)
        {
            // 【集成分层记忆】获取带记忆上下文的提示词
            string userPromptWithMemory = GetContextWithMemory(requestContext.HierarchicalMemory, requestContext.UserPrompt);

            string systemPrompt = requestContext.SystemPrompt ?? string.Empty;
            if (requestContext.UseStructuredResponse)
                systemPrompt += "\nFor this reply, replace all earlier output-format rules with this JSON contract. " +
                    "Return exactly one JSON object with three string fields: emotion (tag name without brackets), " +
                    "voice_text (spoken reply, keeping the requested spoken language), subtitle_text (translated subtitle, " +
                    "keeping the requested subtitle language). No markdown fences or surrounding text. " +
                    "Example: {\"emotion\":\"Greeting\",\"voice_text\":\"こんにちは！\",\"subtitle_text\":\"你好！\"}";

            var messages = new JArray();
            if ((requestContext.ModelName ?? string.Empty).IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0)
                messages.Add(new JObject { ["role"] = "user", ["content"] = $"[System Instruction]\n{systemPrompt}\n\n[User Message]\n{userPromptWithMemory}" });
            else
            {
                messages.Add(new JObject { ["role"] = "system", ["content"] = systemPrompt });
                messages.Add(new JObject { ["role"] = "user", ["content"] = userPromptWithMemory ?? string.Empty });
            }
            var body = new JObject { ["model"] = requestContext.ModelName, ["messages"] = messages, ["stream"] = false };
            if (requestContext.UseStructuredResponse)
            {
                if (requestContext.UseLocalOllama) body["format"] = "json";
                else body["response_format"] = new JObject { ["type"] = "json_object" };
            }
            if (requestContext.ThinkMode != ThinkMode.Default)
            {
                if (requestContext.UseLocalOllama) body["think"] = requestContext.ThinkMode == ThinkMode.Enable;
                else if ((requestContext.ModelName ?? string.Empty).IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0)
                    body["thinking"] = new JObject { ["type"] = requestContext.ThinkMode == ThinkMode.Enable ? "enabled" : "disabled" };
            }
            string jsonBody = body.ToString(Formatting.None);

            Log.Info($"[记忆系统] 启用状态: {requestContext.HierarchicalMemory != null}");
            // 【日志】打印完整的请求体（如果启用）
            if (requestContext.LogApiRequestBody)
            {
                // 【调试日志】显示完整的请求内容
                Log.Info($"[发送给LLM的完整内容]\n========================================\n[System Prompt]\n{requestContext.SystemPrompt}\n\n[User Content]\n{userPromptWithMemory}\n========================================");
                Log.Info($"[API请求] 完整请求体:\n{jsonBody}");
            }

            return jsonBody;
        }

        private static string GetContextWithMemory(HierarchicalMemory hierarchicalMemory, string currentPrompt)
        {
            if (hierarchicalMemory != null)
            {
                string memoryContext = hierarchicalMemory.GetContext();
                Log.Info($"[记忆系统] 当前记忆状态:\n{hierarchicalMemory.GetMemoryStats()}");

                // 如果有记忆内容，则拼接；否则只返回当前提示
                if (!string.IsNullOrWhiteSpace(memoryContext))
                {
                    return $"{memoryContext}\n\n【Current Input】\n{currentPrompt}";
                }
            }
            
            // 无记忆或未启用，直接返回原始 prompt
            return currentPrompt;
        }

        /// <summary>
        /// 获取适合当前think模式的API URL
        /// </summary>
        public static string GetApiUrlForThinkMode(LLMRequestContext requestContext)
        {
            string baseUrl = requestContext.ApiUrl;
            // The Ollama response parser expects its native /api/chat envelope.
            if (requestContext.UseLocalOllama && requestContext.FixApiPathForThinkMode)
            {
                // 将 /v1/chat/completions 替换为 /api/chat
                if (baseUrl.Contains("/v1/chat/completions"))
                {
                    baseUrl = baseUrl.Replace("/v1/chat/completions", "/api/chat");
                    Log.Info($"[Think Mode] 切换到 Ollama 原生 API: {baseUrl}");
                }
                // 如果URL已经是 /api/chat 或其他格式，保持不变
            }
            
            return baseUrl;
        }
    }
}
