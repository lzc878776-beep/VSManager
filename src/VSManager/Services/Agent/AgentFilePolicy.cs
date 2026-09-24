using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VSManager
{
    internal sealed class AgentFileDeniedException : UnauthorizedAccessException
    {
        internal AgentFileDeniedException(string reason) : base(reason) { }
    }

    /// <summary>本机只读文件边界；权限来自用户设置和登记表。/ Local read-only boundary; grants come only from user settings and the registry.</summary>
    internal sealed class AgentFilePolicy
    {
        private readonly Func<IEnumerable<string>> _roots;
        internal AgentFilePolicy(Func<IEnumerable<string>> roots) { _roots = roots; }

        internal static string Canonical(string path)
        {
            if (path == null || path.Length > 240) throw new AgentFileDeniedException("路径为空或超过 240 字符 / Empty path or path exceeds 240 characters");
            path = Environment.ExpandEnvironmentVariables(path);
            if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Length < 3
                || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\'
                || path.IndexOfAny(new[] { '/', '*', '?', '"', '<', '>', '|', '\0' }) >= 0
                || path.Skip(2).Any(c => c == ':' || char.IsControl(c))) throw new UnauthorizedAccessException();
            foreach (string part in path.Substring(3).Split('\\'))
            {
                if (part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal)
                    || part.EndsWith(" ", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
                string name = part.Split('.')[0].ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(name)
                    || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                    && char.IsDigit(name[3]))) throw new UnauthorizedAccessException();
            }
            string full = Path.GetFullPath(path);
            return full.Length == 3 ? full : full.TrimEnd('\\');
        }

        internal static bool Within(string path, string root) => string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        internal string Require(string path)
        {
            string full = Canonical(path);
            if (Sensitive(full)) throw new AgentFileDeniedException("敏感路径禁止访问，即使位于授权目录内 / Sensitive paths are blocked even within an authorized root");
            bool granted = false;
            foreach (string value in _roots() ?? Enumerable.Empty<string>())
            {
                try { if (Within(full, Canonical(value))) { granted = true; break; } }
                catch (Exception ex) when (ex is ArgumentException || ex is UnauthorizedAccessException || ex is NotSupportedException) { }
            }
            if (!granted) throw new AgentFileDeniedException("路径不在授权根目录内，请在属性中配置授权 / Path is outside authorized roots; configure grants in Settings");
            return full;
        }

        internal static bool Sensitive(string full)
        {
            string[] parts = full.Split('\\');
            var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".ssh", ".aws", ".azure", ".kube", ".gnupg", ".config", ".docker", ".git", ".vs",
                "credentials", "credential", "vault", "protect", "passwords", "tokens", "secrets", "keychains",
                "user data", "profiles", "firefox", "chromium", "google", "bravesoftware", "opera software",
                "microsoftedge", "windows", "programdata", "system volume information", "$recycle.bin"
            };
            if (parts.Any(p => denied.Contains(p))) return true;
            foreach (string part in parts.Skip(1))
            {
                string name = part.ToLowerInvariant();
                string extension = Path.GetExtension(name);
                if (name.StartsWith(".env", StringComparison.Ordinal) || name == ".npmrc" || name == ".pypirc"
                    || name == ".netrc" || name == "_netrc" || name.StartsWith("id_", StringComparison.Ordinal)
                    || name.Contains("credential") || name.Contains("password") || name.Contains("secret") || name.Contains("token")
                    || name == "login data" || name == "cookies" || name == "web data" || name == "key4.db" || name == "logins.json"
                    || name == "settings.json" || name.StartsWith("settings.json.", StringComparison.Ordinal)
                    || name == "agent-chat.jsonl" || name == "agent.log"
                    || name == "file-audit.log" || name.EndsWith(".kubeconfig", StringComparison.Ordinal)
                    || new[] { ".pem", ".key", ".pfx", ".p12", ".p8", ".ppk", ".kdbx", ".jks", ".keystore" }.Contains(extension)) return true;
            }
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(root) && Within(full, root)) return true;
            }
            foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                string root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root) || !Within(full, root)) continue;
                string regularTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
                bool temporaryData = folder == Environment.SpecialFolder.LocalApplicationData && Within(full, regularTemp)
                    && Within(full, Path.GetTempPath().TrimEnd('\\'));
                if (!temporaryData) return true;
            }
            return string.Equals(full, AppSettings.FilePath, StringComparison.OrdinalIgnoreCase);
        }

        // 固定每层目录句柄，拒绝重解析点及硬链接，禁止删除共享以缩小检查/使用竞态。/ Pin every ancestor, reject reparse points and hard links, deny delete sharing to close check/use races.
        internal Lease Open(string path, bool directory)
        {
            string full = Require(path);
            var lease = new Lease();
            try
            {
                string current = Path.GetPathRoot(full);
                OpenComponent(current, true, false, lease);
                string[] parts = full.Substring(current.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    current = Path.Combine(current, parts[i]);
                    bool last = i == parts.Length - 1;
                    OpenComponent(current, !last || directory, last && !directory, lease);
                }
                Require(full);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private static void OpenComponent(string path, bool directory, bool read, Lease lease)
        {
            var handle = CreateFile(path, read ? 0x80000000u : 0u, read ? 1u : 3u, IntPtr.Zero, 3,
                0x00200000u | 0x02000000u, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException(); }
            lease.Handles.Add(handle);
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400u) != 0
                || ((info.Attributes & 0x10u) != 0) != directory || (!directory && info.Links != 1)) throw new UnauthorizedAccessException();
            var final = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity) throw new UnauthorizedAccessException();
            string actual = final.ToString();
            if (!actual.StartsWith("\\\\?\\", StringComparison.Ordinal) || !string.Equals(actual.Substring(4).TrimEnd('\\'),
                path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
            lease.Information = info;
        }

        internal sealed class Lease : IDisposable
        {
            internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
            internal FileInformation Information;
            internal SafeFileHandle Handle => Handles[Handles.Count - 1];
            internal long Length => ((long)Information.SizeHigh << 32) | Information.SizeLow;
            internal DateTime Modified => DateTime.FromFileTimeUtc(((long)Information.WriteTime.High << 32) | Information.WriteTime.Low);
            public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
        }

        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low, High; }
        [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
        {
            public uint Attributes;
            public FileTime CreationTime, AccessTime, WriteTime;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    }
}
