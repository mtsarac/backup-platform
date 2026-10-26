namespace BackupPlatform.StorageProviders.Abstractions;

public interface IWindowsNetworkSession
{
    Task<T> RunAsync<T>(string userName, string password, string? domain, Func<Task<T>> operation);
}
