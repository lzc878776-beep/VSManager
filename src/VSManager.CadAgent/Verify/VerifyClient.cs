using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace VSManager.Verify
{
    /// <summary>ai.exe verify list 的输出。/ Output of ai.exe verify list.</summary>
    [DataContract]
    public sealed class VerifyDiscovery
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "endpoints")] public List<VerifyResponse> Endpoints { get; set; } = new List<VerifyResponse>();
    }

    /// <summary>ai.exe verify run 的输入。/ Input of ai.exe verify run.</summary>
    [DataContract]
    public sealed class VerifyRunRequest
    {
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "check")] public string Check { get; set; }
        [DataMember(Name = "args", EmitDefaultValue = false)] public Dictionary<string, string> Args { get; set; }
        [DataMember(Name = "timeoutMs", EmitDefaultValue = false)] public int TimeoutMs { get; set; }
    }

    /// <summary>
    /// 验证端点客户端（ai.exe 使用）：枚举本机验证管道，并核对管道服务端进程 ID 与管道名一致，防止同名管道冒充。
    /// Verification endpoint client (used by ai.exe): enumerates local verification pipes and checks that the pipe server's process id matches the pipe name, so a squatting pipe cannot impersonate an endpoint.
    /// </summary>
    public static class VerifyClient
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string name, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr handle, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

        /// <summary>列出本机正在监听的验证端点进程 ID。/ Lists process ids of local verification endpoints that are listening.</summary>
        public static List<int> Discover()
        {
            var pids = new List<int>();
            IntPtr h = FindFirstFile(@"\\.\pipe\*", out var data);
            if (h == new IntPtr(-1)) return pids;
            try
            {
                do
                {
                    int pid = VerifyProtocol.ParsePid(data.cFileName);
                    if (pid > 0 && !pids.Contains(pid)) pids.Add(pid);
                } while (FindNextFile(h, out data));
            }
            finally { FindClose(h); }
            pids.Sort();
            return pids;
        }

        /// <summary>调用一个端点；连接失败、超时或身份不符时返回错误应答，不抛异常。/ Calls one endpoint; connection failures, timeouts or identity mismatches return an error reply instead of throwing.</summary>
        public static VerifyResponse Call(int pid, VerifyRequest request, int timeoutMs)
        {
            if (pid <= 0) return VerifyResponse.Failure(VerifyErrors.InvalidRequest, "缺少进程 ID / Missing process id");
            var pipe = new NamedPipeClientStream(".", VerifyProtocol.PipeName(pid), PipeDirection.InOut, PipeOptions.None);
            try
            {
                try { pipe.Connect(Math.Min(5000, Math.Max(500, timeoutMs))); }
                catch (TimeoutException) { return VerifyResponse.Failure(VerifyErrors.NoEndpoint, "进程 " + pid + " 没有可连接的验证端点 / No verification endpoint answers in process " + pid); }
                catch (IOException ex) { return VerifyResponse.Failure(VerifyErrors.NoEndpoint, "进程 " + pid + " 的验证端点不可用 / Endpoint unavailable: " + ex.Message); }
                catch (UnauthorizedAccessException) { return VerifyResponse.Failure(VerifyErrors.Transport, "无权访问验证管道（CAD 与 VSManager 需以同一用户、同一权限级别运行）/ Access denied (run both as the same user and elevation)"); }
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint server) || server != (uint)pid)
                    return VerifyResponse.Failure(VerifyErrors.Transport, "管道服务端进程与名称不符，已拒绝 / Pipe server process does not match its name; rejected");
                var io = Task.Run(() =>
                {
                    VerifyWire.WriteLine(pipe, VerifyWire.Serialize(request));
                    return VerifyWire.ReadLine(pipe);
                });
                int wait = VerifyProtocol.ClampTimeout(timeoutMs) + 5000;
                if (!io.Wait(wait)) return VerifyResponse.Failure(VerifyErrors.Timeout, "等待验证端点应答超时 / Timed out waiting for the endpoint");
                VerifyResponse reply = null;
                try { reply = VerifyWire.Deserialize<VerifyResponse>(io.Result); } catch (SerializationException) { }
                return reply ?? VerifyResponse.Failure(VerifyErrors.Transport, "验证端点应答无法解析 / The endpoint reply could not be parsed");
            }
            catch (AggregateException ex)
            {
                var inner = ex.GetBaseException();
                return VerifyResponse.Failure(VerifyErrors.Transport, "与验证端点通信失败 / Endpoint communication failed: " + inner.Message);
            }
            catch (Win32Exception ex) { return VerifyResponse.Failure(VerifyErrors.Transport, ex.Message); }
            finally { pipe.Dispose(); }
        }
    }
}
