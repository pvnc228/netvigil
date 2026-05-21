using System.Text.Json;
using System.Text.Json.Serialization;
using NetVigil.Shared;

namespace NetVigil.Server.Services.Anomaly
{
    public class IsolationForestDetector : IAnomalyDetector, IDisposable
    {
        public string Name => "isolation-forest";

        private const int ReservoirSize     = 10_000;
        private const int NumTrees          = 100;
        private const int SubSampleSize     = 256;
        private const int MinSamplesToTrain = 256;
        private const int SchemaVersion     = 3;
        private static readonly TimeSpan RetrainInterval = TimeSpan.FromSeconds(60);

        private const double SuspiciousScore = 0.55;
        private const double AnomalousScore  = 0.62;
        private const double CriticalScore   = 0.70;

        private readonly string _modelPath;
        private readonly ILogger<IsolationForestDetector> _logger;

        private readonly object _reservoirLock = new();
        private readonly double[]?[] _reservoir = new double[ReservoirSize][];
        private long _seenCount;

        private volatile IsolationForest? _forest;

        private readonly Timer _retrainTimer;
        private int _retraining;

        private DateTime? _lastTrainedAt;
        private long _lastTrainDurationMs;

        public IsolationForestDetector(IConfiguration config, ILogger<IsolationForestDetector> logger)
        {
            _logger = logger;
            _modelPath = config["Anomaly:ModelPath"] ?? "/app/data/anomaly-model.json";
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            TryLoad();
            _retrainTimer = new Timer(_ => RetrainSafe(), null, RetrainInterval, RetrainInterval);
        }

        public void Observe(string deviceMac, double mbps, DateTime timestamp)
        {
            var feat = ToFeatures(mbps, timestamp);
            lock (_reservoirLock)
            {
                long n = _seenCount;
                _seenCount++;
                if (n < ReservoirSize)
                {
                    _reservoir[(int)n] = feat;
                    return;
                }
                long j = (long)(Random.Shared.NextDouble() * (n + 1));
                if (j < ReservoirSize) _reservoir[(int)j] = feat;
            }
        }

        public AnomalyResult Score(string deviceMac, double mbps, DateTime timestamp)
        {
            var forest = _forest;
            if (forest is null || forest.Trees.Count == 0)
                return new AnomalyResult(0, false, RiskLevel.Normal, "warmup");

            var feat = ToFeatures(mbps, timestamp);
            var s = forest.Score(feat);

            var severity = s switch
            {
                >= CriticalScore   => RiskLevel.Critical,
                >= AnomalousScore  => RiskLevel.Anomalous,
                >= SuspiciousScore => RiskLevel.Suspicious,
                _                  => RiskLevel.Normal
            };
            var isAnomalous = severity >= RiskLevel.Anomalous;
            var reason = isAnomalous ? $"if score={s:F3}" : "ok";
            return new AnomalyResult(Math.Round(s, 4), isAnomalous, severity, reason);
        }

        private static double[] ToFeatures(double mbps, DateTime ts)
        {
            var hour = ts.ToUniversalTime().Hour + ts.Minute / 60.0;
            return new[]
            {
                mbps,
                Math.Sin(2 * Math.PI * hour / 24.0),
                Math.Cos(2 * Math.PI * hour / 24.0)
            };
        }

        private void RetrainSafe()
        {
            if (Interlocked.Exchange(ref _retraining, 1) == 1) return;
            try { Retrain(); }
            catch (Exception ex) { _logger.LogError(ex, "IF retrain failed"); }
            finally { Interlocked.Exchange(ref _retraining, 0); }
        }

        private void Retrain()
        {
            double[][] snapshot;
            lock (_reservoirLock)
            {
                int filled = (int)Math.Min(_seenCount, ReservoirSize);
                if (filled < MinSamplesToTrain)
                {
                    _logger.LogDebug("IF retrain skipped: {Filled}/{Min} samples", filled, MinSamplesToTrain);
                    return;
                }
                snapshot = new double[filled][];
                for (int i = 0; i < filled; i++) snapshot[i] = _reservoir[i]!;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var newForest = new IsolationForest(snapshot, NumTrees, SubSampleSize, seed: 0);
            sw.Stop();

            _forest = newForest; 
            _lastTrainedAt = DateTime.UtcNow;
            _lastTrainDurationMs = sw.ElapsedMilliseconds;
            _logger.LogInformation("IF retrained: {Trees}×{Sub} on {N} samples in {Ms}ms",
                NumTrees, SubSampleSize, snapshot.Length, sw.ElapsedMilliseconds);

            TrySave();
        }

        public DetectorStats GetStats()
        {
            var forest = _forest;
            int filled;
            long seen;
            lock (_reservoirLock)
            {
                seen = _seenCount;
                filled = (int)Math.Min(seen, ReservoirSize);
            }

            return new DetectorStats
            {
                DetectorKind          = Name,
                IsTrained             = forest is { Trees.Count: > 0 },
                SamplesSeen           = seen,
                ReservoirFilled       = filled,
                ReservoirCapacity     = ReservoirSize,
                Trees                 = forest?.Trees.Count ?? NumTrees,
                SubSampleSize         = forest?.SubSampleSize ?? SubSampleSize,
                MaxDepth              = (int)Math.Ceiling(Math.Log2(SubSampleSize)),
                LastTrainedAt         = _lastTrainedAt,
                LastTrainDurationMs   = _lastTrainedAt is null ? null : _lastTrainDurationMs,
                RetrainIntervalSeconds = (int)RetrainInterval.TotalSeconds,
                MbpsMin               = forest?.MbpsMin,
                MbpsMax               = forest?.MbpsMax,
                SuspiciousThreshold   = SuspiciousScore,
                AnomalousThreshold    = AnomalousScore,
                CriticalThreshold     = CriticalScore
            };
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private void TryLoad()
        {
            try
            {
                if (!File.Exists(_modelPath))
                {
                    _logger.LogInformation("No saved IF model at {Path} — starting fresh.", _modelPath);
                    return;
                }
                var json = File.ReadAllText(_modelPath);
                var state = JsonSerializer.Deserialize<ModelState>(json, JsonOpts);
                if (state is null || state.SchemaVersion != SchemaVersion)
                {
                    _logger.LogWarning(
                        "IF model schema mismatch (have {Have}, want {Want}) — discarding old weights.",
                        state?.SchemaVersion, SchemaVersion);
                    return;
                }
                _forest = state.Forest;
                _logger.LogInformation(
                    "Loaded IF forest from {Path}: {Trees} trees, sub-sample {Sub}.",
                    _modelPath, state.Forest?.Trees.Count ?? 0, state.Forest?.SubSampleSize ?? 0);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load IF model from {Path}", _modelPath); }
        }

        private void TrySave()
        {
            try
            {
                var state = new ModelState { SchemaVersion = SchemaVersion, Forest = _forest };
                var tmp = _modelPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
                File.Move(tmp, _modelPath, overwrite: true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to save IF model to {Path}", _modelPath); }
        }

        public void Dispose()
        {
            _retrainTimer.Dispose();
        }

        private sealed class ModelState
        {
            public int SchemaVersion { get; set; }
            public IsolationForest? Forest { get; set; }
        }
    }

    public sealed class IsolationForest
    {
        public List<IsolationTree> Trees { get; set; } = new();
        public int SubSampleSize { get; set; }

        public double MbpsMax { get; set; } = 1;

        public IsolationForest() { }

        public IsolationForest(double[][] rawSamples, int numTrees, int subSampleSize, int seed)
        {
            (MbpsMin, MbpsMax) = ComputeRange(rawSamples, featureIdx: 0);
            var normalized = NormalizeAll(rawSamples, MbpsMin, MbpsMax);

            SubSampleSize = Math.Min(subSampleSize, normalized.Length);
            int maxDepth = Math.Max(1, (int)Math.Ceiling(Math.Log2(SubSampleSize)));
            var rand = new Random(seed);
            Trees = new List<IsolationTree>(numTrees);
            for (int t = 0; t < numTrees; t++)
            {
                var sub = ReservoirSubsample(normalized, SubSampleSize, rand);
                Trees.Add(new IsolationTree(sub, maxDepth, rand));
            }
        }

        public double Score(double[] rawSample)
        {
            if (Trees.Count == 0) return 0;
            var normalized = NormalizeOne(rawSample, MbpsMin, MbpsMax);
            double sum = 0;
            for (int i = 0; i < Trees.Count; i++) sum += Trees[i].PathLength(normalized);
            double avg = sum / Trees.Count;
            double c = IsolationTree.AveragePathLength(SubSampleSize);
            if (c <= 0) return 0;
            return Math.Pow(2, -avg / c);
        }

        private static (double Min, double Max) ComputeRange(double[][] samples, int featureIdx)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < samples.Length; i++)
            {
                var v = samples[i][featureIdx];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min < 1e-9) max = min + 1;
            return (min, max);
        }

        private static double[][] NormalizeAll(double[][] src, double mMin, double mMax)
        {
            double range = mMax - mMin;
            var dst = new double[src.Length][];
            for (int i = 0; i < src.Length; i++)
            {
                var s = src[i];
                var t = new double[s.Length];
                t[0] = (s[0] - mMin) / range;
                for (int k = 1; k < s.Length; k++) t[k] = s[k];
                dst[i] = t;
            }
            return dst;
        }

        private static double[] NormalizeOne(double[] src, double mMin, double mMax)
        {
            double range = mMax - mMin;
            if (range < 1e-9) range = 1;
            var t = new double[src.Length];
            t[0] = (src[0] - mMin) / range;
            for (int k = 1; k < src.Length; k++) t[k] = src[k];
            return t;
        }

        private static double[][] ReservoirSubsample(double[][] src, int k, Random rand)
        {
            var sub = new double[k][];
            for (int i = 0; i < k; i++) sub[i] = src[i];
            for (int i = k; i < src.Length; i++)
            {
                int j = rand.Next(i + 1);
                if (j < k) sub[j] = src[i];
            }
            return sub;
        }
    }

    public sealed class IsolationTree
    {
        public Node? Root { get; set; }
        public int MaxDepth { get; set; }

        public IsolationTree() { }

        public IsolationTree(double[][] samples, int maxDepth, Random rand)
        {
            MaxDepth = maxDepth;
            Root = Build(samples, 0, rand);
        }

        private Node Build(double[][] samples, int depth, Random rand)
        {
            if (depth >= MaxDepth || samples.Length <= 1)
                return new Node { Size = samples.Length, Depth = depth };

            int nFeatures = samples[0].Length;
            int featureIdx = rand.Next(nFeatures);

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < samples.Length; i++)
            {
                var v = samples[i][featureIdx];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min < 1e-12)
                return new Node { Size = samples.Length, Depth = depth };

            double threshold = min + rand.NextDouble() * (max - min);

            int leftCount = 0;
            for (int i = 0; i < samples.Length; i++)
                if (samples[i][featureIdx] < threshold) leftCount++;

            var left  = new double[leftCount][];
            var right = new double[samples.Length - leftCount][];
            int li = 0, ri = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i][featureIdx] < threshold) left[li++] = samples[i];
                else right[ri++] = samples[i];
            }

            return new Node
            {
                FeatureIndex = featureIdx,
                Threshold = threshold,
                Left = Build(left, depth + 1, rand),
                Right = Build(right, depth + 1, rand),
                Depth = depth
            };
        }

        public double PathLength(double[] sample)
        {
            var node = Root;
            while (node != null && node.FeatureIndex.HasValue)
            {
                node = sample[node.FeatureIndex.Value] < node.Threshold!.Value
                    ? node.Left : node.Right;
            }
            if (node == null) return 0;
            return node.Depth + AveragePathLength(node.Size);
        }

        public static double AveragePathLength(int n)
        {
            if (n <= 1) return 0;
            const double EulerMascheroni = 0.5772156649;
            return 2 * (Math.Log(n - 1) + EulerMascheroni) - 2.0 * (n - 1) / n;
        }

        public sealed class Node
        {
            public int? FeatureIndex { get; set; }
            public double? Threshold { get; set; }
            public Node? Left { get; set; }
            public Node? Right { get; set; }
            public int Size { get; set; }
            public int Depth { get; set; }
        }
    }
}
