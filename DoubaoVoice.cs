using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>豆包语音合成（火山引擎 V3 HTTP Chunked 单向流式接口），在后台线程依次合成并播放。</summary>
    public sealed class DoubaoVoice : IDisposable
    {
        public const string Endpoint = "https://openspeech.bytedance.com/api/v3/tts/unidirectional";
        public const string CreateEndpoint = "https://openspeech.bytedance.com/api/v3/tts/create";
        public const string DefaultResource = "seed-audio-1.0";
        public const string DefaultSpeaker = "zh_female_vv_uranus_bigtts";
        public const string DefaultVoicePrompt = "年轻女声，普通话，语气轻快清晰";
        /// <summary>英文默认音色 ID（seed-tts-*）。/ Default English voice ID (seed-tts-*).</summary>
        public const string DefaultSpeakerEn = "en_female_anna_mars_bigtts";
        /// <summary>英文默认声音描述（seed-audio-*）。/ Default English voice description (seed-audio-*).</summary>
        public const string DefaultVoicePromptEn = "A young female voice speaking clear American English in a light, cheerful tone";
        /// <summary>音色不可用错误的消息前缀（用于触发降级）。/ Message prefix of "voice unavailable" errors (triggers the fallback).</summary>
        public const string VoiceUnavailablePrefix = "音色不可用";
        public static readonly string[] Resources = { "seed-audio-1.0", "seed-tts-2.0", "seed-tts-1.0", "seed-tts-1.0-concurr" };
        private const int SampleRate = 24000;
        private const int MaxQueue = 3;

        /// <summary>seed-audio 系列走 /tts/create：用自然语言描述声音，而不是音色 ID。</summary>
        public static bool IsPromptModel(string resource) =>
            (resource ?? DefaultResource).Trim().StartsWith("seed-audio", StringComparison.OrdinalIgnoreCase);

        /// <summary>按模型类型与语言返回默认音色。/ Default voice for the model type and language.</summary>
        public static string DefaultVoiceFor(string resource, bool english = false) =>
            IsPromptModel(resource) ? (english ? DefaultVoicePromptEn : DefaultVoicePrompt) : (english ? DefaultSpeakerEn : DefaultSpeaker);

        /// <summary>
        /// 音色不可用时的降级音色：英文音色 ID → 默认（多语种）音色 zh_female_vv_uranus_bigtts；英文声音描述 → 内置英文描述；
        /// 中文 → 内置中文默认。与当前音色相同时返回 null（不再重试）。
        /// Fallback voice when a voice is unavailable: English voice ID → default (multilingual) voice; English description → built-in
        /// English description; Chinese → built-in Chinese default. Returns null when it equals the current voice (no retry).
        /// </summary>
        public static string FallbackVoiceFor(string resource, string speaker, bool english)
        {
            string fb = IsPromptModel(resource) ? DefaultVoiceFor(resource, english) : DefaultSpeaker;
            return string.Equals((speaker ?? "").Trim(), fb, StringComparison.OrdinalIgnoreCase) ? null : fb;
        }

        private readonly Func<AppSettings> _settings;
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Thread _worker;
        private readonly object _playLock = new object();
        private CancellationTokenSource _itemCts;
        private SoundPlayer _player;
        private ManualResetEvent _playStop;

        /// <summary>播报失败时触发（在后台线程）。</summary>
        public event Action<string> Failed;
        /// <summary>提示信息（如音色已降级），在后台线程触发。/ Informational notice (e.g. voice fell back), raised on the worker thread.</summary>
        public event Action<string> Notice;

        public DoubaoVoice(Func<AppSettings> settings)
        {
            _settings = settings;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _worker = new Thread(Run) { IsBackground = true, Name = "DoubaoVoice" };
            _worker.Start();
        }

        /// <summary>排队播报；队列已满时丢弃，避免积压。</summary>
        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _queue.IsAddingCompleted || _queue.Count >= MaxQueue) return;
            try { _queue.Add(text.Trim()); } catch (InvalidOperationException) { }
        }

        /// <summary>立即合成并播放一段文本，返回 null 表示成功，否则为错误信息。</summary>
        public Task<string> TestAsync(string apiKey, string resource, string speaker, string text) =>
            Task.Run(() => TestAsync(apiKey, resource, speaker, text, false).Result.Error);

        /// <summary>
        /// 按语言试听（含音色降级）：Error 为 null 表示成功，Notice 为降级提示。
        /// Preview in the given language (with voice fallback): Error = null on success; Notice describes any fallback.
        /// </summary>
        public Task<VoiceResult> TestAsync(string apiKey, string resource, string speaker, string text, bool english) =>
            Task.Run(() =>
            {
                var r = new VoiceResult();
                try { Play(SynthesizeWithFallback(apiKey, resource, speaker, text, english, _cts.Token, out r.Notice)); }
                catch (Exception ex) { r.Error = ex.Message; }
                return r;
            });

        /// <summary>清空待播队列，并中止正在合成 / 播放的语音。</summary>
        public void StopAll()
        {
            while (_queue.TryTake(out _)) { }
            lock (_playLock)
            {
                try { _itemCts?.Cancel(); } catch (ObjectDisposedException) { }
                try { _player?.Stop(); } catch { }
                try { _playStop?.Set(); } catch (ObjectDisposedException) { }
            }
        }

        private void Run()
        {
            try
            {
                foreach (var text in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    var s = _settings();
                    if (!s.VoiceEnabled) continue;
                    var item = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    lock (_playLock) _itemCts = item;
                    try
                    {
                        // 每条播报都重新读取语言与音色，切换后立即生效 / Read language and voice per item so changes apply immediately
                        bool en = s.IsEnglishVoice;
                        var wav = SynthesizeWithFallback(s.EffectiveVoiceApiKey, s.VoiceResource, s.SpeakerFor(en), text, en, item.Token, out string notice);
                        if (notice != null) Notice?.Invoke(notice);
                        if (!item.IsCancellationRequested && _settings().VoiceEnabled) Play(wav, item.Token);
                    }
                    catch (OperationCanceledException) { if (_cts.IsCancellationRequested) return; }
                    catch (Exception) when (item.IsCancellationRequested) { if (_cts.IsCancellationRequested) return; }
                    catch (Exception ex) { Failed?.Invoke(ex.Message); }
                    finally
                    {
                        lock (_playLock) _itemCts = null;
                        item.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>调用接口，返回可直接播放的 WAV。</summary>
        public static byte[] Synthesize(string apiKey, string resource, string speaker, string text, CancellationToken ct) =>
            Synthesize(apiKey, resource, speaker, text, false, ct);

        /// <summary>按语言合成（英文使用英文提示模板与语种参数）。/ Synthesize for a language (English uses the English prompt template and language parameter).</summary>
        public static byte[] Synthesize(string apiKey, string resource, string speaker, string text, bool english, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("未填写豆包语音 API Key");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("播报内容为空");
            resource = string.IsNullOrWhiteSpace(resource) ? DefaultResource : resource.Trim();
            return IsPromptModel(resource)
                ? CreateWav(apiKey, resource, speaker, text, english, ct)
                : ToWav(StreamPcm(apiKey, resource, speaker, text, english, ct));
        }

        /// <summary>
        /// 合成；若所选音色不可用，回退到默认音色重试一次并通过 notice 给出提示（不抛出音色错误）。
        /// Synthesize; if the selected voice is unavailable, retry once with the default voice and report it via notice.
        /// </summary>
        public static byte[] SynthesizeWithFallback(string apiKey, string resource, string speaker, string text, bool english,
            CancellationToken ct, out string notice)
        {
            return SynthesizeWithFallback((spk) => Synthesize(apiKey, resource, spk, text, english, ct), resource, speaker, english, out notice);
        }

        /// <summary>降级逻辑（可注入合成函数，便于测试）。/ Fallback logic (synthesis function injectable for tests).</summary>
        public static byte[] SynthesizeWithFallback(Func<string, byte[]> synth, string resource, string speaker, bool english, out string notice)
        {
            notice = null;
            if (string.IsNullOrWhiteSpace(speaker)) speaker = DefaultVoiceFor(resource, english);
            try { return synth(speaker); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith(VoiceUnavailablePrefix, StringComparison.Ordinal))
            {
                string fb = FallbackVoiceFor(resource, speaker, english);
                if (fb == null) throw;
                var wav = synth(fb);
                notice = (english ? "英文" : "") + "音色「" + speaker.Trim() + "」不可用，已回退到默认音色「" + fb + "」 / " +
                         (english ? "English voice" : "Voice") + " unavailable, fell back to the default voice";
                return wav;
            }
        }

        private static byte[] CreateWav(string apiKey, string model, string voice, string text, bool english, CancellationToken ct)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string desc = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceFor(model, english) : voice;
            string prompt = Prompts.SpeechPrompt(desc, text, english);
            string body = json.Serialize(new
            {
                model,
                text_prompt = prompt,
                audio_config = new { format = "wav", sample_rate = SampleRate, pitch_rate = 0, speech_rate = 0, loudness_rate = 0 },
                watermark = new { }
            });
            string raw = Post(CreateEndpoint, apiKey, null, body, 90000, json, ct);
            var d = json.DeserializeObject(raw) as Dictionary<string, object>;
            if (d == null) throw new InvalidOperationException("接口返回格式无法识别");
            int code = GetCode(d);
            if (code != 0 && code != 20000000) throw new InvalidOperationException(ErrorText(d, code));
            if (!(d.TryGetValue("audio", out var a) && a is string b64 && b64.Length > 0)) throw new InvalidOperationException("接口未返回音频");
            var wav = Convert.FromBase64String(b64);
            return wav.Length > 12 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" ? wav : ToWav(wav);
        }

        /// <summary>豆包机器翻译（与语音共用 API Key，自动识别源语言）。</summary>
        public static string Translate(string apiKey, string text, string target, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("未填写豆包语音 API Key");
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string body = json.Serialize(new Dictionary<string, object> { ["target_language"] = target, ["text_list"] = new[] { text } });
            string raw = Post("https://openspeech.bytedance.com/api/v3/machine_translation/matx_translate", apiKey, "volc.speech.mt", body, 10000, json, ct);
            var root = json.DeserializeObject(raw) as Dictionary<string, object>;
            if (root != null && root.TryGetValue("data", out var d) && d is Dictionary<string, object> data &&
                data.TryGetValue("translation_list", out var l) && l is object[] list && list.Length > 0 &&
                list[0] is Dictionary<string, object> first && first.TryGetValue("translation", out var tr) && tr is string s)
                return s;
            throw new InvalidOperationException(DescribeError(json, raw, "翻译失败"));
        }

        private static string Post(string url, string apiKey, string resource, string body, int timeoutMs, JavaScriptSerializer json, CancellationToken ct)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Headers["X-Api-Key"] = apiKey.Trim();
            if (resource != null) req.Headers["X-Api-Resource-Id"] = resource;
            req.Headers["X-Api-Request-Id"] = Guid.NewGuid().ToString();
            var bytes = Encoding.UTF8.GetBytes(body);
            using (ct.Register(req.Abort))
            {
                try
                {
                    using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string raw = sr.ReadToEnd();
                        ct.ThrowIfCancellationRequested();
                        return raw;
                    }
                }
                catch (WebException ex) when (ex.Response != null)
                {
                    string raw;
                    using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) raw = sr.ReadToEnd();
                    throw new InvalidOperationException(DescribeError(json, raw, ((HttpWebResponse)ex.Response).StatusCode.ToString()));
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
                {
                    throw new InvalidOperationException("请求超时");
                }
            }
        }

        /// <summary>V3 单向流式接口，返回 16 位单声道 PCM。</summary>
        private static byte[] StreamPcm(string apiKey, string resource, string speaker, string text, bool english, CancellationToken ct)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var req = new Dictionary<string, object>
            {
                ["text"] = text,
                ["speaker"] = string.IsNullOrWhiteSpace(speaker) ? DefaultVoiceFor(resource, english) : speaker.Trim(),
                ["audio_params"] = new Dictionary<string, object> { ["format"] = "pcm", ["sample_rate"] = SampleRate }
            };
            // 英文显式指定语种；中文保持原有请求不变 / English explicitly sets the language; Chinese keeps the original request
            if (english) req["additions"] = json.Serialize(new Dictionary<string, object> { ["explicit_language"] = "en" });
            string body = json.Serialize(new Dictionary<string, object>
            {
                ["user"] = new Dictionary<string, object> { ["uid"] = "vsmanager" },
                ["req_params"] = req
            });
            return ParseAudio(json, Post(Endpoint, apiKey, resource, body, 20000, json, ct));
        }

        private static byte[] ParseAudio(JavaScriptSerializer json, string raw)
        {
            var pcm = new MemoryStream();
            foreach (var obj in SplitObjects(raw))
            {
                if (!(json.DeserializeObject(obj) is Dictionary<string, object> d)) continue;
                int code = GetCode(d);
                if (code != 0 && code != 20000000) throw new InvalidOperationException(ErrorText(d, code));
                if (d.TryGetValue("data", out var data) && data is string b64 && b64.Length > 0)
                {
                    var chunk = Convert.FromBase64String(b64);
                    pcm.Write(chunk, 0, chunk.Length);
                }
            }
            if (pcm.Length == 0) throw new InvalidOperationException("接口未返回音频");
            return pcm.ToArray();
        }

        private static int GetCode(Dictionary<string, object> d)
        {
            object c = null;
            if (!d.TryGetValue("code", out c) && d.TryGetValue("header", out var h) && h is Dictionary<string, object> hd) hd.TryGetValue("code", out c);
            try { return c == null ? 0 : Convert.ToInt32(c); } catch { return -1; }
        }

        private static string ErrorText(Dictionary<string, object> d, int code)
        {
            object m = null;
            if (!d.TryGetValue("message", out m) && d.TryGetValue("header", out var h) && h is Dictionary<string, object> hd) hd.TryGetValue("message", out m);
            string msg = m as string ?? "";
            if (code == 45000030 || msg.Contains("not granted"))
                return "该 API Key 未开通所选模型 / 资源（" + code + "）。请在火山引擎控制台开通，或换用已开通的模型（如 seed-audio-1.0）";
            if (code == 45000010 || msg.IndexOf("speaker", StringComparison.OrdinalIgnoreCase) >= 0)
                return "音色不可用（" + code + "）：" + msg;
            return "豆包语音错误 " + code + "：" + msg;
        }

        private static string DescribeError(JavaScriptSerializer json, string raw, string status)
        {
            foreach (var obj in SplitObjects(raw))
                try
                {
                    if (json.DeserializeObject(obj) is Dictionary<string, object> d) return ErrorText(d, GetCode(d));
                }
                catch { }
            return "HTTP " + status + "：" + (raw.Length > 200 ? raw.Substring(0, 200) : raw);
        }

        /// <summary>响应是若干个依次拼接的 JSON 对象（通常以换行分隔）。</summary>
        private static IEnumerable<string> SplitObjects(string raw)
        {
            int depth = 0, start = -1;
            bool str = false, esc = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (str)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') str = false;
                    continue;
                }
                if (c == '"') str = true;
                else if (c == '{') { if (depth++ == 0) start = i; }
                else if (c == '}' && depth > 0 && --depth == 0) yield return raw.Substring(start, i - start + 1);
            }
        }

        private static byte[] ToWav(byte[] pcm)
        {
            using (var ms = new MemoryStream(44 + pcm.Length))
            {
                var w = new BinaryWriter(ms);
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + pcm.Length);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(SampleRate);
                w.Write(SampleRate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(pcm.Length);
                w.Write(pcm);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>异步播放并等待时长结束；PlaySync 无法从其他线程中止，故用 Play + 等待，取消时 Stop。</summary>
        private void Play(byte[] wav, CancellationToken ct = default(CancellationToken))
        {
            using (var ms = new MemoryStream(wav))
            using (var player = new SoundPlayer(ms))
            using (var stop = new ManualResetEvent(false))
            {
                player.Load();
                lock (_playLock)
                {
                    if (ct.IsCancellationRequested) return;
                    _player = player;
                    _playStop = stop;
                }
                try
                {
                    player.Play();
                    if (WaitHandle.WaitAny(new[] { ct.WaitHandle, stop }, WavDuration(wav) + TimeSpan.FromMilliseconds(150)) != WaitHandle.WaitTimeout) player.Stop();
                }
                finally { lock (_playLock) { _player = null; _playStop = null; } }
            }
        }

        private static TimeSpan WavDuration(byte[] wav)
        {
            try
            {
                int byteRate = BitConverter.ToInt32(wav, 28);
                for (int i = 12; i + 8 <= wav.Length;)
                {
                    int size = BitConverter.ToInt32(wav, i + 4);
                    if (Encoding.ASCII.GetString(wav, i, 4) == "data" && byteRate > 0)
                        return TimeSpan.FromSeconds(Math.Min(size, wav.Length - i - 8) / (double)byteRate);
                    if (size < 0) break;
                    i += 8 + size + (size & 1);
                }
            }
            catch { }
            return TimeSpan.FromSeconds(Math.Max(1, (wav.Length - 44) / 48000.0));
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _cts.Cancel();
        }
    }

    /// <summary>语音合成结果：Error 为 null 表示成功；Notice 为降级等提示。/ Synthesis result: Error = null on success; Notice = fallback notice.</summary>
    public sealed class VoiceResult
    {
        public string Error;
        public string Notice;
    }

    /// <summary>从 Copilot 最后一条回答中提炼不超过 30 字的任务概述。</summary>
    public static class TaskSummary
    {
        public const int MaxLength = 30;

        public static string From(ChatTranscript t, bool english = false)
        {
            if (t?.Messages == null) return null;
            for (int i = t.Messages.Count - 1; i >= 0; i--)
            {
                var m = t.Messages[i];
                if (m.Role != ChatRole.Assistant) continue;
                var s = FirstSentence(string.Join("\n", PartsText(m)));
                if (s != null) return s;
                break;
            }
            return FromUser(t, english);
        }

        /// <summary>最后一条回答的正文（去掉过程步骤），无回答时返回 null。</summary>
        public static string AnswerText(ChatTranscript t, int max = 3000)
        {
            if (t?.Messages == null) return null;
            for (int i = t.Messages.Count - 1; i >= 0; i--)
            {
                var m = t.Messages[i];
                if (m.Role != ChatRole.Assistant) continue;
                string s = string.Join("\n", PartsText(m)).Trim();
                if (s.Length == 0) return null;
                return s.Length > max ? s.Substring(0, max) : s;
            }
            return null;
        }

        /// <summary>中文字符占字母类字符 30% 以上视为中文。</summary>
        public static bool IsChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            int cjk = 0, letters = 0;
            foreach (char c in s)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) { cjk++; letters++; }
                else if (char.IsLetter(c)) letters++;
            }
            return letters == 0 || cjk * 10 >= letters * 3;
        }

        /// <summary>清理模型输出的概述：去掉引号、Markdown 符号、结尾标点并截断。</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Split('\n')[0];
            s = Regex.Replace(s, @"^(概述|总结|摘要)[:：]\s*", "");
            s = Regex.Replace(s, @"^(Summary|Overview)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[*_`#""“”「」『』]+", "").Trim();
            s = StripFiles(s);
            s = Regex.Replace(s, @"^我(已经|已)", "已");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            s = s.TrimEnd('。', '！', '？', '!', '?', '；', ';', '，', ',', '.');
            if (s.Length == 0) return null;
            s = Truncate(s).TrimEnd('，', ',', '、');
            return s.Length == 0 ? null : s;
        }

        private static string FromUser(ChatTranscript t, bool english)
        {
            for (int i = t.Messages.Count - 1; i >= 0; i--)
            {
                if (t.Messages[i].Role != ChatRole.User) continue;
                var s = FirstSentence(string.Join("\n", PartsText(t.Messages[i])));
                return s == null ? null : Truncate(Prompts.HandledPrefix(english) + s);
            }
            return null;
        }

        private static IEnumerable<string> PartsText(ChatMessage m)
        {
            foreach (var p in m.Parts)
                if (!p.IsStep && !string.IsNullOrWhiteSpace(p.Text)) yield return p.Text;
        }

        public static string FirstSentence(string md, bool truncate = true)
        {
            if (string.IsNullOrWhiteSpace(md)) return null;
            md = Regex.Replace(md, @"```[\s\S]*?(```|$)", "\n");
            md = Regex.Replace(md, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            md = Regex.Replace(md, @"`([^`]*)`", "$1");
            foreach (var rawLine in md.Split('\n'))
            {
                string line = rawLine.Trim();
                if (Regex.IsMatch(line, @"^\|?[\s:\-|]+\|?$")) continue;
                line = Regex.Replace(line, @"^(#{1,6}\s*|>\s*|[-*+]\s+|\d+[.)、]\s*|\|)", "").Trim();
                line = Regex.Replace(line, @"[*_~|#]+", "");
                line = Regex.Replace(line, @"[\p{So}\p{Cs}\u2600-\u27BF\uFE0F]", "");
                line = Regex.Replace(line, @"\s+", " ").Trim();
                line = Regex.Replace(line, @"^(好的|好|OK|Okay)[，,！!。\s]*", "", RegexOptions.IgnoreCase);
                line = Regex.Replace(line, @"^我(已经|已)", "已");
                if (Regex.Matches(line, @"[\p{L}\p{N}]").Count < 4) continue;
                var sentence = Regex.Split(line, @"(?<=[。！？!?；;])|(?<=\.)\s")[0].Trim();
                sentence = sentence.TrimEnd('。', '！', '？', '!', '?', '；', ';', '：', ':', '，', ',', '.');
                if (sentence.Length > 0) return truncate ? Truncate(sentence) : sentence;
            }
            return null;
        }

        /// <summary>按“字”截断：一个汉字或一个英文单词 / 数字算 1 个字，空白不计；优先在逗号处截断。</summary>
        public static string Truncate(string s)
        {
            int units = 0, i = 0, lastComma = -1, commaUnits = 0;
            while (i < s.Length)
            {
                char c = s[i];
                int start = i;
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c < 128 && char.IsLetterOrDigit(c))
                    while (i < s.Length && s[i] < 128 && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '\'' || s[i] == '-')) i++;
                else i += char.IsSurrogatePair(s, i) ? 2 : 1;
                if (units == MaxLength)
                    return (lastComma >= 0 && commaUnits >= MaxLength / 2 ? s.Substring(0, lastComma) : s.Substring(0, start)).Trim();
                units++;
                if (c == '，' || c == ',') { lastComma = start; commaUnits = units; }
            }
            return s;
        }

        /// <summary>去掉不适合朗读的文件名 / 路径（如“在 OrderService.cs 中的”）。</summary>
        private static string StripFiles(string s) =>
            Regex.Replace(s, @"(在|于)?\s*[A-Za-z0-9_./\\:-]+\.(cs|xaml|csproj|sln|slnx|json|xml|md|js|ts|py|cpp|h|config|txt|dll|exe)(?![A-Za-z0-9])\s*(中|里|内)?(的)?", "", RegexOptions.IgnoreCase);
    }
}
