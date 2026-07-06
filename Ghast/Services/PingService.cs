using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsClient;

namespace Ghast.Services;

/// <summary>Thrown for expected, user-facing failures (bad address, offline, timeout).</summary>
public class PingTestException : Exception
{
    public PingTestException(string friendlyMessage) : base(friendlyMessage) { }
}

// Properties, not fields — WPF bindings (Report.AvgMs etc.) silently ignore fields.
public class PingReport
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string ResolvedVia { get; set; } = "";      // "SRV record" | "direct"
    public double AvgMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double JitterMs { get; set; }
    public double LossPercent { get; set; }
    public int SamplesTried { get; set; }
    public int SamplesOk { get; set; }
    public string? Motd { get; set; }
    public string? Version { get; set; }
    public int? PlayersOnline { get; set; }
    public int? PlayersMax { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public string Summary { get; set; } = "";
    public string LatencyRating { get; set; } = "";
    public string JitterRating { get; set; } = "";
    public string StabilityRating { get; set; } = "";
}

/// <summary>
/// Minecraft Server List Ping (status protocol): TCP connect → handshake(next state=1)
/// → status request/response → ping(0x01)/pong round trip. Vanilla servers close the
/// connection after one pong, so each latency sample is its own short connection,
/// repeated across an ~8 second window. That per-connection failure rate is what the
/// "packet loss" figure reports.
/// </summary>
public class PingService
{
    private const int DefaultPort = 25565;
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(8);
    private const int MaxSamples = 12;
    private const int GapMs = 300;

    public async Task<PingReport> TestAsync(string input, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var (host, explicitPort) = ParseAddress(input);

        var port = explicitPort ?? DefaultPort;
        var resolvedVia = "direct";
        if (explicitPort is null)
        {
            progress?.Report("Looking up server…");
            var srv = await TryResolveSrvAsync(host, ct);
            if (srv is { } s)
            {
                (host, port) = s;
                resolvedVia = "SRV record";
            }
        }

        var report = new PingReport { Host = host, Port = port, ResolvedVia = resolvedVia };
        var samples = new List<double>();
        var tried = 0;
        string? firstError = null;

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TestBudget && tried < MaxSamples)
        {
            ct.ThrowIfCancellationRequested();
            tried++;
            progress?.Report($"Pinging… sample {tried}/{MaxSamples}");
            try
            {
                var wantStatus = report.Motd is null && samples.Count == 0;
                var (ms, status) = await SampleAsync(host, port, wantStatus, ct);
                samples.Add(ms);
                if (status is not null)
                    ApplyStatus(report, status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstError ??= FriendlyNetworkError(ex);
                Logger.Log($"ping sample {tried} to {host}:{port} failed: {ex.Message}");
            }
            await Task.Delay(GapMs, ct);
        }

        report.SamplesTried = tried;
        report.SamplesOk = samples.Count;

        if (samples.Count == 0)
            throw new PingTestException(firstError
                ?? $"Couldn't reach {host}:{port}. Check the address, or the server may be offline.");

        report.AvgMs = Math.Round(samples.Average(), 1);
        report.MinMs = Math.Round(samples.Min(), 1);
        report.MaxMs = Math.Round(samples.Max(), 1);
        report.JitterMs = Math.Round(Jitter(samples), 1);
        report.LossPercent = Math.Round(100.0 * (tried - samples.Count) / tried, 1);
        ScoreReport(report);
        Logger.Log($"ping test {host}:{port} — avg {report.AvgMs}ms jitter {report.JitterMs}ms " +
                   $"loss {report.LossPercent}% score {report.Score}");
        return report;
    }

    // ---------- address handling ----------

    private static (string Host, int? Port) ParseAddress(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
            throw new PingTestException("Enter a server address first.");
        if (text.Contains(' ') || text.Contains('/'))
            throw new PingTestException("That doesn't look like a server address (no spaces or slashes).");

        // "host:port" — only when there's exactly one colon (avoids mangling raw IPv6).
        var colons = text.Count(c => c == ':');
        if (colons == 1)
        {
            var idx = text.IndexOf(':');
            var portText = text[(idx + 1)..];
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
                throw new PingTestException($"'{portText}' isn't a valid port (1–65535).");
            return (text[..idx], port);
        }
        return (text, null);
    }

    private static async Task<(string Host, int Port)?> TryResolveSrvAsync(string host, CancellationToken ct)
    {
        try
        {
            var lookup = new LookupClient(new LookupClientOptions
            {
                Timeout = TimeSpan.FromSeconds(3),
                ThrowDnsErrors = false
            });
            var result = await lookup.QueryAsync($"_minecraft._tcp.{host}", QueryType.SRV, cancellationToken: ct);
            var record = result.Answers.SrvRecords()
                .OrderBy(r => r.Priority).ThenByDescending(r => r.Weight)
                .FirstOrDefault();
            if (record is null)
                return null;
            var target = record.Target.Value.TrimEnd('.');
            if (target.Length == 0)
                return null;
            return (target, record.Port);
        }
        catch (Exception ex)
        {
            Logger.Log($"SRV lookup for {host} failed ({ex.Message}); falling back to A record + {DefaultPort}");
            return null;
        }
    }

    // ---------- one sample = one short connection ----------

    private static async Task<(double Ms, JsonDocument? Status)> SampleAsync(
        string host, int port, bool readStatusJson, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct).AsTask().WaitAsync(SampleTimeout, ct);
        var stream = tcp.GetStream();

        // Handshake (protocol -1 = "just asking for status") + status request
        var handshake = BuildPacket(0x00, w =>
        {
            WriteVarInt(w, -1);
            WriteString(w, host);
            w.Write((byte)(port >> 8));
            w.Write((byte)port);
            WriteVarInt(w, 1); // next state: status
        });
        var statusRequest = BuildPacket(0x00, _ => { });
        await stream.WriteAsync(handshake, ct).AsTask().WaitAsync(SampleTimeout, ct);
        await stream.WriteAsync(statusRequest, ct).AsTask().WaitAsync(SampleTimeout, ct);

        // Status response: [VarInt len][VarInt id=0x00][VarInt strLen][json]
        var (statusId, statusPayload) = await ReadPacketAsync(stream, ct);
        if (statusId != 0x00)
            throw new InvalidOperationException($"unexpected status packet id 0x{statusId:X2}");
        JsonDocument? status = null;
        if (readStatusJson)
        {
            using var reader = new BinaryReader(new MemoryStream(statusPayload));
            var len = ReadVarInt(reader);
            var json = Encoding.UTF8.GetString(reader.ReadBytes(len));
            try { status = JsonDocument.Parse(json); }
            catch { /* malformed MOTD JSON is not fatal to the latency test */ }
        }

        // Timed ping/pong
        var payload = Environment.TickCount64;
        var ping = BuildPacket(0x01, w => w.Write(payload));
        var sw = Stopwatch.StartNew();
        await stream.WriteAsync(ping, ct).AsTask().WaitAsync(SampleTimeout, ct);
        var (pongId, _) = await ReadPacketAsync(stream, ct);
        sw.Stop();
        if (pongId != 0x01)
            throw new InvalidOperationException($"unexpected pong packet id 0x{pongId:X2}");

        return (sw.Elapsed.TotalMilliseconds, status);
    }

    private static void ApplyStatus(PingReport report, JsonDocument status)
    {
        try
        {
            var root = status.RootElement;
            if (root.TryGetProperty("description", out var desc))
                report.Motd = CleanMotd(FlattenChat(desc));
            if (root.TryGetProperty("players", out var players))
            {
                if (players.TryGetProperty("online", out var online) && online.TryGetInt32(out var o))
                    report.PlayersOnline = o;
                if (players.TryGetProperty("max", out var max) && max.TryGetInt32(out var m))
                    report.PlayersMax = m;
            }
            if (root.TryGetProperty("version", out var version)
                && version.TryGetProperty("name", out var name))
                report.Version = name.GetString();
        }
        catch (Exception ex)
        {
            Logger.Log($"status JSON parse issue (ignored): {ex.Message}");
        }
        finally
        {
            status.Dispose();
        }
    }

    /// <summary>MOTDs come as either a plain string or a chat object with "text" + "extra".</summary>
    private static string FlattenChat(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Object:
                var sb = new StringBuilder();
                if (element.TryGetProperty("text", out var text))
                    sb.Append(FlattenChat(text));
                if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    foreach (var part in extra.EnumerateArray())
                        sb.Append(FlattenChat(part));
                return sb.ToString();
            default:
                return "";
        }
    }

    private static string CleanMotd(string motd)
    {
        var clean = Regex.Replace(motd, "§.", "");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length > 120 ? clean[..120] + "…" : clean;
    }

    // ---------- scoring ----------

    private static double Jitter(List<double> samples)
    {
        if (samples.Count < 2)
            return 0;
        double total = 0;
        for (var i = 1; i < samples.Count; i++)
            total += Math.Abs(samples[i] - samples[i - 1]);
        return total / (samples.Count - 1);
    }

    private static void ScoreReport(PingReport r)
    {
        var latencyPenalty = Math.Clamp((r.AvgMs - 25) * 0.45, 0, 45);
        var jitterPenalty = Math.Clamp(r.JitterMs * 1.5, 0, 25);
        var lossPenalty = Math.Clamp(r.LossPercent * 2.5, 0, 30);
        r.Score = (int)Math.Round(Math.Clamp(100 - latencyPenalty - jitterPenalty - lossPenalty, 0, 100));

        r.Grade = r.Score switch
        {
            >= 92 => "A+", >= 84 => "A", >= 74 => "B", >= 62 => "C", >= 50 => "D", _ => "F"
        };

        r.LatencyRating = r.AvgMs switch
        {
            < 30 => "Excellent — you'll feel instant hits",
            < 60 => "Very good — great for PvP",
            < 90 => "Good — fine for most gameplay",
            < 130 => "Fair — noticeable in fast PvP",
            _ => "Poor — expect visible lag"
        };
        r.JitterRating = r.JitterMs switch
        {
            < 4 => "Rock steady",
            < 10 => "Steady",
            < 20 => "A little wobbly",
            _ => "Unstable timing between packets"
        };
        r.StabilityRating = r.LossPercent switch
        {
            <= 0 => $"No failed pings ({r.SamplesOk}/{r.SamplesTried} succeeded)",
            <= 10 => $"Mostly reliable ({r.SamplesOk}/{r.SamplesTried} succeeded)",
            _ => $"Connection drops — only {r.SamplesOk}/{r.SamplesTried} pings made it"
        };
        r.Summary = r.Grade switch
        {
            "A+" or "A" => "Solid connection — good for competitive play.",
            "B" => "Decent connection — fine for most servers, minor disadvantage in sweaty PvP.",
            "C" => "Playable, but you'll feel the delay in fights.",
            "D" => "Rough — expect rubber-banding and late hits.",
            _ => "This route is struggling — try a closer server or check your network."
        };
    }

    // ---------- protocol plumbing ----------

    private static byte[] BuildPacket(int packetId, Action<BinaryWriter> writePayload)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            WriteVarInt(w, packetId);
            writePayload(w);
        }
        using var framed = new MemoryStream();
        using (var w = new BinaryWriter(framed, Encoding.UTF8, leaveOpen: true))
        {
            WriteVarInt(w, (int)body.Length);
            w.Write(body.ToArray());
        }
        return framed.ToArray();
    }

    private static async Task<(int PacketId, byte[] Payload)> ReadPacketAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        if (length is < 1 or > 4 * 1024 * 1024)
            throw new InvalidOperationException($"implausible packet length {length}");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct).AsTask().WaitAsync(SampleTimeout, ct);
        using var reader = new BinaryReader(new MemoryStream(buffer));
        var id = ReadVarInt(reader);
        var payload = reader.ReadBytes(length - VarIntSize(id));
        return (id, payload);
    }

    private static void WriteVarInt(BinaryWriter w, int value)
    {
        var u = (uint)value;
        do
        {
            var b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0)
                b |= 0x80;
            w.Write(b);
        } while (u != 0);
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteVarInt(w, bytes.Length);
        w.Write(bytes);
    }

    private static int ReadVarInt(BinaryReader r)
    {
        var value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = r.ReadByte();
            value |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidOperationException("VarInt too long");
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        var one = new byte[1];
        var value = 0;
        for (var i = 0; i < 5; i++)
        {
            await stream.ReadExactlyAsync(one, ct).AsTask().WaitAsync(SampleTimeout, ct);
            value |= (one[0] & 0x7F) << (7 * i);
            if ((one[0] & 0x80) == 0)
                return value;
        }
        throw new InvalidOperationException("VarInt too long");
    }

    private static int VarIntSize(int value)
    {
        var u = (uint)value;
        var size = 0;
        do { size++; u >>= 7; } while (u != 0);
        return size;
    }

    private static string FriendlyNetworkError(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            "Couldn't find that address — check the spelling.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "The server refused the connection — wrong port, or it's offline.",
        SocketException =>
            "Network error while connecting — the server may be offline or blocking pings.",
        TimeoutException =>
            "The server didn't answer in time — it may be offline or very far away.",
        EndOfStreamException or IOException =>
            "The server closed the connection unexpectedly — it may not be a Minecraft server.",
        _ => "Couldn't complete the test — see log.txt for details."
    };
}
