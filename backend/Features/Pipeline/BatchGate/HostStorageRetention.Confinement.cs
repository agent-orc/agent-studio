using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Pipeline;

public static partial class HostStorageRetention
{
    /// <summary>
    /// Linux open handles follow renamed directories. Landlock checks the live
    /// hierarchy at each mutation, including unlinkat through an existing handle.
    /// Restrict a dedicated thread, never a host or thread-pool thread. If the
    /// kernel cannot enforce this boundary, retain state without deleting it.
    /// </summary>
    internal sealed class InstanceDeletionWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _requests = new();
        private readonly Thread? _thread;
        public bool Available { get; private set; }

        public InstanceDeletionWorker(int[] descriptors)
        {
            if (!OperatingSystem.IsLinux())
            {
                Available = true;
                return;
            }
            var ready = new object();
            var initialized = false;
            _thread = new Thread(() =>
            {
                var available = descriptors.Length > 0 && descriptors.All(Confine);
                lock (ready)
                {
                    Available = available;
                    initialized = true;
                    Monitor.Pulse(ready);
                }
                if (!available) return;
                foreach (var request in _requests.GetConsumingEnumerable()) request();
            }) { IsBackground = true, Name = "Instance retention deletion" };
            lock (ready)
            {
                _thread.Start();
                while (!initialized) Monitor.Wait(ready);
            }
        }

        public void Run(Action delete)
        {
            if (!Available) throw new IOException("Instance deletion confinement is unavailable.");
            if (_thread is null)
            {
                delete();
                return;
            }
            ExceptionDispatchInfo? failure = null;
            var completion = new object();
            var completed = false;
            lock (completion)
            {
                _requests.Add(() =>
                {
                    try { delete(); }
                    catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                    finally
                    {
                        lock (completion)
                        {
                            completed = true;
                            Monitor.Pulse(completion);
                        }
                    }
                });
                while (!completed) Monitor.Wait(completion);
            }
            failure?.Throw();
        }

        public void Dispose()
        {
            _requests.CompleteAdding();
            _thread?.Join();
            _requests.Dispose();
        }

        private static bool Confine(int descriptor)
        {
            // Layer each pinned ancestor so moving the anchor outside one of
            // its authorized parents also loses removal rights. Exceeding the
            // kernel's layer limit fails closed, just like missing Landlock.
            // Landlock ABI 1: REMOVE_DIR | REMOVE_FILE. This ABI also denies
            // reparenting; claims only rename within the same parent directory.
            const ulong removal = (1UL << 4) | (1UL << 5);
            var handled = removal;
            var ruleset = CreateRuleset(444, ref handled, (nuint)sizeof(ulong), 0);
            if (ruleset < 0) return false;
            using var handle = new SafeFileHandle((nint)ruleset, ownsHandle: true);
            var rule = new PathBeneath { AllowedAccess = removal, Parent = descriptor };
            return AddRule(445, (int)ruleset, 1, ref rule, 0) == 0
                && Prctl(38, 1, 0, 0, 0) == 0 // PR_SET_NO_NEW_PRIVS
                && RestrictSelf(446, (int)ruleset, 0) == 0;
        }

        // The UAPI structure is packed, including on 64-bit architectures.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PathBeneath
        {
            public ulong AllowedAccess;
            public int Parent;
        }

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern nint CreateRuleset(nint number, ref ulong attributes, nuint size, uint flags);

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern nint AddRule(nint number, int ruleset, int type, ref PathBeneath rule, uint flags);

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern nint RestrictSelf(nint number, int ruleset, uint flags);

        [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
        private static extern int Prctl(int option, nuint value, nuint arg3, nuint arg4, nuint arg5);
    }
}
