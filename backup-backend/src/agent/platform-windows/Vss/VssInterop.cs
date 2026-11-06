using System.Runtime.InteropServices;

namespace BackupPlatform.Agent.Platform.Windows.Vss;

// Declarations follow vsbackup.h / vss.h of the Windows SDK. The method ORDER is the COM vtable layout: the methods marked "layout only" are
// never called, they only keep the called ones at their slots. Do not reorder, add in the middle, or drop any of them.

/// <summary>IVssAsync: the handle every long VSS operation returns.</summary>
[ComImport, Guid("507C37B4-CF5B-4e95-B0AF-14EB9767467E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssAsync
{
    [PreserveSig] int Cancel();
    [PreserveSig] int Wait(uint milliseconds);
    [PreserveSig] int QueryStatus(out int hresult, out int reserved);
}

/// <summary>VSS_SNAPSHOT_PROP. The strings belong to VSS and are released with <see cref="VssNative.FreeSnapshotProperties"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VssSnapshotProp
{
    public Guid SnapshotId;
    public Guid SnapshotSetId;
    public int SnapshotsCount;
    public IntPtr SnapshotDeviceObject;
    public IntPtr OriginalVolumeName;
    public IntPtr OriginatingMachine;
    public IntPtr ServiceMachine;
    public IntPtr ExposedName;
    public IntPtr ExposedPath;
    public Guid ProviderId;
    public int SnapshotAttributes;
    public long CreationTimestamp;
    public int Status;
}

[ComImport, Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssBackupComponents
{
    [PreserveSig] int GetWriterComponentsCount(); // layout only
    [PreserveSig] int GetWriterComponents(); // layout only
    [PreserveSig] int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? xml);
    [PreserveSig] int SetBackupState([MarshalAs(UnmanagedType.Bool)] bool selectComponents, [MarshalAs(UnmanagedType.Bool)] bool backupBootableSystemState, int backupType, [MarshalAs(UnmanagedType.Bool)] bool partialFileSupport);
    [PreserveSig] int InitializeForRestore(); // layout only
    [PreserveSig] int SetRestoreState(); // layout only
    [PreserveSig] int GatherWriterMetadata(out IVssAsync async);
    [PreserveSig] int GetWriterMetadataCount(); // layout only
    [PreserveSig] int GetWriterMetadata(); // layout only
    [PreserveSig] int FreeWriterMetadata(); // layout only
    [PreserveSig] int AddComponent(); // layout only
    [PreserveSig] int PrepareForBackup(out IVssAsync async);
    [PreserveSig] int AbortBackup();
    [PreserveSig] int GatherWriterStatus(); // layout only
    [PreserveSig] int GetWriterStatusCount(); // layout only
    [PreserveSig] int FreeWriterStatus(); // layout only
    [PreserveSig] int GetWriterStatus(); // layout only
    [PreserveSig] int SetBackupSucceeded(); // layout only
    [PreserveSig] int SetBackupOptions(); // layout only
    [PreserveSig] int SetSelectedForRestore(); // layout only
    [PreserveSig] int SetRestoreOptions(); // layout only
    [PreserveSig] int SetAdditionalRestores(); // layout only
    [PreserveSig] int SetPreviousBackupStamp(); // layout only
    [PreserveSig] int SaveAsXML(); // layout only
    [PreserveSig] int BackupComplete(out IVssAsync async);
    [PreserveSig] int AddAlternativeLocationMapping(); // layout only
    [PreserveSig] int AddRestoreSubcomponent(); // layout only
    [PreserveSig] int SetFileRestoreStatus(); // layout only
    [PreserveSig] int AddNewTarget(); // layout only
    [PreserveSig] int SetRangesFilePath(); // layout only
    [PreserveSig] int PreRestore(); // layout only
    [PreserveSig] int PostRestore(); // layout only
    [PreserveSig] int SetContext(int context);
    [PreserveSig] int StartSnapshotSet(out Guid snapshotSetId);
    [PreserveSig] int AddToSnapshotSet([MarshalAs(UnmanagedType.LPWStr)] string volumeName, Guid providerId, out Guid snapshotId);
    [PreserveSig] int DoSnapshotSet(out IVssAsync async);
    [PreserveSig] int DeleteSnapshots(Guid sourceObjectId, int sourceObjectType, [MarshalAs(UnmanagedType.Bool)] bool forceDelete, out int deletedSnapshots, out Guid nonDeletedSnapshotId);
    [PreserveSig] int ImportSnapshots(); // layout only
    [PreserveSig] int BreakSnapshotSet(); // layout only
    [PreserveSig] int GetSnapshotProperties(Guid snapshotId, out VssSnapshotProp properties);
}

internal static class VssNative
{
    /// <summary>Success status of an <see cref="IVssAsync"/> (VSS_S_ASYNC_FINISHED).</summary>
    public const int AsyncFinished = 0x0004230A;
    /// <summary>VSS_S_ASYNC_PENDING.</summary>
    public const int AsyncPending = 0x00042309;
    /// <summary>VSS_CTX_BACKUP: the snapshot disappears when the requester releases it or its process ends.</summary>
    public const int ContextBackup = 0;
    /// <summary>VSS_BT_COPY: a copy backup never truncates other applications' logs.</summary>
    public const int BackupTypeCopy = 5;
    /// <summary>VSS_CTX_ALL: every snapshot regardless of how it was created; needed to delete a leftover of a dead requester.</summary>
    public const int ContextAll = unchecked((int)0xFFFFFFFF);
    /// <summary>VSS_OBJECT_SNAPSHOT.</summary>
    public const int ObjectSnapshot = 3;
    /// <summary>VSS_E_OBJECT_NOT_FOUND.</summary>
    public const int ObjectNotFound = unchecked((int)0x80042308);

    [DllImport("vssapi.dll", EntryPoint = "?CreateVssBackupComponents@@YAJPEAPEAVIVssBackupComponents@@@Z")]
    public static extern int CreateVssBackupComponents(out IntPtr components);

    [DllImport("vssapi.dll", EntryPoint = "VssFreeSnapshotPropertiesInternal")]
    public static extern void FreeSnapshotProperties(ref VssSnapshotProp properties);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, int flags);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern int CoInitializeSecurity(IntPtr descriptor, int authServices, IntPtr authServiceList, IntPtr reserved, int authLevel, int impersonation, IntPtr authInfo, int capabilities, IntPtr reserved3);
}
