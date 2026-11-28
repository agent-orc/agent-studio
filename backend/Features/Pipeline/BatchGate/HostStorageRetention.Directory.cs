using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Pipeline;

public static partial class HostStorageRetention
{
    /// <summary>
    /// Anchors instance inspection and nonrecursive removal to open directories.
    /// Linux uses no-follow openat and unlinkat and checks the pinned locations
    /// before removal; Windows denies directory rename while a handle is open.
    /// </summary>
    private sealed class PinnedRetentionDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly PinnedRetentionDirectory? _parent;
        private readonly bool _ownsParent;
        private readonly string _name;
        private readonly string _absolutePath;
        public string IoPath { get; }
        public DateTime LastWriteTimeUtc => File.GetLastWriteTimeUtc(_handle);
        private int Descriptor => _handle.DangerousGetHandle().ToInt32();

        private PinnedRetentionDirectory(PinnedRetentionDirectory? parent, string name,
            bool ownsParent = false)
        {
            _parent = parent;
            _name = name;
            _ownsParent = ownsParent;
            _absolutePath = parent is null ? name : Path.Combine(parent._absolutePath, name);
            if (OperatingSystem.IsWindows())
            {
                IoPath = parent is null ? name : Path.Combine(parent.IoPath, name);
                // FILE_READ_ATTRIBUTES, OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
                _handle = CreateFile(IoPath, 0x80, FileShare.ReadWrite, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            }
            else if (OperatingSystem.IsLinux())
            {
                var (directory, noFollow) = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 or Architecture.X86 => (0x10000, 0x20000),
                    Architecture.Arm or Architecture.Arm64 => (0x4000, 0x8000),
                    _ => throw new PlatformNotSupportedException("Instance retention directory ABI is unsupported."),
                };
                // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, relative to a pinned parent.
                var descriptor = OpenAt(parent?.Descriptor ?? -100, name, directory | noFollow | 0x80000);
                _handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
                IoPath = "/proc/self/fd/" + descriptor;
            }
            else throw new PlatformNotSupportedException("Safe instance retention is supported on Linux and Windows.");
            try
            {
                if (_handle.IsInvalid)
                    throw Failure("open");
                if ((File.GetAttributes(_handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                    != FileAttributes.Directory)
                    throw new IOException("Linked or invalid instance retention directory.");
            }
            catch { _handle.Dispose(); throw; }
        }

        public static PinnedRetentionDirectory OpenRoot(string path)
        {
            // Pin the entire chain, including ancestors of the configured state root.
            // An absolute open with O_NOFOLLOW protects only its final component.
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var current = new PinnedRetentionDirectory(null, root);
            try
            {
                foreach (var name in full[root.Length..].Split(Path.DirectorySeparatorChar,
                             StringSplitOptions.RemoveEmptyEntries))
                    current = new PinnedRetentionDirectory(current, name, ownsParent: true);
                return current;
            }
            catch { current.Dispose(); throw; }
        }

        public PinnedRetentionDirectory OpenChild(string name)
        {
            if (name is "" or "." or ".." || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                throw new IOException("Invalid instance retention entry name.");
            return new PinnedRetentionDirectory(this, name);
        }

        // A Linux descriptor pins an inode, not its location. In particular,
        // unlinkat on a moved candidate would still remove its metadata and
        // descendants. Check every pinned ancestor before each mutation so a
        // detected move aborts the entire candidate without touching its contents.
        public void EnsureUnmoved()
        {
            if (!OperatingSystem.IsLinux()) return;
            _parent?.EnsureUnmoved();
            if (!string.Equals(new DirectoryInfo(IoPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName,
                    _absolutePath, StringComparison.Ordinal))
                throw new IOException("Instance retention directory moved after inspection.");
        }

        public void DeleteOrigin()
        {
            EnsureUnmoved();
            if (OperatingSystem.IsWindows()) File.Delete(Path.Combine(IoPath, "batch-state-origin"));
            else if (UnlinkAt(Descriptor, "batch-state-origin", 0) != 0) throw Failure("unlink metadata");
        }

        public void DeleteEmpty()
        {
            if (_parent is null) throw new IOException("Cannot remove the retention anchor.");
            // On Windows the parent and all its ancestors remain pinned. Releasing
            // only the leaf permits removal; a substituted leaf link is never followed.
            EnsureUnmoved();
            if (OperatingSystem.IsWindows())
            {
                _handle.Dispose();
                Directory.Delete(Path.Combine(_parent.IoPath, _name));
            }
            else if (UnlinkAt(_parent.Descriptor, _name, 0x200) != 0) throw Failure("unlink directory"); // AT_REMOVEDIR
        }

        public void Dispose()
        {
            _handle.Dispose();
            if (_ownsParent) _parent?.Dispose();
        }

        private static IOException Failure(string operation)
            => new("Instance retention could not " + operation + ".", new Win32Exception(Marshal.GetLastPInvokeError()));

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int directory, string name, int flags);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        private static extern int UnlinkAt(int directory, string name, int flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
