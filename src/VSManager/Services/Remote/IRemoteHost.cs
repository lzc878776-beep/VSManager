using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>Shared desktop capabilities used by the web remote.</summary>
    public interface IRemoteHost
    {
        IList<VsInstance> Instances { get; }
        string NameOf(VsInstance v);
        string NoteOf(VsInstance v);
        Task<string> SetNote(VsInstance v, string note);
        ChatTranscript CachedChat(VsInstance v);
        Task<string> SendChat(VsInstance v, string text);
        Task<string> InvokeChatButton(VsInstance v, string automationId, string name);
        void Log(string s);
        void FocusChat(int pid);
        /// <summary>把所有 VS 的 Copilot 对话窗格切换为停靠的工具窗口。</summary>
        Task<string> DockPanes();
        /// <summary>读取错误列表文本。</summary>
        Task<string> ErrorList(VsInstance v, int max);
    }
}
