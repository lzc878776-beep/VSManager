using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NAudio.Wave;

namespace VSManager
{
    /// <summary>
    /// 豆包流式语音识别（火山引擎 V3 sauc，WebSocket 二进制协议）。
    /// 按住说话：Start 打开麦克风并边录边传，Stop 发送结束包并等待最终文本。
    /// </summary>
    public sealed class DoubaoAsr : IDisposable
    {
        public const string Endpoint = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async";
        public const string DefaultResource = "volc.seedasr.sauc.duration";
        public static readonly string[] Resources =
            { "volc.seedasr.sauc.duration", "volc.seedasr.sauc.concurrent", "volc.bigasr.sauc.duration", "volc.bigasr.sauc.concurrent" };
        public const int SampleRate = 16000;
        public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

        private const byte FullClientRequest = 0x1, AudioOnlyRequest = 0x2, FullServerResponse = 0x9, ServerError = 0xF;
        private const int ChunkBytes = SampleRate * 2 / 5; // 200 ms

        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly TaskCompletionSource<string> _final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _pending = new MemoryStream();
        private readonly object _gate = new object();
        private WaveInEvent _mic;
        private Task _receive;
        private Task _sendChain = Task.CompletedTask;
        private volatile string _text = "";
        private bool _stopped, _disposed, _connected;

        /// <summary>识别中间结果（后台线程）。</summary>
        public event Action<string> Partial;
        /// <summary>输入音量 0~1（后台线程）。</summary>
        public event Action<float> Level;

        public string Text => _text;

        /// <summary>连接服务并发送识别参数；micDevice 为 null 时不打开麦克风（用于测试）。</summary>
        public async Task ConnectAsync(string apiKey, string resource)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("未填写豆包语音 API Key");
            _ws.Options.SetRequestHeader("X-Api-Key", apiKey.Trim());
            _ws.Options.SetRequestHeader("X-Api-Resource-Id", string.IsNullOrWhiteSpace(resource) ? DefaultResource : resource.Trim());
            _ws.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    timeout.CancelAfter(8000);
                    await _ws.ConnectAsync(new Uri(Endpoint), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (WebSocketException ex)
            {
                throw new InvalidOperationException("无法连接豆包语音识别：" + ex.Message +
                    "（请确认 API Key 已开通所选识别资源 " + resource + "）");
            }
            catch (OperationCanceledException) { throw new InvalidOperationException("连接豆包语音识别超时"); }

            var json = new JavaScriptSerializer().Serialize(new
            {
                user = new { uid = "vsmanager" },
                audio = new { format = "pcm", codec = "raw", rate = SampleRate, bits = 16, channel = 1 },
                request = new { model_name = "bigmodel", enable_itn = true, enable_punc = true, enable_ddc = true, result_type = "full" }
            });
            await SendFrame(FullClientRequest, 0x0, 0x1, Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
            _receive = Task.Run(ReceiveLoop);
            lock (_gate)
            {
                _connected = true;
                if (_stopped) FlushFinal();
                else FlushChunks();
            }
        }

        /// <summary>打开默认麦克风，开始边录边传。</summary>
        public void StartMicrophone()
        {
            if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("未检测到麦克风");
            _mic = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 100, NumberOfBuffers = 3 };
            _mic.DataAvailable += (s, e) => { ReportLevel(e.Buffer, e.BytesRecorded); Feed(e.Buffer, 0, e.BytesRecorded); };
            _mic.StartRecording();
        }

        /// <summary>追加 16k/16bit/单声道 PCM；连接建立前先缓存，避免丢掉开头。</summary>
        public void Feed(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                if (_stopped || _disposed) return;
                _pending.Write(buffer, offset, count);
                if (_connected) FlushChunks();
            }
        }

        private void FlushChunks()
        {
            if (_pending.Length < ChunkBytes) return;
            var all = _pending.ToArray();
            int n = all.Length / ChunkBytes * ChunkBytes;
            for (int i = 0; i < n; i += ChunkBytes)
            {
                var chunk = new byte[ChunkBytes];
                Buffer.BlockCopy(all, i, chunk, 0, ChunkBytes);
                Enqueue(chunk, false);
            }
            _pending.SetLength(0);
            _pending.Write(all, n, all.Length - n);
        }

        private void FlushFinal()
        {
            FlushChunks();
            Enqueue(_pending.ToArray(), true);
            _pending.SetLength(0);
        }

        /// <summary>停止录音，发送结束包并等待最终结果。</summary>
        public async Task<string> StopAsync(int timeoutMs = 10000)
        {
            StopMic();
            lock (_gate)
            {
                if (!_stopped)
                {
                    _stopped = true;
                    if (_connected) FlushFinal();
                }
            }
            var done = await Task.WhenAny(_final.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (done != _final.Task)
            {
                if (_text.Length > 0) return _text;
                throw new InvalidOperationException("等待识别结果超时");
            }
            return await _final.Task.ConfigureAwait(false);
        }

        private void Enqueue(byte[] chunk, bool last)
        {
            // WebSocket 不允许并发发送：串成一条链
            _sendChain = _sendChain.ContinueWith(_ => SendFrame(AudioOnlyRequest, last ? (byte)0x2 : (byte)0x0, 0x0, chunk),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _sendChain.ContinueWith(t => Fail(t.Exception?.GetBaseException().Message ?? "发送音频失败"), TaskContinuationOptions.OnlyOnFaulted);
        }

        private void ReportLevel(byte[] buf, int n)
        {
            int peak = 0;
            for (int i = 0; i + 1 < n; i += 2)
            {
                int v = Math.Abs((int)(short)(buf[i] | (buf[i + 1] << 8)));
                if (v > peak) peak = v;
            }
            try { Level?.Invoke(Math.Min(1f, peak / 20000f)); } catch { }
        }

        private async Task SendFrame(byte type, byte flags, byte serialization, byte[] payload)
        {
            byte[] gz = Gzip(payload);
            var frame = new byte[8 + gz.Length];
            frame[0] = 0x11;
            frame[1] = (byte)((type << 4) | flags);
            frame[2] = (byte)((serialization << 4) | 0x1);
            frame[3] = 0;
            WriteBE(frame, 4, gz.Length);
            Buffer.BlockCopy(gz, 0, frame, 8, gz.Length);
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveLoop()
        {
            var buf = new byte[64 * 1024];
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                            if (r.MessageType == WebSocketMessageType.Close) { Fail(CloseText(r)); return; }
                            ms.Write(buf, 0, r.Count);
                        } while (!r.EndOfMessage);
                        if (HandleFrame(ms.ToArray(), json)) return;
                    }
                }
            }
            catch (OperationCanceledException) { _final.TrySetCanceled(); }
            catch (Exception ex) { Fail(ex.Message); }
        }

        private static string CloseText(WebSocketReceiveResult r) =>
            string.IsNullOrEmpty(r.CloseStatusDescription) ? "识别服务已断开" : "识别服务已断开：" + r.CloseStatusDescription;

        /// <summary>返回 true 表示已收到最终结果。</summary>
        private bool HandleFrame(byte[] f, JavaScriptSerializer json)
        {
            if (f.Length < 4) return false;
            int headerSize = (f[0] & 0x0F) * 4;
            int type = f[1] >> 4, flags = f[1] & 0x0F, compression = f[2] & 0x0F;
            int p = headerSize;
            if (type == ServerError)
            {
                int code = ReadBE(f, p), size = ReadBE(f, p + 4);
                string msg = Encoding.UTF8.GetString(f, p + 8, Math.Max(0, Math.Min(size, f.Length - p - 8)));
                Fail(Describe(code, msg));
                return true;
            }
            if (type != FullServerResponse) return false;
            if ((flags & 0x1) != 0) p += 4;
            int len = ReadBE(f, p);
            p += 4;
            var payload = new byte[Math.Max(0, Math.Min(len, f.Length - p))];
            Buffer.BlockCopy(f, p, payload, 0, payload.Length);
            if (compression == 0x1) payload = Gunzip(payload);
            if (payload.Length > 0 && json.DeserializeObject(Encoding.UTF8.GetString(payload)) is Dictionary<string, object> d &&
                d.TryGetValue("result", out var res) && res is Dictionary<string, object> rd && rd.TryGetValue("text", out var t) && t is string text)
            {
                if (text != _text) { _text = text; try { Partial?.Invoke(text); } catch { } }
            }
            if ((flags & 0x2) != 0)
            {
                _final.TrySetResult(_text);
                return true;
            }
            return false;
        }

        private static string Describe(int code, string msg)
        {
            if (msg.Contains("not granted")) return "该 API Key 未开通所选语音识别资源（" + code + "）";
            if (code == 45000081) return "等待音频超时";
            if (code == 45000002 || code == 45000151) return "音频格式或参数错误（" + code + "）：" + msg;
            return "豆包语音识别错误 " + code + "：" + msg;
        }

        private void Fail(string message)
        {
            if (_final.Task.IsCompleted) return;
            if (_stopped && _text.Length > 0) _final.TrySetResult(_text);
            else _final.TrySetException(new InvalidOperationException(message));
        }

        private void StopMic()
        {
            var mic = _mic;
            _mic = null;
            if (mic == null) return;
            try { mic.StopRecording(); } catch { }
            mic.Dispose();
        }

        private static void WriteBE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static int ReadBE(byte[] b, int o) =>
            o + 4 > b.Length ? 0 : (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Gunzip(byte[] data)
        {
            using (var src = new MemoryStream(data))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        /// <summary>识别一段现成的 PCM（设置页自检用）。</summary>
        public static async Task<string> RecognizeAsync(string apiKey, string resource, byte[] pcm)
        {
            using (var asr = new DoubaoAsr())
            {
                await asr.ConnectAsync(apiKey, resource).ConfigureAwait(false);
                for (int i = 0; i < pcm.Length; i += ChunkBytes)
                {
                    asr.Feed(pcm, i, Math.Min(ChunkBytes, pcm.Length - i));
                    // 服务端按实时节奏处理，灌得过快会丢尾部
                    await Task.Delay(200).ConfigureAwait(false);
                }
                return await asr.StopAsync(15000).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopMic();
            _cts.Cancel();
            try { if (_ws.State == WebSocketState.Open) _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500); } catch { }
            _ws.Dispose();
        }
    }
}
