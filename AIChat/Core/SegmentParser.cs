using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AIChat.Utils;
using UnityEngine;

namespace AIChat.Core
{
    /// <summary>
    /// 一段很短的对白：一句日语配音 + 一句中文字幕。
    /// </summary>
    public sealed class ChatSegment
    {
        public string EmotionTag = "Think";
        public string VoiceText = "";
        public string SubtitleText = "";
    }

    /// <summary>
    /// 句段在运行时的状态：音频是否已经合成完毕 / 是否失败。
    /// </summary>
    public sealed class SegmentRuntime
    {
        public ChatSegment Segment;
        public AudioClip Clip;
        public bool Done;
        public bool Failed;
    }

    /// <summary>
    /// 把 LLM 的整段回复切成有序的短句。
    /// 约定的回复格式是「每行一句」：
    ///   [Emotion] ||| 日本語 ||| 中文
    /// 同时兼容旧的一行整段格式（单句），以及模型偶尔把多句挤在一行的情况。
    /// </summary>
    public static class SegmentParser
    {
        public const int MaxSegments = 12;

        public static List<ChatSegment> Parse(string response)
        {
            bool ignored;
            return Parse(response, out ignored);
        }

        public static List<ChatSegment> Parse(string response, out bool formatOk)
        {
            formatOk = false;
            var list = new List<ChatSegment>();
            if (string.IsNullOrWhiteSpace(response)) return list;

            string text = response.Replace("\r", "").Replace("```", "");

            var anchors = Regex.Matches(text, @"\[([A-Za-z_][A-Za-z0-9_]*)\]");
            if (anchors.Count == 0)
            {
                // 没有任何情绪标签：整段当一句，尽量沿用原有兜底解析
                var single = LLMUtils.ParseStandardResponse(text);
                string voice = (single.VoiceText ?? "").Trim();
                string sub = (single.SubtitleText ?? "").Trim();
                if (voice.Length == 0 && sub.Length == 0) return list;

                formatOk = single.Success;
                AddWithSentenceSplit(list, single.EmotionTag, voice, sub);
                NormalizeSegments(list);
                return list;
            }

            formatOk = true;
            for (int i = 0; i < anchors.Count && list.Count < MaxSegments; i++)
            {
                int start = anchors[i].Index + anchors[i].Length;
                int end = (i + 1 < anchors.Count) ? anchors[i + 1].Index : text.Length;
                string chunk = text.Substring(start, end - start);
                string tag = anchors[i].Groups[1].Value;

                string voice, subtitle;
                SplitChunk(chunk, out voice, out subtitle);
                if (voice.Length == 0 && subtitle.Length == 0) continue;

                AddWithSentenceSplit(list, tag, voice, subtitle);
            }

            NormalizeSegments(list);
            return list;
        }

        // ── 口癖（にゃ / 喵）归一化 ──────────────────────────────────

        // 「只剩口癖」的碎片，例如单独一行的「にゃ」「喵」「meow。」
        private static readonly Regex BareTic = new Regex(
            @"^\s*(?:にゃ|ニャ|みゃ|にゃん|ニャン|喵|meow|nya+n?)[。！？，、.!?,\s]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MeowWord = new Regex(@"(?i)\bmeow\b|\bnya+n?\b", RegexOptions.Compiled);

        private static readonly Regex TrailingPunct = new Regex(@"[。．！？，、,.!?;；:：\s]+$", RegexOptions.Compiled);

        /// <summary>
        /// 1) 把 "meow" 之类写法统一成 にゃ（日语）/ 喵（中文）；
        /// 2) 把只剩口癖的碎片并回上一句，避免出现「、にゃ」单独成句。
        /// </summary>
        private static void NormalizeSegments(List<ChatSegment> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                list[i].VoiceText = MeowWord.Replace(list[i].VoiceText, "にゃ");
                list[i].SubtitleText = MeowWord.Replace(list[i].SubtitleText, "喵");
            }

            for (int i = 0; i < list.Count && list.Count > 1; i++)
            {
                var seg = list[i];
                bool voiceBare = BareTic.IsMatch(seg.VoiceText);
                bool subBare = string.IsNullOrWhiteSpace(seg.SubtitleText) || BareTic.IsMatch(seg.SubtitleText);
                if (!voiceBare && !subBare) continue;

                if (i > 0)
                {
                    AttachTic(list[i - 1], seg);
                    list.RemoveAt(i);
                    i--;
                }
                else
                {
                    // 第一句就是口癖碎片：并到下一句开头
                    var next = list[1];
                    if (voiceBare) next.VoiceText = seg.VoiceText.Trim() + next.VoiceText;
                    if (subBare) next.SubtitleText = seg.SubtitleText.Trim() + next.SubtitleText;
                    list.RemoveAt(0);
                    i--;
                }
            }
        }

        private static readonly Regex EndsWithJapaneseTic = new Regex(@"(?:にゃ|ニャ|みゃ|にゃん)[。！？、，\s]*$", RegexOptions.Compiled);
        private static readonly Regex EndsWithChineseTic = new Regex(@"(?:喵)[。！？、，\s]*$", RegexOptions.Compiled);

        private static void AttachTic(ChatSegment prev, ChatSegment tic)
        {
            string voiceTic = TrailingPunct.Replace(tic.VoiceText ?? "", "").Trim();
            if (voiceTic.Length == 0) voiceTic = "にゃ";
            if (!EndsWithJapaneseTic.IsMatch(prev.VoiceText ?? ""))
            {
                prev.VoiceText = TrailingPunct.Replace(prev.VoiceText ?? "", "").TrimEnd('、', '，', ' ') + voiceTic;
            }

            string subTic = TrailingPunct.Replace(tic.SubtitleText ?? "", "").Trim();
            if (subTic.Length == 0) subTic = "喵";
            if (!EndsWithChineseTic.IsMatch(prev.SubtitleText ?? ""))
            {
                prev.SubtitleText = TrailingPunct.Replace(prev.SubtitleText ?? "", "").TrimEnd('、', '，', ' ') + subTic;
            }
        }

        /// <summary>
        /// 一段 chunk 里可能是 "||| 日语 ||| 中文"，也可能是 "日语 ||| 中文"。
        /// </summary>
        private static void SplitChunk(string chunk, out string voice, out string subtitle)
        {
            voice = "";
            subtitle = "";

            var pieces = new List<string>();
            foreach (var p in chunk.Split(new[] { "|||" }, StringSplitOptions.None))
            {
                string t = Normalize(p);
                if (t.Length > 0) pieces.Add(t);
            }

            if (pieces.Count == 0)
            {
                // 没有 |||：试着用单个 | 分开
                foreach (var p in chunk.Split('|'))
                {
                    string t = Normalize(p);
                    if (t.Length > 0) pieces.Add(t);
                }
            }

            if (pieces.Count == 0) return;
            voice = pieces[0];
            subtitle = pieces.Count > 1 ? pieces[1] : pieces[0];
        }

        /// <summary>
        /// 模型有时会忽略「一句一行」，把两句挤在同一行。这里按标点再切一次。
        /// 只有当日语和中文切出来的段数一致时才采用，避免中配错位。
        /// </summary>
        private static void AddWithSentenceSplit(List<ChatSegment> list, string tag, string voice, string subtitle)
        {
            if (voice.Length == 0 && subtitle.Length == 0) return;

            var voiceParts = SplitByPunctuation(voice);
            var subParts = SplitByPunctuation(subtitle);

            if (voiceParts.Count > 1 && voiceParts.Count == subParts.Count)
            {
                for (int i = 0; i < voiceParts.Count && list.Count < MaxSegments; i++)
                {
                    list.Add(new ChatSegment
                    {
                        EmotionTag = tag,
                        VoiceText = voiceParts[i],
                        SubtitleText = subParts[i],
                    });
                }
                return;
            }

            list.Add(new ChatSegment
            {
                EmotionTag = tag,
                VoiceText = voice,
                SubtitleText = subtitle,
            });
        }

        private static List<string> SplitByPunctuation(string text)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return parts;

            var buffer = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                buffer.Append(c);
                if (IsBreak(c))
                {
                    string piece = Normalize(buffer.ToString());
                    if (piece.Length > 0) parts.Add(piece);
                    buffer.Length = 0;
                }
            }

            string tail = Normalize(buffer.ToString());
            if (tail.Length > 0) parts.Add(tail);
            return parts;
        }

        /// <summary>
        /// 只在句末标点处兜底断句。
        /// 逗号不断：模型偶尔在列举（「はい、奈奇、千鵺」）里用逗号，
        /// 按逗号切会得到「はい、」「奈奇、」这种碎片，反而更糟；
        /// 正常情况本来就应该一句一行，用不到逗号兜底。
        /// </summary>
        private static bool IsBreak(char c)
        {
            switch (c)
            {
                case '。':
                case '！':
                case '？':
                case '；':
                case '.':
                case '!':
                case '?':
                case ';':
                    return true;
                default:
                    return false;
            }
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // 折叠所有空白（含换行），去掉模型偶尔加的行首符号
            string t = Regex.Replace(s, @"\s+", " ");
            t = t.Trim().Trim('|').Trim();
            t = Regex.Replace(t, @"^[-*•\u2022]+\s*", "");
            return t.Trim();
        }
    }
}
