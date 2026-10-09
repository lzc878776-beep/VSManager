using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>
    /// 整个测试程序集的兜底隔离：运行期间把数据目录（%APPDATA%\VSManager）重定向到测试输出下的独立目录，
    /// 即使某个测试忘了用 <see cref="TempDataFolder"/>，也不会写入真实用户的对话记录、任务清单或设置。
    /// Assembly-wide safety net: redirects the data folder (%APPDATA%\VSManager) to an isolated folder under the test output for the
    /// whole run, so a test that forgets <see cref="TempDataFolder"/> still never writes the real user's chat history, task list or settings.
    /// </summary>
    [TestClass]
    public static class TestAssemblySetup
    {
        private static IDisposable _override;
        private static string _folder;

        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            _folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _override = AppPaths.OverrideDataFolder(_folder);
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            _override?.Dispose();
            try { Directory.Delete(_folder, true); } catch { }
        }
    }
}
