using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class SolutionScanUiTests
    {
        [TestMethod]
        public void ScanDialog_RequiresSelection_AndRegistersOnlyCheckedFile()
        {
            RunUi(async (dialog, registry, data) =>
            {
                File.WriteAllText(data.File("Order.sln"), "");
                File.WriteAllText(data.File("Rebar.slnx"), "");
                Field<TextBox>(dialog, "_folder").Text = data.Path;
                await (Task)typeof(SolutionScanForm).GetMethod("ScanAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dialog, null);
                var files = Field<CheckedListBox>(dialog, "_files");
                Assert.AreEqual(2, files.Items.Count);
                Assert.AreEqual(0, files.CheckedItems.Count);
                Assert.AreEqual(0, registry.Count);
                Assert.IsFalse(File.Exists(SolutionRegistry.DefaultPath));
                var import = Field<FlatButton>(dialog, "_import");
                Assert.IsFalse(import.Enabled);
                files.SetItemChecked(0, true);
                Assert.IsTrue(import.Enabled);
                typeof(SolutionScanForm).GetMethod("ImportSelected", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dialog, null);
                Assert.AreEqual(1, registry.Count);
                Assert.AreEqual("Order", registry.Items[0].Alias);
                Assert.AreEqual(1, new SolutionRegistry().Load().Count);
                Assert.AreEqual(DialogResult.OK, dialog.DialogResult);
            });
        }

        [TestMethod]
        public void ScanDialog_InvalidFolder_ShowsErrorAndLeavesRegistryUntouched()
        {
            RunUi(async (dialog, registry, data) =>
            {
                Field<TextBox>(dialog, "_folder").Text = data.File("missing");
                await (Task)typeof(SolutionScanForm).GetMethod("ScanAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dialog, null);
                StringAssert.Contains(Field<TextBox>(dialog, "_status").Text, "扫描失败 / Scan failed");
                Assert.IsTrue(Field<FlatButton>(dialog, "_scan").Enabled);
                Assert.IsFalse(Field<FlatButton>(dialog, "_import").Enabled);
                Assert.AreEqual(0, registry.Count);
                Assert.IsFalse(File.Exists(SolutionRegistry.DefaultPath));
            });
        }

        private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);

        private static void RunUi(Func<SolutionScanForm, SolutionRegistry, TempDataFolder, Task> action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                using (var data = new TempDataFolder())
                {
                    var registry = new SolutionRegistry();
                    using (var dialog = new SolutionScanForm(registry))
                    {
                        dialog.Shown += async (s, e) =>
                        {
                            try { await action(dialog, registry, data); }
                            catch (Exception ex) { failure = ex; }
                            finally { if (!dialog.IsDisposed) dialog.Close(); }
                        };
                        Application.Run(dialog);
                    }
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "界面测试超时 / UI test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
