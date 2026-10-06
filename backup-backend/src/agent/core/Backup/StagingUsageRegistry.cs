namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// The staged file names that backups running in this agent process use right now. The staging cleanup consults it so that it never
/// removes a file a live attempt still needs, however old the file looks (a long backup writes for hours, and a file's last write time
/// says nothing about whether somebody still depends on it). Names are compared ignoring case: a case-insensitive file system treats
/// two spellings as one file, and wrongly protecting a file is harmless while wrongly deleting one is not.
/// </summary>
public sealed class StagingUsageRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _holds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks <paramref name="fileName"/> as used by a current attempt until the returned lease is disposed. Holds are counted: two
    /// holds of one name need two releases. Disposing a lease more than once releases it once.
    /// </summary>
    public IDisposable Hold(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        lock (_gate)
        {
            _holds[fileName] = _holds.GetValueOrDefault(fileName) + 1;
        }

        return new Lease(this, fileName);
    }

    /// <summary>True when at least one attempt currently holds <paramref name="fileName"/>.</summary>
    public bool IsHeld(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        lock (_gate)
        {
            return _holds.ContainsKey(fileName);
        }
    }

    private void Release(string fileName)
    {
        lock (_gate)
        {
            if (!_holds.TryGetValue(fileName, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _holds.Remove(fileName);
            }
            else
            {
                _holds[fileName] = count - 1;
            }
        }
    }

    private sealed class Lease(StagingUsageRegistry owner, string fileName) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(fileName);
            }
        }
    }
}
