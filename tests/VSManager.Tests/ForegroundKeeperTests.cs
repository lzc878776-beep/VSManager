using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class ForegroundKeeperTests
    {
        [TestMethod]
        public void ShouldRestore_OnlyWhenOperatedVsTookTheForeground()
        {
            var vs = new[] { 100 };
            // 用户在 VSManager（pid 1），发送后 VS 抢到前台 / User in VSManager, VS took the foreground
            Assert.IsTrue(ForegroundKeeper.ShouldRestore(1, 100, vs));
            // 开始时没有前台窗口 / No foreground window at the start
            Assert.IsTrue(ForegroundKeeper.ShouldRestore(0, 100, vs));
            // 用户本来就在该 VS 中 / User was already in that VS
            Assert.IsFalse(ForegroundKeeper.ShouldRestore(100, 100, vs));
            // 前台仍是 VSManager 或别的程序 / Foreground is still VSManager or another program
            Assert.IsFalse(ForegroundKeeper.ShouldRestore(1, 1, vs));
            Assert.IsFalse(ForegroundKeeper.ShouldRestore(1, 200, vs));
            Assert.IsFalse(ForegroundKeeper.ShouldRestore(1, 0, vs));
            Assert.IsFalse(ForegroundKeeper.ShouldRestore(1, 100, new int[0]));
        }

        [TestMethod]
        public void GuardStep_RestoresLateStealsUntilUserActsOrTimeout()
        {
            // VS 在发送结束后才抢到前台：切回 / VS took the foreground after the send finished: switch back
            Assert.AreEqual(ForegroundKeeper.GuardStep.Restore, ForegroundKeeper.NextGuardStep(1500, false, true));
            Assert.AreEqual(ForegroundKeeper.GuardStep.Wait, ForegroundKeeper.NextGuardStep(1500, false, false));
            // 用户已操作或超时：停止，不与用户抢焦点 / User acted or timed out: stop, never fight the user
            Assert.AreEqual(ForegroundKeeper.GuardStep.Stop, ForegroundKeeper.NextGuardStep(1500, true, true));
            Assert.AreEqual(ForegroundKeeper.GuardStep.Stop, ForegroundKeeper.NextGuardStep(ForegroundKeeper.GuardMs + 1, false, true));
        }

        [TestMethod]
        public void UserActedSince_HandlesTickWraparound()
        {
            Assert.IsTrue(ForegroundKeeper.UserActedSince(1001, 1000));
            Assert.IsFalse(ForegroundKeeper.UserActedSince(1000, 1000));
            Assert.IsFalse(ForegroundKeeper.UserActedSince(900, 1000));
            Assert.IsTrue(ForegroundKeeper.UserActedSince(int.MinValue + 10, int.MaxValue - 10));
        }
    }
}