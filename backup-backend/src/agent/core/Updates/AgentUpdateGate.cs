namespace BackupPlatform.Agent.Core.Updates;

/// <summary>Reserves queued work through completion, so activation cannot race a new claim.</summary>
public sealed class AgentUpdateGate
{
    private readonly object _sync = new();
    private int _work;
    private bool _updating;

    public bool TryQueue(Func<bool> enqueue)
    {
        lock (_sync)
        {
            if (_updating || !enqueue()) return false;
            _work++;
            return true;
        }
    }

    public void Complete() { lock (_sync) { _work--; } }
    public bool TryBeginUpdate()
    {
        lock (_sync)
        {
            if (_updating || _work != 0) return false;
            _updating = true;
            return true;
        }
    }
    public void CancelUpdate() { lock (_sync) _updating = false; }
}
