using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    DisableDefaults = true,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddCommandLine(args);
builder.Services.AddWindowsService(options => options.ServiceName = builder.Configuration["service-name"] ?? "T1DirectLab");
builder.Services.AddSingleton(new LabOptions(new Uri(builder.Configuration["control-url"] ?? "http://127.0.0.1:18761")));
builder.Services.AddHostedService<LabSender>();
await builder.Build().RunAsync();

internal sealed record LabOptions(Uri ControlUrl);

// Synthetic transport/service experiment only. No enrollment, product authorization, or provider secrets.
internal sealed class LabSender(LabOptions options, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!options.ControlUrl.IsLoopback || options.ControlUrl.Scheme != "http")
                throw new InvalidOperationException("The laboratory control endpoint must be loopback HTTP.");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = options.ControlUrl, Timeout = TimeSpan.FromSeconds(10)
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(20));
            JsonElement offer;
            do
            {
                offer = await http.GetFromJsonAsync<JsonElement>("/agent-offer", deadline.Token);
                if (offer.ValueKind == JsonValueKind.Null) await Task.Delay(100, deadline.Token);
            } while (offer.ValueKind == JsonValueKind.Null);
            await TransferAsync(http, offer, deadline.Token);
        }
        catch (Exception error)
        {
            // No remote exception body or signaling material is logged.
            Console.WriteLine($"T1_DOTNET_FAILED {error.GetType().Name}");
            Environment.ExitCode = 1;
        }
        finally { lifetime.StopApplication(); }
    }

    private static async Task TransferAsync(HttpClient http, JsonElement offer, CancellationToken token)
    {
        const int chunkSize = 16384, windowSize = 1024 * 1024;
        var length = offer.GetProperty("length").GetInt64();
        if (length is <= 0 or > 8L * 1024 * 1024 * 1024) throw new InvalidDataException();
        var corrupt = offer.GetProperty("corrupt").GetBoolean();
        var sdp = offer.GetProperty("sdp").GetString() ?? throw new InvalidDataException();
        var iceServers = offer.GetProperty("iceServers").EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (iceServers.Any(value => !value.StartsWith("stun:", StringComparison.Ordinal))) throw new InvalidDataException();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var signals = Channel.CreateBounded<object>(32);
        var incoming = Channel.CreateBounded<string>(32);
        var channelReady = new TaskCompletionSource<IRtcDataChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var peer = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = iceServers, MaxMessageSize = chunkSize });
        void Signal(object value)
        {
            if (!signals.Writer.TryWrite(value)) stop.Cancel();
        }
        peer.OnLocalDescriptionSafe += (_, description) => Signal(new { kind = "description", sdp = description.Sdp, type = description.Type.ToString().ToLowerInvariant() });
        peer.OnCandidateSafe += (_, candidate) =>
        {
            if (candidate.Content.Contains(" typ relay ", StringComparison.Ordinal)) stop.Cancel();
            else Signal(new { kind = "candidate", candidate = candidate.Content, mid = candidate.Mid });
        };
        peer.OnDataChannel += (_, dc) =>
        {
            if (dc.Label != "artifact" || channelReady.Task.IsCompleted) { stop.Cancel(); return; }
            dc.OnTextReceivedSafe += (_, data) =>
            {
                if (data.Text.Length > 1024 || !incoming.Writer.TryWrite(data.Text)) stop.Cancel();
            };
            dc.OnClose += _ => stop.Cancel();
            dc.OnError += (_, _) => stop.Cancel();
            channelReady.TrySetResult(dc);
        };
        var signalTask = SendSignalsAsync(http, signals.Reader, stop);
        var cancelTask = ObserveCancellationAsync(http, stop);
        long sent = 0, acknowledged = 0, maxOutstanding = 0;
        using var process = Process.GetCurrentProcess();
        var maxRss = process.WorkingSet64;
        var clock = Stopwatch.StartNew();
        var lastSample = TimeSpan.Zero;
        string? digest = null;
        var outcome = "Failed";
        string? failure = null;
        try
        {
            peer.SetRemoteDescription(new RtcDescription
            {
                Sdp = sdp,
                Type = Enum.Parse<RtcDescriptionType>(offer.GetProperty("type").GetString()!, true)
            });
            var channel = await channelReady.Task.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var block = RandomNumberGenerator.GetBytes(chunkSize);
            while (true)
            {
                var raw = await incoming.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(120), stop.Token);
                using var message = JsonDocument.Parse(raw);
                var root = message.RootElement;
                var kind = root.GetProperty("kind").GetString();
                if (kind == "cancel") { outcome = "Cancelled"; failure = "ReceiverCancelled"; break; }
                if (kind == "verified")
                {
                    if (sent != length || root.GetProperty("length").GetInt64() != length || digest is null || root.GetProperty("sha256").GetString() != digest)
                        throw new InvalidDataException("Invalid completion receipt.");
                    outcome = "Verified";
                    break;
                }
                if (kind != "ack") throw new InvalidDataException();
                var credit = root.GetProperty("bytes").GetInt64();
                if (credit < acknowledged || credit > sent) throw new InvalidDataException();
                acknowledged = credit;
                while (sent < length && sent - acknowledged < windowSize)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var size = (int)Math.Min(chunkSize, Math.Min(length - sent, windowSize - (sent - acknowledged)));
                    if (size >= 8) BinaryPrimitives.WriteInt64LittleEndian(block, sent);
                    hash.AppendData(block.AsSpan(0, size));
                    if (corrupt && sent + size == length) block[size - 1] ^= 1;
                    channel.Send(block.AsSpan(0, size));
                    sent += size;
                    maxOutstanding = Math.Max(maxOutstanding, sent - acknowledged);
                }
                if (sent == length && digest is null)
                {
                    digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                    channel.Send(JsonSerializer.Serialize(new { kind = "end", length, sha256 = digest }));
                }
                if (clock.Elapsed - lastSample >= TimeSpan.FromMilliseconds(250))
                {
                    process.Refresh();
                    maxRss = Math.Max(maxRss, process.WorkingSet64);
                    lastSample = clock.Elapsed;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { outcome = "Cancelled"; failure = "CancelledOrChannelClosed"; }
        catch (Exception error) { failure = error.GetType().Name; }
        finally
        {
            peer.TryGetSelectedCandidatePair(out var pair);
            Signal(new { kind = "metrics", runtime = ".NET", outcome, error = failure, length, sent, acknowledged,
                sha256 = digest, maxOutstanding, maxRss, durationMs = clock.ElapsedMilliseconds,
                pair, offerHasMdns = sdp.Contains(".local", StringComparison.OrdinalIgnoreCase),
                connectionState = peer.ConnectionState.ToString(), iceState = peer.IceState.ToString(),
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription });
            signals.Writer.TryComplete();
            try { await signalTask.WaitAsync(TimeSpan.FromSeconds(10), token); }
            finally
            {
                await stop.CancelAsync();
                try { await cancelTask; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        Console.WriteLine($"T1_DOTNET_{outcome.ToUpperInvariant()} sent={sent} maxOutstanding={maxOutstanding}");
        if (outcome != "Verified") Environment.ExitCode = 1;
    }

    private static async Task SendSignalsAsync(HttpClient http, ChannelReader<object> signals, CancellationTokenSource stop)
    {
        try
        {
            await foreach (var signal in signals.ReadAllAsync())
            {
                using var reply = await http.PostAsJsonAsync("/agent-events", signal, CancellationToken.None);
                reply.EnsureSuccessStatusCode();
            }
        }
        catch { await stop.CancelAsync(); throw; }
    }

    private static async Task ObserveCancellationAsync(HttpClient http, CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var state = await http.GetFromJsonAsync<JsonElement>("/agent-state", stop.Token);
                if (state.GetProperty("cancelled").GetBoolean()) { await stop.CancelAsync(); return; }
                await Task.Delay(1000, stop.Token);
            }
        }
        catch { await stop.CancelAsync(); throw; }
    }
}
