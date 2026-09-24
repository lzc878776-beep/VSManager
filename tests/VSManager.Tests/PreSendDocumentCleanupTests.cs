using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class PreSendDocumentCleanupTests
    {
        [TestMethod]
        public async Task Disabled_DoesNotInspectOrNotify_AndReturnsOriginalSendResult()
        {
            int sends = 0;
            string result = await PreSendDocumentCleanup.SendAsync(false,
                () => throw new AssertFailedException("不应清理 / Must not clean"),
                _ => throw new AssertFailedException("不应通知 / Must not notify"),
                _ => throw new AssertFailedException("不应记录清理 / Must not log cleanup"),
                () => { sends++; return Task.FromResult("已发送"); });
            Assert.AreEqual("已发送", result);
            Assert.AreEqual(1, sends);
        }

        [TestMethod]
        public async Task Enabled_AwaitsCleanupAndReportBeforeSingleSend()
        {
            var order = new List<string>();
            var gate = new TaskCompletionSource<VsDocumentCleanupResult>();
            Task<string> task = PreSendDocumentCleanup.SendAsync(true,
                () => { order.Add("cleanup"); return gate.Task; },
                result => { Assert.AreEqual(2, result.Closed); order.Add("report"); },
                _ => order.Add("error"),
                () => { order.Add("send"); return Task.FromResult("original result"); });
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "cleanup" }, order);
            gate.SetResult(new VsDocumentCleanupResult { Closed = 2 });
            Assert.AreEqual("original result", await task);
            CollectionAssert.AreEqual(new[] { "cleanup", "report", "send" }, order);
        }

        [TestMethod]
        public async Task CleanupFailure_IsLogged_AndDoesNotChangeDelivery()
        {
            var log = new List<string>();
            int sends = 0;
            string result = await PreSendDocumentCleanup.SendAsync(true,
                () => throw new COMException("无法连接 / Cannot connect"),
                _ => Assert.Fail("失败不应报告成功 / Failed cleanup must not report success"), log.Add,
                () => { sends++; return Task.FromResult("发送被窗口阻挡"); });
            Assert.AreEqual("发送被窗口阻挡", result);
            Assert.AreEqual(1, sends);
            StringAssert.Contains(log[0], "continuing original send");
        }

        [TestMethod]
        public async Task NotificationFailure_DoesNotPreventSend_AndSendErrorsStillPropagate()
        {
            var log = new List<string>();
            var failure = new InvalidOperationException("发送失败 / Send failed");
            var actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => PreSendDocumentCleanup.SendAsync(true,
                () => Task.FromResult(new VsDocumentCleanupResult()),
                _ => throw new InvalidOperationException("通知失败 / Notification failed"), log.Add,
                () => throw failure));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(1, log.Count);
        }
    }
}
