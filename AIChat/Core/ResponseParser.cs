using AIChat.Utils;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIChat.Core
{
    public static class ResponseParser
    {
        public static string EscapeJson(string s)
        {
            string encoded = JsonConvert.ToString(s ?? string.Empty);
            return encoded.Substring(1, encoded.Length - 2);
        }

        public static string ExtractContentFromOllama(string jsonResponse)
        {
            try
            {
                return ReadString(JObject.Parse(jsonResponse)["message"]?["content"]);
            }
            catch (Exception ex)
            {
                Log.Error($"[Ollama] 解析失败: {ex.Message}");
                return null;
            }
        }

        public static string ExtractContentRegex(string json)
        {
            // Keep the public method name for existing integrations.
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var choices = JObject.Parse(json)["choices"] as JArray;
                var first = choices?.First as JObject;
                var message = first?["message"] as JObject;
                return ReadString(message?["content"]);
            }
            catch (JsonException) { return null; }
        }

        private static string ReadString(JToken token)
        {
            return token?.Type == JTokenType.String ? (string)token : null;
        }

        public static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(key)) return string.Empty;
            try { return ReadString(JObject.Parse(json)[key]) ?? string.Empty; }
            catch (JsonException) { return string.Empty; }
        }
        // =========================================================================================
        // 【新增辅助函数】确保对话文本（字幕）强制换行，以防过长溢出屏幕。
        // =========================================================================================
        /// <summary>
        /// 在长文本中插入换行符，以确保文本在 UI 中可见。
        /// </summary>
        /// <param name="text">原始文本</param>
        /// <param name="maxLineLength">每行最大字符数</param>
        /// <returns>带有换行符的文本</returns>
        public static string InsertLineBreaks(string text, int maxLineLength = 25)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLineLength)
            {
                return text;
            }

            StringBuilder sb = new StringBuilder();
            int currentLength = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                sb.Append(c);
                currentLength++;

                if (currentLength >= maxLineLength && c != '\n')
                {
                    // 检查下一个字符是否已经是换行符，避免双重换行
                    if (i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        sb.Append('\n');
                        currentLength = 0;
                    }
                }

                if (c == '\n')
                {
                    currentLength = 0;
                }
            }
            return sb.ToString();
        }
    }
}

