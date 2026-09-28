using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace VSManager.Tests
{
    /// <summary>
    /// 调试启动 CAD 前把 FILEDIA=0 改回 1（在临时注册表键中模拟 AutoCAD 配置）。
    /// Setting FILEDIA=0 back to 1 before debugging starts CAD (AutoCAD settings simulated under a temporary registry key).
    /// </summary>
    [TestClass]
    public class CadFileDiaTests
    {
        private string _root;
        private RegistryKey _user, _machine;

        [TestInitialize]
        public void Init()
        {
            _root = @"Software\VSManager.Tests\" + Guid.NewGuid().ToString("N");
            _user = Registry.CurrentUser.CreateSubKey(_root + @"\User");
            _machine = Registry.CurrentUser.CreateSubKey(_root + @"\Machine");
        }

        [TestCleanup]
        public void Cleanup()
        {
            _user.Dispose();
            _machine.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_root, false);
        }

        private void Product(string product, object fileDialog, string location)
        {
            using (var cfg = _user.CreateSubKey(CadFileDia.AutoCadKey + "\\" + product + "\\" + CadFileDia.ConfigKey))
                if (fileDialog != null) cfg.SetValue(CadFileDia.ValueName, fileDialog, RegistryValueKind.DWord);
            if (location != null)
                using (var m = _machine.CreateSubKey(CadFileDia.AutoCadKey + "\\" + product))
                    m.SetValue("AcadLocation", location);
        }

        private int Value(string product) =>
            Convert.ToInt32(_user.OpenSubKey(CadFileDia.AutoCadKey + "\\" + product + "\\" + CadFileDia.ConfigKey).GetValue(CadFileDia.ValueName));

        [TestMethod]
        public void ZeroIsSetToOne_OnlyForTheMatchingInstall()
        {
            Product(@"R24.3\ACAD-7101:804", 0, @"C:\Program Files\Autodesk\AutoCAD 2024\");
            Product(@"R25.0\ACAD-8101:804", 0, @"C:\Program Files\Autodesk\AutoCAD 2025\");
            var changed = CadFileDia.EnsureAutoCad(_user, _machine, "\"C:\\Program Files\\Autodesk\\AutoCAD 2024\\acad.exe\"");
            CollectionAssert.AreEqual(new[] { @"R24.3\ACAD-7101:804" }, changed);
            Assert.AreEqual(1, Value(@"R24.3\ACAD-7101:804"));
            Assert.AreEqual(0, Value(@"R25.0\ACAD-8101:804"));
            Assert.AreEqual(0, CadFileDia.EnsureAutoCad(_user, _machine, @"C:\Program Files\Autodesk\AutoCAD 2024\acad.exe").Count);
        }

        [TestMethod]
        public void NoMatchingInstall_FixesEveryZeroProduct_AndLeavesOthers()
        {
            Product(@"R24.3\ACAD-7101:804", 0, null);
            Product(@"R24.3\ACAD-7101:409", 1, null);
            Product(@"R25.0\ACAD-8101:804", null, null);
            var changed = CadFileDia.EnsureAutoCad(_user, _machine, @"D:\Other\acad.exe");
            CollectionAssert.AreEqual(new[] { @"R24.3\ACAD-7101:804" }, changed);
            Assert.AreEqual(1, Value(@"R24.3\ACAD-7101:804"));
            Assert.AreEqual(1, Value(@"R24.3\ACAD-7101:409"));
            Assert.IsNull(_user.OpenSubKey(CadFileDia.AutoCadKey + @"\R25.0\ACAD-8101:804\" + CadFileDia.ConfigKey).GetValue(CadFileDia.ValueName));
        }

        [TestMethod]
        public void OtherHostsAndMissingKeys_ChangeNothing()
        {
            Assert.AreEqual(0, CadFileDia.EnsureAutoCad(_user, _machine, null).Count);
            Assert.AreEqual(0, CadFileDia.EnsureEnabled("ZWCAD", @"C:\Program Files\ZWSOFT\ZWCAD\ZWCAD.exe").Count);
            StringAssert.StartsWith(CadDebugPlan.FileDiaGuard, "(if (= (getvar \"FILEDIA\") 0) (setvar \"FILEDIA\" 1))");
            Assert.IsTrue(CadDebugPlan.FileDiaGuard.EndsWith("\r\n"));
        }
    }
}
