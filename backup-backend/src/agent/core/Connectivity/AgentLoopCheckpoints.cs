namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>Optional internal observation of real async suspension points; never schedules or orders the loops.</summary>
internal interface IAgentLoopObserver
{
    void Started(string operation);
    void Executing(string operation);
    bool Signal(string operation, Func<bool> signal);
    void Waiting(string operation, Task checkpoint);
    void Resumed(string operation);
    void Completed(string operation);
    Task AdmittedAsync(string operation, CancellationToken cancellationToken);
}

internal static class AgentLoopCheckpoints
{
    internal static void StartOperation(this TimeProvider time, string operation) =>
        (time as IAgentLoopObserver)?.Started(operation);

    internal static void EnterOperation(this TimeProvider time, string operation) =>
        (time as IAgentLoopObserver)?.Executing(operation);

    internal static void EndOperation(this TimeProvider time, string operation) =>
        (time as IAgentLoopObserver)?.Completed(operation);

    internal static Task ObserveAsync(this TimeProvider time, string operation, Task checkpoint) =>
        time is IAgentLoopObserver observer ? ObserveCoreAsync(observer, operation, checkpoint) : checkpoint;

    internal static Task<T> ObserveAsync<T>(this TimeProvider time, string operation, Task<T> checkpoint) =>
        time is IAgentLoopObserver observer ? ObserveCoreAsync(observer, operation, checkpoint) : checkpoint;

    internal static Task ObserveAdmissionAsync(this TimeProvider time, string operation, CancellationToken cancellationToken) =>
        (time as IAgentLoopObserver)?.AdmittedAsync(operation, cancellationToken) ?? Task.CompletedTask;

    internal static Task ObserveAsync(this TimeProvider time, string operation, Task checkpoint, CancellationToken cancellationToken) =>
        time is IAgentLoopObserver observer
            ? ObserveCoreAsync(observer, operation, checkpoint, cancellationToken)
            : checkpoint.WaitAsync(cancellationToken);

    private static async Task ObserveCoreAsync(IAgentLoopObserver observer, string operation, Task checkpoint, CancellationToken cancellationToken)
    {
        observer.Waiting(operation, checkpoint);
        try
        {
            await checkpoint.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            observer.Resumed(operation);
        }
    }

    private static async Task ObserveCoreAsync(IAgentLoopObserver observer, string operation, Task checkpoint)
    {
        observer.Waiting(operation, checkpoint);
        try
        {
            await checkpoint.ConfigureAwait(false);
        }
        finally
        {
            observer.Resumed(operation);
        }
    }

    private static async Task<T> ObserveCoreAsync<T>(IAgentLoopObserver observer, string operation, Task<T> checkpoint)
    {
        observer.Waiting(operation, checkpoint);
        try
        {
            return await checkpoint.ConfigureAwait(false);
        }
        finally
        {
            observer.Resumed(operation);
        }
    }
}
