using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using NetVigil.Shared.Protos;

// Usage:
//   dotnet run --project NetVigil.LoadTest -- --devices 200 --rate 5 --duration 60
//   dotnet run --project NetVigil.LoadTest -- --target http://localhost:5002 --concurrency 4
//   dotnet run --project NetVigil.LoadTest -- --devices 100 --rate 10 --mbps-target 500 --probe-frontend

var args0 = ParseArgs(args);
int    devices     = args0.GetInt("devices", 50);
int    rate        = args0.GetInt("rate", 1);              // samples per device per second
int    duration    = args0.GetInt("duration", 30);          // seconds
string target      = args0.GetStr("target", "http://localhost:5002");
int    concurrency = args0.GetInt("concurrency", Math.Max(1, Environment.ProcessorCount / 2));
bool   noPreregister = args0.GetBool("no-preregister");

bool   probeFrontend = args0.GetBool("probe-frontend");
string probeBase     = args0.GetStr("probe-base", "http://localhost:5000");
string probeUser     = args0.GetStr("probe-user", "admin");
string probePass     = args0.GetStr("probe-pass", "admin");
int    probeRate     = args0.GetInt("probe-rate", 1); 

double mbpsTarget = args0.GetDouble("mbps-target", 0);

bool   injectSpikes  = args0.GetBool("inject-spikes");
int    spikeInterval = args0.GetInt("spike-interval", 10);
int    spikeDuration = args0.GetInt("spike-duration", 1);
double spikeFraction = args0.GetDouble("spike-fraction", 0.05);
double spikeMult     = args0.GetDouble("spike-multiplier", 10.0);

Console.WriteLine($"NetVigil.LoadTest → {target}");
Console.WriteLine($"  devices     = {devices}");
Console.WriteLine($"  rate        = {rate} sample/s/device  ({devices * rate} samples/s total)");
if (mbpsTarget > 0)
{
    Console.WriteLine($"  mbps-target = {mbpsTarget:F1} Mbps total ({mbpsTarget / devices:F2} Mbps/device avg ±30%)");
}
Console.WriteLine($"  duration    = {duration}s");
Console.WriteLine($"  concurrency = {concurrency} parallel gRPC streams");
if (probeFrontend)
{
    Console.WriteLine($"  probe       = {probeBase} (every {1000 / Math.Max(1, probeRate)} ms per endpoint)");
}
if (injectSpikes)
{
    var spikeCount = Math.Max(1, (int)Math.Round(devices * spikeFraction));
    Console.WriteLine($"  spikes      = every {spikeInterval}s × {spikeDuration}s, {spikeCount}/{devices} devices × {spikeMult:F1}");
}
Console.WriteLine();

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress(target);
var client = new NetworkScanner.NetworkScannerClient(channel);

if (!noPreregister)
{
    Console.Write("Pre-registering devices... ");
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < devices; i++)
    {
        try
        {
            await client.ReportDeviceAsync(new DeviceData
            {
                MacAddress = MakeMac(i),
                IpAddress = $"10.99.{i / 256}.{i % 256}",
                Hostname = $"loadtest-{i:D5}",
                Vendor = "LoadTest",
                CurrentTrafficMbps = 0
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nReportDevice {i} failed: {ex.Message}");
            return 2;
        }
    }
    Console.WriteLine($"done ({sw.ElapsedMilliseconds} ms)");
}

var chunks = new List<List<int>>(concurrency);
for (int c = 0; c < concurrency; c++) chunks.Add(new List<int>());
for (int i = 0; i < devices; i++) chunks[i % concurrency].Add(i);

var sentTotal     = 0L;
var receivedTotal = 0L;
var anomaliesTotal = 0L;
var errors        = 0;

var statsLatencies   = new ConcurrentBag<long>();
var devicesLatencies = new ConcurrentBag<long>();
var probeErrors      = 0;

var spikeSetCache = new ConcurrentDictionary<int, HashSet<int>>();
HashSet<int> GetSpikeSet(int windowIdx)
{
    return spikeSetCache.GetOrAdd(windowIdx, idx =>
    {
        var rng = new Random(7919 ^ idx);
        var count = Math.Max(1, (int)Math.Round(devices * spikeFraction));
        var set = new HashSet<int>();
        while (set.Count < count) set.Add(rng.Next(devices));
        return set;
    });
}

var testStartUtc = DateTime.UtcNow;
var globalSw     = Stopwatch.StartNew();

var spikeOps = new List<(string Mac, int WindowIdx, DateTime WindowStart)>();
if (injectSpikes)
{
    int numWindows = Math.Max(1, duration / Math.Max(1, spikeInterval));
    for (int w = 0; w < numWindows; w++)
    {
        var ws = testStartUtc.AddSeconds((double)w * spikeInterval);
        if ((ws - testStartUtc).TotalSeconds + spikeDuration > duration) break;
        var set = GetSpikeSet(w);
        foreach (var i in set) spikeOps.Add((MakeMac(i), w, ws));
    }
}

Task probeTask = Task.CompletedTask;
if (probeFrontend)
{
    probeTask = Task.Run(async () =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(probeBase) };
        async Task<bool> LoginAsync()
        {
            try
            {
                var resp = await http.PostAsJsonAsync("api/auth/login",
                    new { username = probeUser, password = probePass });
                if (!resp.IsSuccessStatusCode) return false;
                var body = await resp.Content.ReadFromJsonAsync<LoginReply>();
                if (body is null || string.IsNullOrEmpty(body.Token)) return false;
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.Token);
                return true;
            }
            catch { return false; }
        }

        if (!await LoginAsync())
        {
            Console.Error.WriteLine($"[probe] login to {probeBase} failed — disabling frontend probe");
            return;
        }

        var deadline = TimeSpan.FromSeconds(duration);
        var period   = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, probeRate));

        async Task ProbeOnce(string path, ConcurrentBag<long> sink)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await http.GetAsync(path);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    if (await LoginAsync())
                    {
                        return;
                    }
                }
                if (!resp.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref probeErrors);
                    return;
                }
                _ = await resp.Content.ReadAsByteArrayAsync();
                sink.Add(sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
            }
            catch
            {
                Interlocked.Increment(ref probeErrors);
            }
        }

        while (globalSw.Elapsed < deadline)
        {
            var tickStart = Stopwatch.GetTimestamp();
            await Task.WhenAll(
                ProbeOnce("api/dashboard/stats",   statsLatencies),
                ProbeOnce("api/dashboard/devices", devicesLatencies));
            var elapsed = TimeSpan.FromTicks((Stopwatch.GetTimestamp() - tickStart)
                * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            if (elapsed < period) await Task.Delay(period - elapsed);
        }
    });
}

var workers = chunks.Select((chunk, idx) => Task.Run(async () =>
{
    var deadline = TimeSpan.FromSeconds(duration);
    var localSent = 0L;
    var rng = new Random(1000 + idx);

    while (globalSw.Elapsed < deadline)
    {
        try
        {
            using var call = client.StreamMetrics();
            var localDeadline = globalSw.Elapsed + deadline - globalSw.Elapsed; 
            var tickInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, rate));

            while (globalSw.Elapsed < deadline)
            {
                var tickStart = Stopwatch.GetTimestamp();
                var now = DateTime.UtcNow;

                HashSet<int>? activeSpikes = null;
                if (injectSpikes)
                {
                    var elapsedSec = globalSw.Elapsed.TotalSeconds;
                    int wIdx  = (int)(elapsedSec / spikeInterval);
                    double phase = elapsedSec - wIdx * spikeInterval;
                    if (phase < spikeDuration) activeSpikes = GetSpikeSet(wIdx);
                }

                foreach (var i in chunk)
                {
                    double mbps;
                    if (mbpsTarget > 0)
                    {
                        var avg = mbpsTarget / devices;
                        mbps = avg * (0.7 + rng.NextDouble() * 0.6); 
                    }
                    else
                    {
                        mbps = 0.5 + rng.NextDouble() * 5.0;
                    }
                    if (activeSpikes != null && activeSpikes.Contains(i))
                    {
                        mbps *= spikeMult;
                    }
                    await call.RequestStream.WriteAsync(new MetricSample
                    {
                        MacAddress = MakeMac(i),
                        IpAddress = $"10.99.{i / 256}.{i % 256}",
                        Timestamp = Timestamp.FromDateTime(now),
                        MbpsIn = mbps * 0.6,
                        MbpsOut = mbps * 0.4,
                        PacketsPerSec = (uint)(mbps * 100)
                    });
                    Interlocked.Increment(ref localSent);
                }

                var elapsedTicks = Stopwatch.GetTimestamp() - tickStart;
                var elapsed = TimeSpan.FromTicks(elapsedTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
                if (elapsed < tickInterval)
                {
                    await Task.Delay(tickInterval - elapsed);
                }
            }

            await call.RequestStream.CompleteAsync();
            var resp = await call.ResponseAsync;
            Interlocked.Add(ref receivedTotal, resp.SamplesReceived);
            Interlocked.Add(ref anomaliesTotal, resp.AnomaliesDetected);
            break; 
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref errors);
            Console.Error.WriteLine($"[w{idx}] stream error: {ex.GetType().Name}: {ex.Message}");
            await Task.Delay(500);
        }
    }

    Interlocked.Add(ref sentTotal, localSent);
})).ToArray();

await Task.WhenAll(workers);
await probeTask; 
globalSw.Stop();

var elapsedSec = Math.Max(0.001, globalSw.Elapsed.TotalSeconds);
Console.WriteLine();
Console.WriteLine("==== Results ====");
Console.WriteLine($"  Wall-clock     : {elapsedSec:F2}s");
Console.WriteLine($"  Samples sent   : {sentTotal:N0}");
Console.WriteLine($"  Samples acked  : {receivedTotal:N0}");
Console.WriteLine($"  Anomalies      : {anomaliesTotal:N0}");
Console.WriteLine($"  Throughput     : {sentTotal / elapsedSec:N0} samples/s sent  ({receivedTotal / elapsedSec:N0} acked/s)");
Console.WriteLine($"  Stream errors  : {errors}");

if (probeFrontend)
{
    Console.WriteLine();
    Console.WriteLine("==== Frontend response time (under load) ====");
    PrintLatency("/api/dashboard/stats  ", statsLatencies);
    PrintLatency("/api/dashboard/devices", devicesLatencies);
    Console.WriteLine($"  Probe errors   : {probeErrors}");
}

if (injectSpikes)
{
    Console.WriteLine();
    Console.WriteLine("==== Ground-truth detection (--inject-spikes) ====");

    await Task.Delay(2500);

    using var http = new HttpClient { BaseAddress = new Uri(probeBase) };
    bool loggedIn = false;
    try
    {
        var resp = await http.PostAsJsonAsync("api/auth/login",
            new { username = probeUser, password = probePass });
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<LoginReply>();
            if (body is not null && !string.IsNullOrEmpty(body.Token))
            {
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.Token);
                loggedIn = true;
            }
        }
    }
    catch {}

    if (!loggedIn)
    {
        Console.Error.WriteLine($"  [spikes] login to {probeBase} failed — cannot fetch anomalies for matching");
    }
    else
    {
        try
        {
            var anomalies = await http.GetFromJsonAsync<List<AnomalyResp>>(
                "api/dashboard/anomalies?limit=500") ?? new List<AnomalyResp>();

            var tolerance = TimeSpan.FromSeconds(3);
            var matchedOps = new HashSet<int>();
            int tp = 0, fp = 0;

            foreach (var a in anomalies)
            {
                int found = -1;
                for (int oi = 0; oi < spikeOps.Count; oi++)
                {
                    var op = spikeOps[oi];
                    if (!string.Equals(a.DeviceMac, op.Mac, StringComparison.OrdinalIgnoreCase)) continue;
                    var lo = op.WindowStart - tolerance;
                    var hi = op.WindowStart.AddSeconds(spikeDuration) + tolerance;
                    if (a.Timestamp >= lo && a.Timestamp <= hi) { found = oi; break; }
                }
                if (found >= 0)
                {
                    if (matchedOps.Add(found)) tp++;
                }
                else
                {
                    fp++;
                }
            }
            int fn = spikeOps.Count - tp;

            long samplesPerSpike = (long)Math.Max(1, spikeDuration * rate);
            long spikedSamples   = (long)spikeOps.Count * samplesPerSpike;
            long benignSamples   = Math.Max(0, sentTotal - spikedSamples);
            long tn              = Math.Max(0, benignSamples - fp);

            double precision = (tp + fp) > 0 ? 100.0 * tp / (tp + fp) : 0.0;
            double recall    = spikeOps.Count > 0 ? 100.0 * tp / spikeOps.Count : 0.0;
            double fpr       = benignSamples > 0 ? 100.0 * fp / benignSamples : 0.0;
            double accuracy  = sentTotal > 0 ? 100.0 * (tp * samplesPerSpike + tn) / sentTotal : 0.0;

            Console.WriteLine($"  Spike opportunities  : {spikeOps.Count}  (mac × window pairs)");
            Console.WriteLine($"  Anomalies fetched    : {anomalies.Count}");
            Console.WriteLine($"  TP / FP / FN         : {tp} / {fp} / {fn}");
            Console.WriteLine($"  Precision            : {precision:F1}%");
            Console.WriteLine($"  Recall               : {recall:F1}%");
            Console.WriteLine($"  False Positive Rate  : {fpr:F2}%");
            Console.WriteLine($"  Accuracy (sample-lvl): {accuracy:F2}%   (TN={tn:N0})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [spikes] fetch/match failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

if (errors > 0) return 1;
if (receivedTotal < sentTotal * 0.95)
{
    Console.Error.WriteLine($"WARN: server acked only {receivedTotal} of {sentTotal} samples ({receivedTotal * 100.0 / sentTotal:F1}%)");
    return 1;
}
return 0;

static string MakeMac(int i) =>
    $"02:99:{(byte)(i >> 24):X2}:{(byte)(i >> 16):X2}:{(byte)(i >> 8):X2}:{(byte)i:X2}";

static void PrintLatency(string label, ConcurrentBag<long> samples)
{
    if (samples.IsEmpty)
    {
        Console.WriteLine($"  {label}: no samples");
        return;
    }
    var arr = samples.ToArray();
    Array.Sort(arr);
    long Pct(double p) => arr[Math.Min(arr.Length - 1, (int)(arr.Length * p))];
    static string Fmt(long us) => us >= 1000 ? $"{us / 1000.0:F1} ms" : $"{us} µs";
    Console.WriteLine(
        $"  {label}: n={arr.Length,5}  " +
        $"p50={Fmt(Pct(0.50)),9}  p95={Fmt(Pct(0.95)),9}  p99={Fmt(Pct(0.99)),9}  max={Fmt(arr[^1])}");
}

static ArgsBag ParseArgs(string[] argv)
{
    var bag = new ArgsBag();
    for (int i = 0; i < argv.Length; i++)
    {
        var a = argv[i];
        if (a.StartsWith("--"))
        {
            var key = a[2..];
            if (i + 1 >= argv.Length || argv[i + 1].StartsWith("--"))
            {
                bag.Set(key, "true");
            }
            else
            {
                bag.Set(key, argv[++i]);
            }
        }
    }
    return bag;
}

internal sealed class ArgsBag
{
    private readonly Dictionary<string, string> _v = new(StringComparer.OrdinalIgnoreCase);
    public void Set(string k, string v) => _v[k] = v;
    public string GetStr(string k, string def) => _v.TryGetValue(k, out var v) ? v : def;
    public int    GetInt(string k, int def)    => _v.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : def;
    public double GetDouble(string k, double def) => _v.TryGetValue(k, out var v)
        && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;
    public bool   GetBool(string k)            => _v.TryGetValue(k, out var v) && (v == "true" || v == "1");
}

internal sealed class LoginReply
{
    public string Token { get; set; } = "";
}

internal sealed class AnomalyResp
{
    public string DeviceMac { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public double Score { get; set; }
}
