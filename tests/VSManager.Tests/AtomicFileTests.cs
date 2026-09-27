using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSManager.Tests
{
    [TestClass]
    public class AtomicFileTests
    {
        private const string Path = @"store\tasks.json";
        private TempDataFolder _data;
        [TestInitialize] public void Init() => _data = new TempDataFolder();
        [TestCleanup] public void Cleanup() => _data.Dispose();

        private static AtomicWriteResult Write(MemoryAtomicFileSystem fs, bool backup = true) =>
            AtomicFile.Write(Path, stream =>
            {
                var bytes = Encoding.UTF8.GetBytes("new");
                stream.Write(bytes, 0, bytes.Length);
            }, backup, true, fs);

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FallbackCommitted_DeleteFailureRemainsSuccessAndIsLogged(bool backup)
        {
            var fs = new MemoryAtomicFileSystem { FailReplace = true, FailDelete = true };
            fs.Put(Path, "old");
            var result = Write(fs, backup);
            Assert.IsTrue(result.Ok);
            Assert.IsTrue(result.FallbackSucceeded);
            Assert.IsNotNull(result.Error);
            Assert.IsNull(result.FallbackError);
            Assert.IsNotNull(result.CleanupError);
            Assert.AreEqual("new", fs.Text(Path));
            Assert.AreEqual("new", fs.Text(Path + ".tmp"));
            Assert.AreEqual(backup, fs.FileExists(Path + ".bak"));
            if (backup) Assert.AreEqual("old", fs.Text(Path + ".bak"));
            string log = File.ReadAllText(AppLog.PathOf(AppLog.TasksFile));
            StringAssert.Contains(log, "fallback overwrite committed");
            StringAssert.Contains(log, "temporary-file cleanup failed");
            StringAssert.Contains(log, "delete blocked");
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void PrimaryCommit_ReplaceOrMoveConsumesTempWithoutCleanup(bool existing)
        {
            var fs = new MemoryAtomicFileSystem { FailDelete = true };
            if (existing) fs.Put(Path, "old");
            var result = Write(fs);
            Assert.IsTrue(result.Ok);
            Assert.IsFalse(result.FallbackSucceeded);
            Assert.IsNull(result.Error);
            Assert.IsNull(result.CleanupError);
            Assert.AreEqual("new", fs.Text(Path));
            Assert.IsFalse(fs.FileExists(Path + ".tmp"));
            Assert.AreEqual(0, fs.DeleteCalls);
            Assert.AreEqual(existing, fs.FileExists(Path + ".bak"));
            if (existing) Assert.AreEqual("old", fs.Text(Path + ".bak"));
        }

        [TestMethod]
        public void MoveFailure_FallbackCommitsAndCleanupFailureIsNonfatal()
        {
            var fs = new MemoryAtomicFileSystem { FailMove = true, FailDelete = true };
            var result = Write(fs);
            Assert.IsTrue(result.Ok);
            Assert.IsTrue(result.FallbackSucceeded);
            Assert.IsNotNull(result.CleanupError);
            Assert.AreEqual("new", fs.Text(Path));
            Assert.IsFalse(fs.FileExists(Path + ".bak"));
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FailureBeforeCommit_BackupOrOverwriteStillFails(bool backupFailure)
        {
            var fs = new MemoryAtomicFileSystem { FailReplace = true,
                FailBackup = backupFailure, FailCopy = !backupFailure };
            fs.Put(Path, "old");
            var result = Write(fs);
            Assert.IsFalse(result.Ok);
            Assert.IsFalse(result.FallbackSucceeded);
            Assert.IsNotNull(result.FallbackError);
            Assert.IsNull(result.CleanupError);
            Assert.AreEqual("old", fs.Text(Path));
            Assert.AreEqual(0, fs.DeleteCalls);
        }

        [TestMethod]
        public void SerializationFailure_DoesNotCommitPartialTemp()
        {
            var fs = new MemoryAtomicFileSystem();
            fs.Put(Path, "old");
            var result = AtomicFile.Write(Path, stream =>
            {
                stream.WriteByte(1);
                throw new SerializationException("序列化失败 / Serialization failed");
            }, true, true, fs);
            Assert.IsFalse(result.Ok);
            Assert.IsFalse(result.FallbackSucceeded);
            Assert.AreEqual("old", fs.Text(Path));
            Assert.IsFalse(fs.FileExists(Path + ".bak"));
        }
    }

    // 故障发生在操作提交前，删除故障用于验证提交后的清理边界。/ Fail operations before commit; deletion faults exercise post-commit cleanup.
    internal sealed class MemoryAtomicFileSystem : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public bool FailReplace, FailMove, FailBackup, FailCopy, FailDelete;
        public int DeleteCalls;
        public void Put(string path, string text) => _files[path] = Encoding.UTF8.GetBytes(text);
        public string Text(string path) => Encoding.UTF8.GetString(_files[path]);
        public bool FileExists(string path) => _files.ContainsKey(path);
        public byte[] ReadAllBytes(string path) => (byte[])_files[path].Clone();
        public Stream Create(string path) => new SavedStream(bytes => _files[path] = bytes);
        public void Replace(string source, string destination, string backup)
        {
            if (FailReplace) throw new IOException("替换失败 / replace blocked");
            _files[backup] = ReadAllBytes(destination);
            _files[destination] = ReadAllBytes(source);
            _files.Remove(source);
        }
        public void Move(string source, string destination)
        {
            if (FailMove) throw new IOException("移动失败 / move blocked");
            _files[destination] = ReadAllBytes(source);
            _files.Remove(source);
        }
        public void Copy(string source, string destination, bool overwrite)
        {
            if (destination.EndsWith(".bak", StringComparison.Ordinal) ? FailBackup : FailCopy)
                throw new IOException("复制失败 / copy blocked");
            _files[destination] = ReadAllBytes(source);
        }
        public void Delete(string path)
        {
            DeleteCalls++;
            if (FailDelete) throw new IOException("删除失败 / delete blocked");
            _files.Remove(path);
        }
        public void CreateDirectory(string path) { }
        public void AppendAllText(string path, string text, Encoding encoding) =>
            Put(path, (FileExists(path) ? Text(path) : "") + text);
        private sealed class SavedStream : MemoryStream
        {
            private readonly Action<byte[]> _save;
            public SavedStream(Action<byte[]> save) => _save = save;
            protected override void Dispose(bool disposing)
            {
                if (disposing) _save(ToArray());
                base.Dispose(disposing);
            }
        }
    }
}
