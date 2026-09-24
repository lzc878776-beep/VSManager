using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class DialogPolicyTests
    {
        [DataTestMethod]
        [DataRow("Microsoft Visual Studio", "Build succeeded.", "OK")]
        [DataRow("提示", "操作已成功完成。", "确定")]
        [DataRow("信息", "文件已保存！", "关闭(C)")]
        [DataRow("Information", "No updates are available", "&OK")]
        [DataRow("Notification", "The operation completed successfully!", "Close")]
        public void KnownNotices_CanBeAcknowledged(string title, string body, string button)
        {
            Assert.IsTrue(DialogPolicy.CanAcknowledge(title, body, new[] { button }, false));
        }

        [DataTestMethod]
        [DataRow("Microsoft Visual Studio", "Overwrite the existing file?", "OK")]
        [DataRow("Microsoft Visual Studio", "Delete all files?", "OK")]
        [DataRow("Microsoft Visual Studio", "Discard unsaved changes?", "OK")]
        [DataRow("Microsoft Visual Studio", "Install required components", "OK")]
        [DataRow("Microsoft Visual Studio", "Trust this publisher?", "OK")]
        [DataRow("Microsoft Visual Studio", "Grant permission to run commands", "OK")]
        [DataRow("提示", "操作已完成。继续将覆盖文件。", "确定")]
        [DataRow("提示", "操作已完成，是否丢弃修改？", "确定")]
        [DataRow("提示", "构建失败", "确定")]
        [DataRow("Microsoft Visual Studio", "Build succeeded. Restart now?", "OK")]
        [DataRow("Delete Files", "The operation completed successfully", "OK")]
        [DataRow("Information", "Unknown notification", "Close")]
        [DataRow("Information", "Build succeeded", "Yes")]
        [DataRow("Information", "Build succeeded", "Continue")]
        [DataRow(null, "Build succeeded", "OK")]
        [DataRow("Information", null, "OK")]
        [DataRow("Information", "Build succeeded", null)]
        public void UnknownOrRiskyDialogs_StayManual(string title, string body, string button)
        {
            Assert.IsFalse(DialogPolicy.CanAcknowledge(title, body, new[] { button }, false));
        }

        [TestMethod]
        public void InputsOrMultipleChoices_StayManual()
        {
            Assert.IsFalse(DialogPolicy.CanAcknowledge("Information", "Build succeeded", new[] { "OK" }, true));
            Assert.IsFalse(DialogPolicy.CanAcknowledge("Information", "Build succeeded", new[] { "OK", "Cancel" }, false));
            Assert.IsFalse(DialogPolicy.CanAcknowledge("Information", "Build succeeded", new[] { "OK", "OK" }, false));
            Assert.IsFalse(DialogPolicy.CanAcknowledge("Information", "Build succeeded", new string[0], false));
            Assert.IsFalse(DialogPolicy.CanAcknowledge("Information", "Build succeeded", null, false));
        }
    }
}
