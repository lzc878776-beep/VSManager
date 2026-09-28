using System;
using System.IO;
using System.Text;
using System.Threading;
using VSManager.CadAgent;

namespace VSManager.AiHost
{
    /// <summary>
    /// ai.exe 命令行：ai run [序列文件|-]（缺省读标准输入），ai actions，ai --help。结果 JSON 写到标准输出。
    /// 连接信息来自环境变量 VSM_URL / VSM_TOKEN（由 VSManager 设置，令牌不出现在命令行中）。
    /// ai.exe command line: ai run [sequence file|-] (stdin by default), ai actions, ai --help. The result JSON goes to stdout.
    /// Connection details come from the VSM_URL / VSM_TOKEN environment variables (set by VSManager, so the token never appears on the command line).
    /// </summary>
    internal static class Program
    {
        private const string Help =
@"ai.exe — VSManager CAD 动作序列执行器 / CAD action-sequence executor

用法 / Usage:
  ai run [file|-]     执行动作序列（缺省读标准输入）/ Run a sequence (stdin by default)
  ai actions          列出动作协议 v1 / List action protocol v1
  ai verify list [pid...]  列出目标项目的验证端点与检查项（本机命名管道）/ List target-project verification endpoints and checks (local named pipes)
  ai verify run [file|-]   执行一个检查 / Run one check: {""pid"":1234,""check"":""name"",""args"":{},""timeoutMs"":60000}
  ai --help           显示帮助 / Show help

环境变量（仅 run）/ Environment (run only): VSM_URL (http://127.0.0.1:<port>), VSM_TOKEN
退出码 / Exit codes: 0 全部成功或检查通过 / all ok or check passed, 1 有失败或检查未通过 / failed or check not passed, 2 用法或连接错误 / usage or connection error";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try { Console.InputEncoding = new UTF8Encoding(false); } catch (IOException) { }
            string verb = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "--help";
            switch (verb)
            {
                case "actions":
                    Console.WriteLine(string.Join(Environment.NewLine, CadActions.All));
                    return 0;
                case "run":
                    return Run(args.Length > 1 ? args[1] : "-");
                case "verify":
                    return VerifyCommand.Run(args, Console.In, Console.Out);
                default:
                    Console.WriteLine(Help);
                    return verb == "--help" || verb == "-h" || verb == "/?" ? 0 : 2;
            }
        }

        private static int Run(string file)
        {
            string url = Environment.GetEnvironmentVariable("VSM_URL");
            string token = Environment.GetEnvironmentVariable("VSM_TOKEN");
            if (string.IsNullOrWhiteSpace(url)) return Error("缺少 VSM_URL / VSM_URL is missing");
            CadSequence seq;
            try
            {
                string json = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file, Encoding.UTF8);
                seq = CadJson.Deserialize<CadSequence>(json);
            }
            catch (Exception ex) { return Error("动作序列 JSON 无效 / Invalid sequence JSON: " + ex.Message); }
            if (seq == null) return Error("动作序列为空 / Empty sequence");
            CadAdapter adapter = null;
            if (!string.IsNullOrWhiteSpace(seq.Adapter))
            {
                adapter = CadAdapterStore.Find(seq.Adapter);
                if (adapter == null) return Error("找不到适配包 / Adapter not found: " + seq.Adapter);
            }
            ICadChannel channel;
            try { channel = new WebApiChannel(url, token); }
            catch (Exception ex) { return Error(ex.Message); }
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                var result = new SequenceRunner(channel, adapter).Run(seq, cts.Token);
                Console.WriteLine(CadJson.Serialize(result));
                return result.Ok ? 0 : 1;
            }
        }

        private static int Error(string message)
        {
            Console.WriteLine(CadJson.Serialize(new CadSequenceResult { Ok = false, Message = message }));
            return 2;
        }
    }
}
