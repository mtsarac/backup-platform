using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BackupPlatform.UnitTests.Notifications.Support;

internal static class TestCertificate
{
    /// <summary>A throwaway self-signed certificate for the isolated test servers; clients under test trust it only through an explicit test seam.</summary>
    public static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}

internal sealed record ReceivedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}

internal sealed record TestResponse(int Status, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>An isolated HTTPS endpoint on loopback: records every request and answers with whatever the test's handler decides.</summary>
internal sealed class TestHttpsServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = TestCertificate.Create();
    private readonly Func<ReceivedRequest, TestResponse> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<ReceivedRequest> _requests = [];

    public TestHttpsServer(Func<ReceivedRequest, TestResponse>? handler = null)
    {
        _handler = handler ?? (_ => new TestResponse(200));
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Url(string path = "/hook") => $"https://127.0.0.1:{Port}{path}";
    public IReadOnlyList<ReceivedRequest> Requests { get { lock (_requests) return _requests.ToList(); } }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(_certificate);
            var buffer = new List<byte>();
            var one = new byte[1];
            while (!(buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n'))
            {
                if (await ssl.ReadAsync(one) == 0) return;
                buffer.Add(one[0]);
            }
            var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var start = lines[0].Split(' ');
            var headers = lines.Skip(1).Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0].Trim().ToLowerInvariant(), parts => parts[1].Trim());
            var body = new byte[headers.TryGetValue("content-length", out var length) ? int.Parse(length) : 0];
            for (var read = 0; read < body.Length;) read += await ssl.ReadAsync(body.AsMemory(read));
            var request = new ReceivedRequest(start[0], start[1], headers, body);
            lock (_requests) _requests.Add(request);
            var response = _handler(request);
            var text = new StringBuilder($"HTTP/1.1 {response.Status} X\r\nContent-Length: 0\r\nConnection: close\r\n");
            foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>()) text.Append($"{name}: {value}\r\n");
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"));
            await ssl.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
        {
            // A client that hangs up (or refuses our certificate) is a test scenario, not a server failure.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _certificate.Dispose();
    }
}

public enum TestSmtpTls { None, Implicit, StartTls }

/// <summary>An isolated SMTP server on loopback with scriptable replies. It records accepted messages and authentication attempts.</summary>
internal sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = TestCertificate.Create();
    private readonly TestSmtpTls _tls;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _connections;

    public TestSmtpServer(TestSmtpTls tls = TestSmtpTls.None, (string User, string Password)? credentials = null)
    {
        _tls = tls;
        Credentials = credentials;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public (string User, string Password)? Credentials { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Connections => Volatile.Read(ref _connections);
    /// <summary>The reply to DATA's end, for example <c>451 try later</c>.</summary>
    public string DataReply { get; set; } = "250 queued";
    public List<string> Messages { get; } = [];
    public List<string> AuthAttempts { get; } = [];

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (_tls == TestSmtpTls.Implicit)
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsServerAsync(_certificate);
                stream = ssl;
            }
            var secure = _tls == TestSmtpTls.Implicit;
            await SendAsync(stream, "220 test ESMTP");
            var data = false;
            var message = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync(stream);
                if (line is null) return;
                if (data)
                {
                    if (line == ".")
                    {
                        data = false;
                        lock (Messages) Messages.Add(message.ToString());
                        message.Clear();
                        await SendAsync(stream, DataReply);
                    }
                    else message.Append(line.StartsWith("..") ? line[1..] : line).Append("\r\n");
                    continue;
                }
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        var extensions = new List<string> { "test" };
                        if (_tls == TestSmtpTls.StartTls && !secure) extensions.Add("STARTTLS");
                        if (Credentials is not null && (secure || _tls == TestSmtpTls.None)) extensions.Add("AUTH PLAIN");
                        await SendAsync(stream, string.Join("\r\n", extensions.Select((extension, index) => (index == extensions.Count - 1 ? "250 " : "250-") + extension)));
                        break;
                    case "STARTTLS":
                        await SendAsync(stream, "220 go ahead");
                        var upgraded = new SslStream(stream);
                        await upgraded.AuthenticateAsServerAsync(_certificate);
                        stream = upgraded;
                        secure = true;
                        break;
                    case "AUTH":
                        var parts = line.Split(' ');
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parts.Length > 2 ? parts[2] : "")).Split('\0');
                        lock (AuthAttempts) AuthAttempts.Add(decoded.Length == 3 ? decoded[1] : "?");
                        var ok = Credentials is { } expected && decoded.Length == 3 && decoded[1] == expected.User && decoded[2] == expected.Password;
                        await SendAsync(stream, ok ? "235 authenticated" : "535 authentication failed");
                        break;
                    case "DATA":
                        data = true;
                        await SendAsync(stream, "354 end with <CRLF>.<CRLF>");
                        break;
                    case "QUIT":
                        await SendAsync(stream, "221 bye");
                        return;
                    default:
                        await SendAsync(stream, "250 ok");
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException or FormatException)
        {
            // The client under test may abort (for example when it refuses our certificate or the missing STARTTLS).
        }
    }

    private static async Task SendAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"));
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            if (one[0] == '\n') return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add(one[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _certificate.Dispose();
    }
}
