using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using AIChat.Core;
using AIChat.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static int Main(string[] args)
    {
        try
        {
            string special = "\"quoted\"\n\tC:\\voice\\test.wav ||| \u4f60\u597d \uD83D\uDE42";
            var response = new JObject {
                ["choices"] = new JArray(new JObject { ["message"] = new JObject {
                    ["reasoning_content"] = "Ignore this field", ["content"] = special
                } })
            };
            Check(ResponseParser.ExtractContentRegex(response.ToString()) == special, "OpenAI escaped quotes, newline, tab, slash and Unicode");
            Check(ResponseParser.ExtractContentRegex("{\"choices\":[]}") == null, "Empty choices");
            Check(ResponseParser.ExtractContentRegex("{\"choices\":42}") == null, "Wrong envelope type");
            Check(ResponseParser.ExtractContentRegex("not JSON") == null, "Invalid JSON");
            Check(ResponseParser.ExtractContentRegex(null) == null, "Null envelope");
            Check(ResponseParser.ExtractContentFromOllama(new JObject { ["message"] = new JObject { ["content"] = special } }.ToString()) == special, "Ollama preserves content");
            Check(ResponseParser.ExtractJsonValue(new JObject { ["text"] = special }.ToString(), "text") == special, "ASR text extraction");
            Check(JsonConvert.DeserializeObject<string>("\"" + ResponseParser.EscapeJson(special + "\b\f\r") + "\"") == special + "\b\f\r", "All JSON control characters escape correctly");

            string structured = new JObject { ["emotion"] = "Happy", ["voice_text"] = special, ["subtitle_text"] = special }.ToString();
            var parsed = LLMUtils.ParseStandardResponse(structured);
            Check(parsed.Success && parsed.VoiceText == special && parsed.SubtitleText == special, "Structured reply preserves delimiter inside strings");
            Check(LLMUtils.ParseStandardResponse("```json\n" + structured + "\n```").Success, "Fenced JSON compatibility");
            Check(!LLMUtils.ParseStandardResponse("{\"emotion\":[],\"voice_text\":\"a\",\"subtitle_text\":\"b\"}").Success, "Invalid JSON field type rejected");
            Check(!LLMUtils.ParseStandardResponse("{\"emotion\":\"Happy\"}").Success, "Missing JSON fields rejected");
            Check(!LLMUtils.ParseStandardResponse(null).Success, "Empty reply rejected");
            Check(LLMUtils.ParseStandardResponse("[Happy] ||| hello ||| world").SubtitleText == "world", "Legacy reply compatibility");
            Check(LLMUtils.ParseStandardResponse("[Happy] | hello | world").Success, "Legacy single separator compatibility");
            Check(LLMUtils.ParseStandardResponse("{\"emotion\":\"Think\",\"voice_text\":\"\",\"subtitle_text\":\"hello\"}").Success, "Text-only structured reply");

            var context = new LLMRequestContext(apiUrl: "https://www.sophnet.com/api/open-apis/v1/chat/completions", modelName: "DeepSeek-V4.1-Flash-1017",
                systemPrompt: "Be a friendly fictional companion. Response format MUST be: [Emotion] ||| JAPANESE TEXT ||| CHINESE TRANSLATION", userPrompt: special,
                thinkMode: ThinkMode.Disable, fixApiPathForThinkMode: true, useStructuredResponse: true);
            var body = JObject.Parse(LLMUtils.BuildRequestBody(context));
            string effectivePrompt = (string)body["messages"][0]["content"];
            Check(effectivePrompt.StartsWith(context.SystemPrompt) && effectivePrompt.IndexOf("replace all earlier output-format rules", StringComparison.Ordinal) > effectivePrompt.IndexOf("|||", StringComparison.Ordinal), "JSON contract follows and overrides the legacy persona format");
            Check(!context.SystemPrompt.Contains("replace all earlier output-format rules"), "Building a JSON request preserves the saved persona");
            Check((string)body["messages"][1]["content"] == special, "Request round trip");
            Check((string)body["response_format"]["type"] == "json_object" && !(bool)body["stream"], "JSON and nonstreaming request");
            Check((string)body["thinking"]["type"] == "disabled" && body["think"] == null, "DeepSeek thinking format");
            Check(LLMUtils.GetApiUrlForThinkMode(context) == context.ApiUrl, "Cloud endpoint is never changed into Ollama endpoint");
            context.UseLocalOllama = true;
            context.ApiUrl = "http://127.0.0.1:11434/v1/chat/completions";
            body = JObject.Parse(LLMUtils.BuildRequestBody(context));
            Check((string)body["format"] == "json" && !(bool)body["think"] && body["response_format"] == null, "Ollama structured request");
            Check(LLMUtils.GetApiUrlForThinkMode(context).EndsWith("/api/chat"), "Ollama native endpoint");
            context.UseLocalOllama = false;
            context.UseStructuredResponse = false;
            context.ThinkMode = ThinkMode.Default;
            body = JObject.Parse(LLMUtils.BuildRequestBody(context));
            Check(body["response_format"] == null && body["thinking"] == null && body["think"] == null, "Legacy request has no extra provider options");
            Check(SubtitleTiming.Calculate("hello") >= 4f, "Minimum reading time");
            Check(SubtitleTiming.Calculate(new string('a', 120)) > SubtitleTiming.Calculate("hello"), "Longer replies stay longer");
            Check(SubtitleTiming.Calculate("hello", 60f) >= 60f, "Long audio is never truncated by reading cap");
            Check(SubtitleTiming.Calculate("hello", float.NaN) == SubtitleTiming.Calculate("hello"), "Invalid audio length has safe fallback");
            var vad = new VoiceActivityDetector(16000, 0.003f, 0.02f, 0.04f);
            int segments = 0;
            vad.SpeechSegmentReady += samples => { if (samples.Length > 0) segments++; };
            vad.Process(new float[640]);
            Check(vad.State == VoiceActivityDetector.VadState.Idle, "VAD ignores silence");
            var speech = new float[320];
            for (int i = 0; i < speech.Length; i++) speech[i] = 0.01f;
            vad.Process(speech);
            Check(vad.State == VoiceActivityDetector.VadState.Speech, "VAD accepts energy above configured threshold");
            vad.Process(new float[640]);
            Check(segments == 1 && vad.State == VoiceActivityDetector.VadState.Idle, "VAD emits one segment after configured silence");
            var adjustedVad = new VoiceActivityDetector(16000, 0.02f, 0.02f, 0.04f);
            adjustedVad.Process(speech);
            Check(adjustedVad.State == VoiceActivityDetector.VadState.Idle, "Rebuilt VAD uses the new threshold");
            vad.Process(speech);
            vad.Reset();
            Check(vad.State == VoiceActivityDetector.VadState.Idle && vad.CurrentEnergy == 0, "VAD reset clears active detection");
            var frame = new WindowBounds(100, 150, 610, 600);
            var resized = WindowResizeGeometry.Resize(frame, 90, 0, ResizeEdges.Right, 1600, 900);
            Check(resized.Left == 100 && resized.Width == 700 && resized.Height == 600, "Right edge changes width only");
            resized = WindowResizeGeometry.Resize(frame, -60, 0, ResizeEdges.Left, 1600, 900);
            Check(resized.Left == 40 && resized.Width == 670 && resized.Left + resized.Width == 710, "Left resize preserves opposite edge");
            resized = WindowResizeGeometry.Resize(frame, 0, 90, ResizeEdges.Top, 1600, 900);
            Check(resized.Bottom == 150 && resized.Height == 690 && resized.Width == 610, "Top edge changes height only");
            resized = WindowResizeGeometry.Resize(frame, 0, 50, ResizeEdges.Bottom, 1600, 900);
            Check(resized.Bottom == 200 && resized.Height == 550 && resized.Bottom + resized.Height == 750, "Bottom resize preserves opposite edge");
            resized = WindowResizeGeometry.Resize(frame, -50, -50, ResizeEdges.Left | ResizeEdges.Bottom, 1600, 900);
            Check(resized.Left == 50 && resized.Bottom == 100 && resized.Width == 660 && resized.Height == 650, "Corner resizes both dimensions");
            resized = WindowResizeGeometry.Resize(frame, -900, -900, ResizeEdges.Right | ResizeEdges.Top, 1600, 900);
            Check(resized.Width == 500 && resized.Height == 500 && resized.Left == 100 && resized.Bottom == 150, "Resize enforces minimum dimensions without flipping");
            resized = WindowResizeGeometry.Resize(frame, 10000, 10000, ResizeEdges.Right | ResizeEdges.Top, 1600, 900);
            Check(resized.Left + resized.Width == 1588 && resized.Bottom + resized.Height == 888, "Resize remains inside top and right screen boundaries");
            resized = WindowResizeGeometry.Resize(frame, -10000, -10000, ResizeEdges.Left | ResizeEdges.Bottom, 1600, 900);
            Check(resized.Left == 12 && resized.Bottom == 12 && resized.Left + resized.Width == 710 && resized.Bottom + resized.Height == 750, "Resize remains inside left and bottom boundaries");
            if (args.Length > 0 && args[0] == "--live-sophnet") LiveSophnet(args.Length > 1 ? args[1] : "DeepSeek-V4.1-Flash-1017");
            Console.WriteLine("Completed " + _checks + " checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void LiveSophnet(string model)
    {
        string key = Environment.GetEnvironmentVariable("SOPHNET_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new Exception("SOPHNET_API_KEY is not set.");
        var context = new LLMRequestContext(apiUrl: "https://www.sophnet.com/api/open-apis/v1/chat/completions", modelName: model,
            systemPrompt: "You are a fictional companion. Speak Japanese and subtitle in Chinese. Use the emotion Greeting. Response format MUST be: [Emotion] ||| JAPANESE TEXT ||| CHINESE TRANSLATION",
            userPrompt: "Say a brief hello and include a quoted word.", thinkMode: ThinkMode.Disable, useStructuredResponse: true);
        var body = JObject.Parse(LLMUtils.BuildRequestBody(context));
        body["max_tokens"] = 256;
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var response = client.PostAsync(context.ApiUrl, new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(response.IsSuccessStatusCode, "SophNet HTTP " + (int)response.StatusCode);
            string raw = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string content = ResponseParser.ExtractContentRegex(raw);
            var structuredReply = JObject.Parse(content);
            Check(structuredReply["emotion"]?.Type == JTokenType.String && structuredReply["voice_text"]?.Type == JTokenType.String && structuredReply["subtitle_text"]?.Type == JTokenType.String, "SophNet returns actual JSON despite the legacy persona format");
            var reply = LLMUtils.ParseStandardResponse(content);
            Check(reply.Success && reply.SubtitleText.Length > 0 && reply.VoiceText.Length > 0, "SophNet production serializer/parser round trip: " + model);
            Console.WriteLine("Reply: " + reply.SubtitleText);
        }
    }
}
