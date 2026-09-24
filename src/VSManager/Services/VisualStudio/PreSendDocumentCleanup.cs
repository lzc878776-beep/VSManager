using System;
using System.IO;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>可选清理不改变发送内容、返回值或异常；清理及通知失败单独记录。/ Optional cleanup never changes send content, results or exceptions; cleanup and notification failures are logged separately.</summary>
    internal static class PreSendDocumentCleanup
    {
        internal static async Task<string> SendAsync(bool enabled, Func<Task<VsDocumentCleanupResult>> cleanup,
            Action<VsDocumentCleanupResult> report, Action<string> log, Func<Task<string>> send)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (enabled)
            {
                try
                {
                    var result = await cleanup();
                    report(result);
                }
                catch (Exception ex) when (VsDocumentCleanup.Expected(ex) || ex is IOException)
                {
                    log("发送前文档清理或通知失败，继续原发送流程 / Pre-send document cleanup or notification failed; continuing original send: "
                        + ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + ")");
                }
            }
            return await send();
        }
    }
}
