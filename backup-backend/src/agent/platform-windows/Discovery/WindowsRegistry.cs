using System.Security;
using BackupPlatform.Agent.Core.Discovery;
using Microsoft.Win32;

namespace BackupPlatform.Agent.Platform.Windows.Discovery;

/// <summary>Registry access shared by the Windows detectors: the 64-bit machine hive, and registry failures reduced to source statuses.</summary>
internal static class WindowsRegistry
{
    public static RegistryKey OpenMachineRoot() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Runs <paramref name="detect"/> against the root; a denied or failing registry is a source status, never an exception.</summary>
    public static DetectionResult Detect(Func<RegistryKey> openRoot, Func<RegistryKey, IReadOnlyList<DetectedInstance>> detect)
    {
        try
        {
            using var root = openRoot();
            return DetectionResult.Scanned(detect(root));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return DetectionResult.Unavailable("registry_access_denied");
        }
        catch (Exception exception) when (exception is IOException)
        {
            return DetectionResult.Failed("registry_failed");
        }
    }

    public static string? Text(RegistryKey? key, string name) => key?.GetValue(name) is string { Length: > 0 } text ? text : null;
}
