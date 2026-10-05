using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>Grants only the disposable test account the batch-logon right needed by Task Scheduler; removes it on disposal.</summary>
internal sealed class AccountLogonRight : IDisposable
{
    private readonly byte[] _sid;

    public AccountLogonRight(SecurityIdentifier sid)
    {
        _sid = new byte[sid.BinaryLength];
        sid.GetBinaryForm(_sid, 0);
        SetRight(add: true);
    }

    public void Dispose() => SetRight(add: false);

    private void SetRight(bool add)
    {
        var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x810, out var policy));
        try
        {
            const string name = "SeBatchLogonRight";
            var rights = new[] { new UnicodeString { Buffer = name, Length = (ushort)(name.Length * 2), MaximumLength = (ushort)((name.Length + 1) * 2) } };
            Check(add ? LsaAddAccountRights(policy, _sid, rights, 1) : LsaRemoveAccountRights(policy, _sid, false, rights, 1));
        }
        finally
        {
            LsaClose(policy);
        }
    }

    private static void Check(uint status)
    {
        if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        [MarshalAs(UnmanagedType.LPWStr)] public string Buffer;
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint LsaOpenPolicy(IntPtr system, ref ObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, [In] UnicodeString[] rights, uint count);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint LsaRemoveAccountRights(IntPtr policy, byte[] sid, [MarshalAs(UnmanagedType.Bool)] bool allRights, [In] UnicodeString[] rights, uint count);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint LsaNtStatusToWinError(uint status);
}
