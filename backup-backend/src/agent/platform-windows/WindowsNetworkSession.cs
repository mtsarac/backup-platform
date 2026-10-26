using System.Runtime.InteropServices;
using System.Security.Principal;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace BackupPlatform.Agent.Platform.Windows;

public sealed class WindowsNetworkSession : IWindowsNetworkSession
{
    public async Task<T> RunAsync<T>(string userName, string password, string? domain, Func<Task<T>> operation)
    {
        // NEW_CREDENTIALS isolates outbound authentication without changing global share sessions.
        if (!LogonUser(userName, domain, password, 9, 3, out var identity))
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "SMB network authentication failed.");
        using (identity)
            return await WindowsIdentity.RunImpersonatedAsync(identity, operation).ConfigureAwait(false);
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string? domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);
}
