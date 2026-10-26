using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using BackupPlatform.Agent.Core.Discovery;

namespace BackupPlatform.Agent.Transport;

public sealed class DockerEngineHttpTransport : IDockerEngineTransport, IDisposable
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public DockerEngineHttpTransport() : this(CreateClient(), ownsClient: true) { }

    public DockerEngineHttpTransport(HttpClient client, bool ownsClient = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, token) =>
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(token).ConfigureAwait(false); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    });

    public async Task<string?> QueryContainersAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await client.GetAsync("http://docker/containers/json?all=false", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Docker container discovery is not permitted.");
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumOutputCharacters) return null;
                buffer.Write(bytes, 0, count);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (HttpRequestException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied })
        {
            throw new UnauthorizedAccessException("Docker socket access is not permitted.");
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new UnauthorizedAccessException("Docker socket access is not permitted.");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}
