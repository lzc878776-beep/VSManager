using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class UserActivityTests
    {
        [DataTestMethod]
        [DataRow(1000, true, 1000, true)]
        [DataRow(1400, true, 1000, true)]
        [DataRow(1600, true, 1000, false)]
        [DataRow(900, true, 1000, false)]
        [DataRow(1000, false, 1000, false)]
        public void IsOwnInput_OnlyWithinSyntheticWindow(int last, bool has, int synthetic, bool expected) =>
            Assert.AreEqual(expected, UserActivity.IsOwnInput(last, has, synthetic));
    }
}
