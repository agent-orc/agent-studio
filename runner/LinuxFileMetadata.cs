using System.Runtime.InteropServices;

namespace AgentRunner;

// The statx ABI uses fixed offsets across Linux architectures.
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct LinuxFileMetadata
{
    [FieldOffset(0)] public uint Mask;
    [FieldOffset(20)] public uint Owner;
    [FieldOffset(24)] public uint Group;
    [FieldOffset(28)] public ushort Mode;
    [FieldOffset(32)] public ulong Inode;
    [FieldOffset(136)] public uint DeviceMajor;
    [FieldOffset(140)] public uint DeviceMinor;
    [FieldOffset(144)] public ulong MountId;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    internal static extern int Read(int descriptor, string path, int flags, uint mask, out LinuxFileMetadata metadata);
}
