using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class MemoryRetentionTests
    {
        private TempDataFolder _data;
        private Func<Tuple<int, int>> _limits;
        private string _path;

        [TestInitialize]
        public void Initialize()
        {
            _path = (string)typeof(AgentChatLog).GetField("_path", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _data = new TempDataFolder();
            _limits = AgentChatLog.Limits;
            AgentChatLog.FilePath = _data.File("history.jsonl");
        }

        [TestCleanup]
        public void Cleanup()
        {
            AgentChatLog.Limits = _limits;
            AgentChatLog.FilePath = _path;
            _data.Dispose();
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void StreamingTrim_PreservesFirstRecordWithOrWithoutBom(bool bom)
        {
            string first = Record(DateTime.Now, "first");
            string second = Record(DateTime.Now.AddMinutes(-1), "second");
            Write(new[] { first, "invalid", "  ", second }, bom);
            AgentChatLog.Limits = () => Tuple.Create(0, 10);
            Assert.AreEqual(0, AgentChatLog.Trim());
            CollectionAssert.AreEqual(new[] { first, second }, File.ReadAllLines(AgentChatLog.FilePath));
            Assert.IsNull(AgentChatLog.LastError);
        }

        [TestMethod]
        public void StreamingTrim_AppliesDateAndCountWithoutReorderingRecords()
        {
            string first = Record(DateTime.Now.AddDays(-1), "first");
            string old = Record(DateTime.Now.AddDays(-10), "expired");
            string second = Record(DateTime.Now, "second");
            string third = Record(DateTime.Now.AddDays(-2), "clock moved back");
            Write(new[] { first, old, second, third });
            AgentChatLog.Limits = () => Tuple.Create(3, 2);
            Assert.AreEqual(2, AgentChatLog.Trim());
            CollectionAssert.AreEqual(new[] { second, third }, File.ReadAllLines(AgentChatLog.FilePath));
        }

        [TestMethod]
        public void StreamingTrim_DateOnlyKeepsEveryEligibleRecord()
        {
            var recent = Enumerable.Range(0, 20).Select(i => Record(DateTime.Now.AddMinutes(-i), "record" + i)).ToArray();
            Write(new[] { Record(DateTime.Now.AddDays(-10), "expired") }.Concat(recent));
            AgentChatLog.Limits = () => Tuple.Create(3, 0);
            Assert.AreEqual(1, AgentChatLog.Trim());
            CollectionAssert.AreEqual(recent, File.ReadAllLines(AgentChatLog.FilePath));
        }

        [TestMethod]
        public void StreamingTrim_NoChangesDoesNotRewriteTheFile()
        {
            Write(new[] { Record(DateTime.Now, "kept"), "" }, true);
            var original = File.ReadAllBytes(AgentChatLog.FilePath);
            var timestamp = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(AgentChatLog.FilePath, timestamp);
            timestamp = File.GetLastWriteTimeUtc(AgentChatLog.FilePath);
            AgentChatLog.Limits = () => Tuple.Create(3, 20);
            Assert.AreEqual(0, AgentChatLog.Trim());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(AgentChatLog.FilePath));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(AgentChatLog.FilePath));
            Assert.IsFalse(File.Exists(AgentChatLog.FilePath + ".tmp"));
        }

        [TestMethod]
        public void StreamingTrim_DisabledLeavesExpiredAndDamagedRecordsUntouched()
        {
            Write(new[] { Record(DateTime.Now.AddDays(-100), "kept"), "invalid" });
            var original = File.ReadAllBytes(AgentChatLog.FilePath);
            AgentChatLog.Limits = () => Tuple.Create(0, 0);
            Assert.AreEqual(0, AgentChatLog.Trim());
            CollectionAssert.AreEqual(original, File.ReadAllBytes(AgentChatLog.FilePath));
        }

        [TestMethod]
        public void StreamingTrim_LockedSourceReportsFailureAndKeepsOriginal()
        {
            Write(new[] { Record(DateTime.Now, "one"), Record(DateTime.Now, "two") });
            var original = File.ReadAllBytes(AgentChatLog.FilePath);
            AgentChatLog.Limits = () => Tuple.Create(0, 1);
            using (var file = new FileStream(AgentChatLog.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.AreEqual(0, AgentChatLog.Trim());
                Assert.IsFalse(string.IsNullOrEmpty(AgentChatLog.LastError));
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(AgentChatLog.FilePath));
            Assert.IsFalse(File.Exists(AgentChatLog.FilePath + ".tmp"));
        }

        [TestMethod]
        public void StreamingTrim_LargeHistoryKeepsOnlyRequestedTailAndUnknownFields()
        {
            const int count = 4096;
            var now = DateTime.Now;
            string body = new string('x', 4096);
            using (var writer = new StreamWriter(AgentChatLog.FilePath, false, new UTF8Encoding(false)))
                for (int i = 0; i < count; i++) writer.WriteLine(Record(now, body + i));
            AgentChatLog.Limits = () => Tuple.Create(0, 3);
            Assert.AreEqual(count - 3, AgentChatLog.Trim());
            CollectionAssert.AreEqual(Enumerable.Range(count - 3, 3).Select(i => Record(now, body + i)).ToArray(),
                File.ReadAllLines(AgentChatLog.FilePath));
        }

        [TestMethod]
        public void ClosedVsCachesAreReleased_WithoutEvictingLiveInstances()
        {
            var chat = new CopilotChat(() => new AppSettings());
            string[] caches = { "_panes", "_hosts", "_missUntil", "_tailSig", "_handledDialogs", "_sync" };
            foreach (string name in caches)
            {
                var cache = (IDictionary)Field(name).GetValue(chat);
                var type = cache.GetType().GetGenericArguments()[1];
                object value = type == typeof(string) ? "signature" : type.IsValueType ? Activator.CreateInstance(type)
                    : name == "_sync" ? Activator.CreateInstance(type, true) : null;
                cache.Add(12, value);
                cache.Add(123, value);
            }
            var messages = (IDictionary)Field("_msgCache").GetValue(chat);
            var kept = new ChatMessage();
            messages.Add("12|0|closed|1", new ChatMessage());
            messages.Add("123|0|live|1", kept);
            Field("_lastVs").SetValue(chat, new VsInstance { Pid = 12 });
            Field("_lastQuick").SetValue(chat, new string('x', 100000));

            chat.PruneCaches(new HashSet<int> { 123 });
            foreach (string name in caches)
            {
                var cache = (IDictionary)Field(name).GetValue(chat);
                Assert.AreEqual(1, cache.Count, name);
                Assert.IsTrue(cache.Contains(123), name);
            }
            Assert.AreEqual(1, messages.Count);
            Assert.AreSame(kept, messages["123|0|live|1"]);
            Assert.IsNull(Field("_lastVs").GetValue(chat));
            Assert.IsNull(Field("_lastQuick").GetValue(chat));

            chat.PruneCaches(new HashSet<int>());
            foreach (string name in caches) Assert.AreEqual(0, ((IDictionary)Field(name).GetValue(chat)).Count, name);
            Assert.AreEqual(0, messages.Count);
        }

        private static FieldInfo Field(string name) => typeof(CopilotChat).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static string Record(DateTime time, string text) => "{\"time\":\"" + time.ToString("o", CultureInfo.InvariantCulture)
            + "\",\"role\":\"assistant\",\"text\":\"" + text + "\",\"futureField\":123}";

        private static void Write(IEnumerable<string> lines, bool bom = false) =>
            File.WriteAllLines(AgentChatLog.FilePath, lines, new UTF8Encoding(bom));
    }
}
