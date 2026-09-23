using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    /// <summary>tasks.json 的读写兼容性与原子写入。/ tasks.json read/write compatibility and atomic writes.</summary>
    [TestClass]
    public class JsonTaskStoreTests
    {
        private TempDataFolder _data;
        private string _path;

        [TestInitialize]
        public void Init() { _data = new TempDataFolder(); _path = _data.File("tasks.json"); }

        [TestCleanup]
        public void Cleanup() => _data.Dispose();

        private static QueuedTask T(int id, string status) => new QueuedTask
        {
            Id = id, VsKey = @"%USERPROFILE%\src\Demo\Demo.sln", VsName = "Demo", Text = "任务 " + id, Source = "AI", Status = status,
            Created = new DateTime(2026, 1, 1, 8, 0, 0), Attempts = 1, Result = status == QueueStatus.Done ? "ok" : null,
            Finished = QueueStatus.Active(status) ? (DateTime?)null : new DateTime(2026, 1, 1, 8, 5, 0)
        };

        [TestMethod]
        public void SaveThenLoad_RoundTripsAllStatuses()
        {
            var store = new JsonTaskStore(_path);
            var items = new[] { QueueStatus.Waiting, QueueStatus.Running, QueueStatus.Done, QueueStatus.Failed, QueueStatus.Cancelled }
                .Select((s, i) => T(i + 1, s)).ToList();
            Assert.IsNull(store.Save(items));
            var problems = new List<string>();
            var back = store.Load(problems);
            Assert.AreEqual(0, problems.Count);
            Assert.AreEqual(5, back.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(items[i].Id, back[i].Id);
                Assert.AreEqual(items[i].Status, back[i].Status);
                Assert.AreEqual(items[i].Text, back[i].Text);
                Assert.AreEqual(items[i].Created, back[i].Created);
                Assert.AreEqual(items[i].Finished, back[i].Finished);
                Assert.AreEqual(items[i].Result, back[i].Result);
            }
        }

        [TestMethod]
        public void Save_KeepsFieldNames_AndBackup()
        {
            var store = new JsonTaskStore(_path);
            store.Save(new[] { T(1, QueueStatus.Waiting) });
            store.Save(new[] { T(1, QueueStatus.Done) });
            string json = File.ReadAllText(_path);
            foreach (var f in new[] { "\"Id\"", "\"VsKey\"", "\"VsName\"", "\"Text\"", "\"Source\"", "\"Status\"", "\"Created\"", "\"Started\"", "\"Finished\"", "\"Result\"", "\"Error\"", "\"Attempts\"" })
                StringAssert.Contains(json, f);
            Assert.IsFalse(json.Contains("SawBusy") || json.Contains("NextTry"), "运行期字段不落盘 / runtime fields are not saved");
            StringAssert.Contains(File.ReadAllText(_path + ".bak"), "\"waiting\"");
            Assert.IsFalse(File.Exists(_path + ".tmp"));
        }

        [TestMethod]
        public void Load_ToleratesOldAndPartialRecords()
        {
            File.WriteAllText(_path,
                "[{\"Id\":1,\"Text\":\"旧格式\",\"Status\":\"done\",\"Created\":\"\\/Date(1767225600000+0800)\\/\"}," +
                "{\"Id\":2,\"VsKey\":\"src\\\\Demo\\\\Demo.sln\",\"Text\":\"ISO 日期\",\"Status\":\"waiting\",\"Created\":\"2026-01-01T08:00:00\"}," +
                "{\"Id\":3,\"Status\":\"done\"}," +
                "{\"Id\":4,\"Text\":\"未知状态\",\"Status\":\"paused\",\"Attempts\":\"abc\"}]",
                new UTF8Encoding(true));
            var problems = new List<string>();
            var items = new JsonTaskStore(_path).Load(problems);
            CollectionAssert.AreEqual(new[] { 1, 2, 4 }, items.Select(t => t.Id).ToArray());
            Assert.AreEqual("（未知 VS）", items[0].VsName);
            Assert.AreEqual(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(1767225600000).ToLocalTime(), items[0].Created);
            Assert.IsTrue(items[0].Finished.HasValue, "已结束任务补齐结束时间 / finished time filled in");
            Assert.AreEqual("Demo", items[1].VsName);
            Assert.AreEqual(QueueStatus.Cancelled, items[2].Status);
            StringAssert.Contains(items[2].Error, "paused");
            Assert.AreEqual(0, items[2].Attempts);
            Assert.IsTrue(problems.Any(p => p.Contains("缺少任务内容")));
        }

        [TestMethod]
        public void Load_TruncatedFile_KeepsEarlierRecords_AndUsesBackup()
        {
            var store = new JsonTaskStore(_path);
            store.Save(new[] { T(1, QueueStatus.Done), T(2, QueueStatus.Done), T(3, QueueStatus.Waiting) });
            store.Save(new[] { T(1, QueueStatus.Done), T(2, QueueStatus.Done) }); // .bak 含 1..3 / .bak holds 1..3
            string json = File.ReadAllText(_path);
            File.WriteAllText(_path, json.Substring(0, json.IndexOf("任务 2", StringComparison.Ordinal)));
            var problems = new List<string>();
            var items = store.Load(problems);
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, items.Select(t => t.Id).ToArray());
            Assert.IsTrue(problems[0].StartsWith("tasks.json 读取不完整"));
            Assert.IsTrue(problems.Any(p => p.Contains("tasks.json.bak")));
            Assert.IsTrue(Directory.GetFiles(_data.Path, "tasks.corrupt-*.json").Length == 1, "保留损坏的原文件 / damaged file kept");
        }

        [TestMethod]
        public void Load_MissingFiles_ReturnsEmpty()
        {
            var problems = new List<string>();
            Assert.AreEqual(0, new JsonTaskStore(_path).Load(problems).Count);
            Assert.AreEqual(0, problems.Count);
        }

        [TestMethod]
        public void AtomicFile_FallsBackToOverwrite_WhenReplaceFails()
        {
            File.WriteAllText(_path, "old");
            var fs = new ReplaceFailsFileSystem();
            var r = AtomicFile.Write(_path, s => { var b = Encoding.UTF8.GetBytes("new"); s.Write(b, 0, b.Length); }, true, true, fs);
            Assert.IsTrue(r.Ok);
            Assert.IsTrue(r.FallbackSucceeded);
            Assert.AreEqual("new", File.ReadAllText(_path));
            Assert.AreEqual("old", File.ReadAllText(_path + ".bak"));
            Assert.IsFalse(File.Exists(_path + ".tmp"));
        }

        [TestMethod]
        public void AtomicFile_SerializationError_DoesNotOverwrite()
        {
            File.WriteAllText(_path, "old");
            var r = AtomicFile.Write(_path, s => { s.WriteByte(1); throw new System.Runtime.Serialization.SerializationException("bad"); }, true, true);
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("old", File.ReadAllText(_path));
        }

        [TestMethod]
        public void Store_ReportsError_WhenEverythingFails()
        {
            var store = new JsonTaskStore(_path, new ReplaceFailsFileSystem { CopyFails = true });
            File.WriteAllText(_path, "[]");
            string err = store.Save(new[] { T(1, QueueStatus.Waiting) });
            StringAssert.StartsWith(err, "IOException：replace failed");
            StringAssert.Contains(err, "；直接覆盖也失败：copy failed");
            Assert.AreEqual("[]", File.ReadAllText(_path), "原文件不被破坏 / original file untouched");
        }

        private sealed class ReplaceFailsFileSystem : IFileSystem
        {
            public bool CopyFails;
            private readonly PhysicalFileSystem _real = PhysicalFileSystem.Instance;
            public bool FileExists(string path) => _real.FileExists(path);
            public byte[] ReadAllBytes(string path) => _real.ReadAllBytes(path);
            public Stream Create(string path) => _real.Create(path);
            public void Replace(string source, string destination, string backup) => throw new IOException("replace failed");
            public void Move(string source, string destination) => _real.Move(source, destination);
            public void Copy(string source, string destination, bool overwrite)
            {
                if (CopyFails) throw new IOException("copy failed");
                _real.Copy(source, destination, overwrite);
            }
            public void Delete(string path) => _real.Delete(path);
            public void CreateDirectory(string path) => _real.CreateDirectory(path);
            public void AppendAllText(string path, string text, Encoding encoding) => _real.AppendAllText(path, text, encoding);
        }
    }
}
