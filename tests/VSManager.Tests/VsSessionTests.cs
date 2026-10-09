using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>VS 会话记录：序列化往返与打开计划。/ VS session record: serialization round trip and open planning.</summary>
    [TestClass]
    public class VsSessionTests
    {
        private static VsSessionEntry Entry(string path, string name = "A") => new VsSessionEntry { SolutionPath = path, Name = name };

        [TestMethod]
        public void SaveAndLoad_RoundTripsEntriesAndLayout()
        {
            string file = Path.Combine(Path.GetTempPath(), "vsm-session-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var record = new VsSessionRecord
                {
                    SavedUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    DisplaySignature = "1:0,0,1920,1080:0,0,1920,1040:True",
                    Items = new List<VsSessionEntry>
                    {
                        new VsSessionEntry { SolutionPath = @"C:\Src\A.sln", Name = "A", Maximized = true,
                            Main = new VsSessionRect { X = 0, Y = 0, W = 1920, H = 1040 }, Copilot = new VsSessionRect { X = 1920, Y = 0, W = 800, H = 1000 } },
                        new VsSessionEntry { SolutionPath = null, Name = "empty" },
                    },
                };
                Assert.IsNull(VsSessionStore.Save(record, file));
                var back = VsSessionStore.Load(file);
                Assert.IsNotNull(back);
                Assert.AreEqual(record.DisplaySignature, back.DisplaySignature);
                Assert.AreEqual(2, back.Items.Count);
                Assert.IsTrue(back.Items[0].Maximized);
                Assert.AreEqual(new System.Drawing.Rectangle(1920, 0, 800, 1000), back.Items[0].Copilot.ToRectangle());
                Assert.IsNull(back.Items[0].Output);
                Assert.IsTrue(back.Items[0].HasPanes);
                Assert.IsFalse(back.Items[1].HasSolution);
                Assert.IsNull(back.Items[1].Main);
                StringAssert.Contains(VsSessionStore.Describe(back), "A.sln");
            }
            finally { File.Delete(file); }
        }

        [TestMethod]
        public void Load_MissingOrCorrupt_ReturnsNull()
        {
            string file = Path.Combine(Path.GetTempPath(), "vsm-session-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.IsNull(VsSessionStore.Load(file));
            try
            {
                File.WriteAllText(file, "{ not json");
                Assert.IsNull(VsSessionStore.Load(file));
            }
            finally { File.Delete(file); }
        }

        [TestMethod]
        public void MatchOpen_PairsDuplicateSolutionsInOrder()
        {
            var entries = new[] { Entry(@"C:\Src\A.sln"), Entry(@"C:\Src\A.sln"), Entry(@"C:\Src\B.sln"), Entry(null) };
            var open = new[] { @"c:\src\a.sln", @"C:\Src\C.sln" };
            CollectionAssert.AreEqual(new[] { 0, -1, -1, -1 }, VsSessionStore.MatchOpen(entries, open));
            CollectionAssert.AreEqual(new[] { 1, 2 }, VsSessionStore.ToLaunch(entries, open));
        }

        [TestMethod]
        public void ToLaunch_AllOpen_LaunchesNothing()
        {
            var entries = new[] { Entry(@"C:\Src\A.sln"), Entry(@"C:\Src\B.sln") };
            Assert.AreEqual(0, VsSessionStore.ToLaunch(entries, new[] { @"C:\Src\B.sln", @"C:\Src\A.sln" }).Count);
            Assert.AreEqual(2, VsSessionStore.ToLaunch(entries, new string[0]).Count);
        }

        [TestMethod]
        public void Rect_From_EmptyIsNull()
        {
            Assert.IsNull(VsSessionRect.From(System.Drawing.Rectangle.Empty));
            Assert.AreEqual(new System.Drawing.Rectangle(-5, 3, 10, 20), VsSessionRect.From(new System.Drawing.Rectangle(-5, 3, 10, 20)).ToRectangle());
        }
    }
}
