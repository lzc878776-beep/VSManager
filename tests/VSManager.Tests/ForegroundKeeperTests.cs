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
    }
}
