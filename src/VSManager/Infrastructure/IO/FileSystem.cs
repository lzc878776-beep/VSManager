using System.IO;
using System.Text;

namespace VSManager
{
    /// <summary>
    /// 文件系统抽象：任务清单等存储通过它读写磁盘，单元测试可替换为模拟实现（例如模拟磁盘已满、无权限）。
    /// File-system abstraction used by stores such as the task list; unit tests can replace it with a fake
    /// (for example to simulate a full disk or missing permissions).
    /// </summary>
    public interface IFileSystem
    {
        bool FileExists(string path);
        byte[] ReadAllBytes(string path);
        /// <summary>创建（或截断）文件并返回可写流。/ Creates (or truncates) a file and returns a writable stream.</summary>
        Stream Create(string path);
        void Replace(string source, string destination, string backup);
        void Move(string source, string destination);
        void Copy(string source, string destination, bool overwrite);
        void Delete(string path);
        void CreateDirectory(string path);
        void AppendAllText(string path, string text, Encoding encoding);
    }

    /// <summary>真实磁盘实现（默认）。/ Real disk implementation (the default).</summary>
    public sealed class PhysicalFileSystem : IFileSystem
    {
        public static readonly PhysicalFileSystem Instance = new PhysicalFileSystem();

        public bool FileExists(string path) => File.Exists(path);
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public Stream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup, true);
        public void Move(string source, string destination) => File.Move(source, destination);
        public void Copy(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void Delete(string path) => File.Delete(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void AppendAllText(string path, string text, Encoding encoding) => File.AppendAllText(path, text, encoding);
    }
}
