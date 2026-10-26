using System;

namespace AIChat.Core
{
    public static class SubtitleTiming
    {
        public static float Calculate(string text, float audioSeconds = 0f)
        {
            int characters = 0;
            foreach (char c in text ?? string.Empty)
                if (!char.IsWhiteSpace(c) && !char.IsLowSurrogate(c)) characters++;
            float readingSeconds = Math.Max(4f, Math.Min(45f, 2f + characters / 8f));
            if (float.IsNaN(audioSeconds) || float.IsInfinity(audioSeconds) || audioSeconds < 0f) audioSeconds = 0f;
            return Math.Max(readingSeconds, audioSeconds + 0.5f);
        }
    }
}
