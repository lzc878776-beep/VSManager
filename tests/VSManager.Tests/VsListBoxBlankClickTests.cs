using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>点击列表末尾下方空白处不应选中最后一项。/ Clicking the blank area below the last item must not select it.</summary>
    [TestClass]
    [TestCategory(TestKind.Ui)]
    public class VsListBoxBlankClickTests
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private static IntPtr Lp(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        private static void Sta(Action test)
        {
            Exception error = null;
            var thread = new Thread(() => { try { test(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw new AssertFailedException(error.ToString());
        }

        [TestMethod]
        public void BlankClick_KeepsOrClearsSelection_NeverSelectsLastItem()
        {
            Sta(() =>
            {
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(240, 400), ShowInTaskbar = false })
                {
                    var list = new VsListBox { Dock = DockStyle.Fill, ItemHeight = 20 };
                    list.Items.AddRange(new object[] { "a", "b", "c" });
                    form.Controls.Add(list);
                    form.Show();
                    Application.DoEvents();
                    int blankY = list.GetItemRectangle(2).Bottom + 40;
                    Assert.IsTrue(blankY < list.ClientSize.Height);
                    Assert.AreEqual(-1, list.ItemIndexAt(new Point(10, blankY)));
                    Assert.AreEqual(1, list.ItemIndexAt(new Point(10, list.GetItemRectangle(1).Top + 2)));

                    list.SelectedIndex = 0;
                    SendMessage(list.Handle, 0x0201, (IntPtr)1, Lp(10, blankY));
                    SendMessage(list.Handle, 0x0202, IntPtr.Zero, Lp(10, blankY));
                    Application.DoEvents();
                    Assert.AreEqual(0, list.SelectedIndex, "左键空白处保持原选中 / left blank click keeps selection");

                    SendMessage(list.Handle, 0x0204, (IntPtr)2, Lp(10, blankY));
                    SendMessage(list.Handle, 0x0205, IntPtr.Zero, Lp(10, blankY));
                    Application.DoEvents();
                    Assert.AreEqual(-1, list.SelectedIndex, "右键空白处清除选中 / right blank click clears selection");

                    int itemY = list.GetItemRectangle(1).Top + 2;
                    SendMessage(list.Handle, 0x0201, (IntPtr)1, Lp(10, itemY));
                    SendMessage(list.Handle, 0x0202, IntPtr.Zero, Lp(10, itemY));
                    Application.DoEvents();
                    Assert.AreEqual(1, list.SelectedIndex, "点击条目照常选中 / clicking an item still selects it");
                }
            });
        }
    }
}