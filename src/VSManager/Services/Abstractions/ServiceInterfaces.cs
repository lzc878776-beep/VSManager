using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>
    /// VS 进程与 DTE 操作（枚举实例、调试、错误列表、启动配置、窗口布局）。默认实现转发到 <see cref="VsService"/>。
    /// 调用方需自行切换到 DTE 线程（<see cref="DteWorker"/>）。
    /// VS process and DTE operations (enumerate instances, debugging, error list, launch profiles, window layout).
    /// The default implementation forwards to <see cref="VsService"/>; callers must marshal to the DTE thread (<see cref="DteWorker"/>).
    /// </summary>
    public interface IVsOperations
    {
        List<VsInstance> Enumerate(Dictionary<int, VsInstance> existing);
        string DebugAction(VsInstance vs, string action);
        string ReadErrorList(VsInstance vs, int max);
        VsService.LaunchProfiles GetLaunchProfiles(VsInstance vs);
        string SetLaunchProfile(VsInstance vs, string name);
        bool OpenCopilotChat(VsInstance vs);
        string DockCopilotAsToolWindow(VsInstance vs, string keyword);
        string MoveToolWindows(VsInstance vs, Rectangle area, string layout);
    }

    /// <summary>默认实现：转发到静态 <see cref="VsService"/>。/ Default implementation forwarding to the static <see cref="VsService"/>.</summary>
    public sealed class VsOperations : IVsOperations
    {
        public static readonly VsOperations Default = new VsOperations();

        public List<VsInstance> Enumerate(Dictionary<int, VsInstance> existing) => VsService.Enumerate(existing);
        public string DebugAction(VsInstance vs, string action) => VsService.DebugAction(vs, action);
        public string ReadErrorList(VsInstance vs, int max) => VsService.ReadErrorList(vs, max);
        public VsService.LaunchProfiles GetLaunchProfiles(VsInstance vs) => VsService.GetLaunchProfiles(vs);
        public string SetLaunchProfile(VsInstance vs, string name) => VsService.SetLaunchProfile(vs, name);
        public bool OpenCopilotChat(VsInstance vs) => VsService.OpenCopilotChat(vs);
        public string DockCopilotAsToolWindow(VsInstance vs, string keyword) => VsService.DockCopilotAsToolWindow(vs, keyword);
        public string MoveToolWindows(VsInstance vs, Rectangle area, string layout) => VsService.MoveToolWindows(vs, area, layout);
    }

    /// <summary>
    /// Copilot 对话窗格操作（通过 UI Automation）：读取对话、发送消息、点击按钮。由 <see cref="CopilotChat"/> 实现，需在 STA 线程调用。
    /// Copilot chat pane operations (via UI Automation): read the conversation, send a message, press a button.
    /// Implemented by <see cref="CopilotChat"/>; call on an STA thread.
    /// </summary>
    public interface ICopilotChannel
    {
        ChatTranscript Read(VsInstance vs, int maxMessages = 40);
        ChatTranscript ReadTail(VsInstance vs, int maxMessages = 2, bool skipIfUnchanged = false);
        /// <summary>发送消息，返回结果文字（以「已发送」开头表示送达）。/ Sends a message; a result starting with "已发送" means delivered.</summary>
        string Send(VsInstance vs, string text, IntPtr returnTo, bool background, IReadOnlyList<ChatImage> images = null);
        string InvokeButton(VsInstance vs, string automationId, string actionName);
        void Focus(int pid);
        void Poke();
    }

    /// <summary>
    /// 语音播报服务（默认为豆包语音）。/ Voice announcement service (Doubao voice by default).
    /// </summary>
    public interface IVoiceService : IDisposable
    {
        event Action<string> Failed;
        event Action<string> Notice;
        void Speak(string text);
        void StopAll();
        Task<VoiceResult> TestAsync(string apiKey, string resource, string speaker, string text, bool english);
    }
}
