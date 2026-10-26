using System.Collections.Concurrent;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Downloads;

namespace BackupPlatform.Api.Downloads;

/// <summary>Ephemeral SDP only: a restart fails closed, and sessions remain fenced by the durable download lease.</summary>
public sealed class DirectDownloadSignals(TimeProvider time)
{
    private sealed record Entry(string Offer, string? Answer, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public async Task<IResult> BrowserAsync(HttpContext http, DownloadSession session)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (session.IsTerminal || !session.IsPinned(time.GetUtcNow())) return Results.Conflict(new { error = session.FailureCode ?? "download_expired" });
        if (HttpMethods.IsPost(http.Request.Method))
        {
            var signal = await http.Request.ReadFromJsonAsync<DownloadSignal>(http.RequestAborted);
            if (signal?.Sdp is null || !ValidSdp(signal.Sdp)) return Results.BadRequest();
            foreach (var item in _entries) if (item.Value.ExpiresAt <= time.GetUtcNow()) _entries.TryRemove(item.Key, out _);
            return _entries.TryAdd(session.Id, new Entry(signal!.Sdp!, null, session.ExpiresAt))
                ? Results.Ok(new DownloadSignal(null)) : Results.Conflict(new { error = "download_already_started" });
        }
        return Results.Ok(new DownloadSignal(_entries.TryGetValue(session.Id, out var entry) ? entry.Answer : null));
    }

    public string? Offer(Guid sessionId) => _entries.TryGetValue(sessionId, out var entry) && entry.ExpiresAt > time.GetUtcNow() ? entry.Offer : null;
    public bool Answer(Guid sessionId, string sdp)
    {
        if (!ValidSdp(sdp) || !_entries.TryGetValue(sessionId, out var entry) || entry.Answer is not null || entry.ExpiresAt <= time.GetUtcNow()) return false;
        return _entries.TryUpdate(sessionId, entry with { Answer = sdp }, entry);
    }
    public void Release(Guid sessionId) => _entries.TryRemove(sessionId, out _);

    internal static bool ValidSdp(string? sdp) => sdp is { Length: > 0 and <= 60000 }
        && (sdp.StartsWith("v=0\r\n", StringComparison.Ordinal) || sdp.StartsWith("v=0\n", StringComparison.Ordinal))
        && sdp.Contains("m=application ", StringComparison.Ordinal)
        && sdp.Contains("UDP/DTLS/SCTP", StringComparison.Ordinal)
        && sdp.Contains("a=fingerprint:sha-256 ", StringComparison.Ordinal)
        && !sdp.Contains(" typ relay", StringComparison.Ordinal)
        && !sdp.Contains("m=audio ", StringComparison.Ordinal) && !sdp.Contains("m=video ", StringComparison.Ordinal);
}
