using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MinimalTowerDefenseMapV3
{
    internal static partial class Program
    {
        // ============================================================
        // P1 策略性分析器（软代价模型）
        // 规则约定：
        //   1. 自然障碍 = 硬墙（不可通行），由生成器产出；
        //   2. 玩家放置 = 软代价，格子仍可通行，但代价 = W（一格一物，不可叠加）；
        //   3. 因此永远不会「封死」，S→E 只要自然地图连通就恒可达；
        //   4. n 按格子数计。
        // 分析器只测量与出证书，不改变地图生成逻辑。
        // ============================================================

        private const int InfiniteCapacity = 1 << 20;
        private const int ProbeBlockCost = 10000;           // 用于探测「另一条走廊」的极高代价

        // P3：判据阈值统一来自 Profile（数值与旧常量逐位相同）。
        private static int AnalysisBlockCost = Profile.BlockCost;

        private sealed class DinicEdge
        {
            public int To;
            public int Capacity;
            public int Reverse;
        }

        // 节点拆分 + Dinic 最大流：用于自然地图的顶点最小割（仅作诊断）。
        private sealed class Dinic
        {
            private readonly List<DinicEdge>[] _graph;
            private readonly int[] _level;
            private readonly int[] _iter;

            public Dinic(int nodeCount)
            {
                _graph = new List<DinicEdge>[nodeCount];
                for (int i = 0; i < nodeCount; i++) _graph[i] = new List<DinicEdge>();
                _level = new int[nodeCount];
                _iter = new int[nodeCount];
            }

            public void AddEdge(int from, int to, int capacity)
            {
                _graph[from].Add(new DinicEdge
                {
                    To = to,
                    Capacity = capacity,
                    Reverse = _graph[to].Count
                });
                _graph[to].Add(new DinicEdge
                {
                    To = from,
                    Capacity = 0,
                    Reverse = _graph[from].Count - 1
                });
            }

            public int MaxFlow(int source, int sink)
            {
                int flow = 0;
                while (BuildLevels(source, sink))
                {
                    Array.Clear(_iter, 0, _iter.Length);
                    int pushed;
                    while ((pushed = SendFlow(source, sink, int.MaxValue)) > 0)
                        flow += pushed;
                }
                return flow;
            }

            private bool BuildLevels(int source, int sink)
            {
                Array.Fill(_level, -1);
                _level[source] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(source);

                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    foreach (DinicEdge edge in _graph[u])
                    {
                        if (edge.Capacity <= 0 || _level[edge.To] >= 0) continue;
                        _level[edge.To] = _level[u] + 1;
                        queue.Enqueue(edge.To);
                    }
                }

                return _level[sink] >= 0;
            }

            private int SendFlow(int u, int sink, int limit)
            {
                if (u == sink) return limit;

                for (; _iter[u] < _graph[u].Count; _iter[u]++)
                {
                    DinicEdge edge = _graph[u][_iter[u]];
                    if (edge.Capacity <= 0 || _level[edge.To] != _level[u] + 1) continue;

                    int pushed = SendFlow(edge.To, sink, Math.Min(limit, edge.Capacity));
                    if (pushed <= 0) continue;

                    edge.Capacity -= pushed;
                    _graph[edge.To][edge.Reverse].Capacity += pushed;
                    return pushed;
                }

                return 0;
            }

            public bool[] ReachableFrom(int source)
            {
                var seen = new bool[_graph.Length];
                var queue = new Queue<int>();
                seen[source] = true;
                queue.Enqueue(source);

                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    foreach (DinicEdge edge in _graph[u])
                    {
                        if (edge.Capacity <= 0 || seen[edge.To]) continue;
                        seen[edge.To] = true;
                        queue.Enqueue(edge.To);
                    }
                }

                return seen;
            }
        }

        private struct SwitchSample
        {
            public int Moves;
            public int Cost;
            public bool Tolled;
            public double Overlap;
            public double HalfSeparation;
            public double CentroidShift;
            public double Delta;
            public double KillzoneGain;
            public double Score;
            public List<Point> Blockers;
            public List<Point> NewPath;
        }

        private sealed class SwitchReport
        {
            public int N;
            public int Combos;
            public int Tolled;      // 敌人硬扛罚金，仍踩封锁格
            public int Switched;    // 敌人完全绕开封锁格
            public int EqualCost;   // 通行总代价与原路相同
            public int Passing;
            public SwitchSample? Best;
        }

        private sealed class MapAnalysis
        {
            public bool HasPath;
            public int PathMoves;
            public int BaseCost;
            public bool PrintedMatchesCanonical;
            public int MinCut;
            public List<Point> Choke = new List<Point>();
            public double Killzone;
            public int AlternativeCost = -1;
            public int SwitchPrediction = -1;   // 理论所需封锁格数，-1 = 不存在真正独立的替代走廊
            public List<SwitchReport> Reports = new List<SwitchReport>();
        }

        // ---------------- 基础图操作 ----------------

        // 寻路统一走 MapGeneratorV3_1.cs 里的 FindPath（P0 契约）：
        // 成本场 Dijkstra + (cost, y, x) 字典序 tie-break，生成器与分析器共用同一份实现。

        // 自然地图的顶点最小割 = 最大顶点不相交路径数（Menger）。
        // 软代价规则下它不再决定「能否封死」，只作为「有几条独立走廊」的诊断。
        private static (int Flow, List<Point> Cut) ComputeMinVertexCut(
            bool[,] obstacles, int width, int height, Point spawn, Point exit)
        {
            var cells = new List<Point>();
            var indices = new Dictionary<Point, int>();

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (obstacles[x, y]) continue;
                var cell = new Point(x, y);
                indices[cell] = cells.Count;
                cells.Add(cell);
            }

            int count = cells.Count;
            var dinic = new Dinic(count * 2);

            for (int i = 0; i < count; i++)
            {
                int capacity = (cells[i].Equals(spawn) || cells[i].Equals(exit))
                    ? InfiniteCapacity
                    : 1;
                dinic.AddEdge(2 * i, 2 * i + 1, capacity);
            }

            for (int i = 0; i < count; i++)
            {
                Point cell = cells[i];
                foreach (Direction direction in Directions)
                {
                    int x = cell.X + direction.Dx;
                    int y = cell.Y + direction.Dy;
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    if (obstacles[x, y]) continue;

                    dinic.AddEdge(2 * i + 1, 2 * indices[new Point(x, y)], InfiniteCapacity);
                }
            }

            int source = 2 * indices[spawn];
            int sink = 2 * indices[exit] + 1;
            int flow = dinic.MaxFlow(source, sink);

            var cut = new List<Point>();
            if (flow > 0)
            {
                bool[] reachable = dinic.ReachableFrom(source);
                for (int i = 0; i < count; i++)
                {
                    if (reachable[2 * i] && !reachable[2 * i + 1]) cut.Add(cells[i]);
                }
            }

            return (flow, cut);
        }

        // ---------------- 度量 ----------------

        private static double JaccardOverlap(List<Point> a, List<Point> b)
        {
            var setA = new HashSet<Point>(a);
            var setB = new HashSet<Point>(b);
            int intersection = setA.Count(cell => setB.Contains(cell));
            int union = setA.Count + setB.Count - intersection;
            return union == 0 ? 1.0 : (double)intersection / union;
        }

        private static Point Centroid(List<Point> path)
        {
            double x = 0;
            double y = 0;
            foreach (Point p in path)
            {
                x += p.X;
                y += p.Y;
            }
            return new Point((int)Math.Round(x / path.Count), (int)Math.Round(y / path.Count));
        }

        private static double CentroidShift(List<Point> a, List<Point> b)
        {
            Point ca = Centroid(a);
            Point cb = Centroid(b);
            double dx = cb.X - ca.X;
            double dy = cb.Y - ca.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // 半场分离：只统计「对方路径没走过」的格子落在自己一侧的比例。
        // θ 骨架的共享茎段同时属于两条路线，若计入会把分离度错误地压到 0.5 附近。
        private static double HalfSeparation(List<Point> a, List<Point> b)
        {
            Point ca = Centroid(a);
            Point cb = Centroid(b);
            double dx = cb.X - ca.X;
            double dy = cb.Y - ca.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 1e-9) return 0;

            double mx = (ca.X + cb.X) / 2.0;
            double my = (ca.Y + cb.Y) / 2.0;

            var setA = new HashSet<Point>(a);
            var setB = new HashSet<Point>(b);
            var exclusiveA = a.Where(p => !setB.Contains(p)).ToList();
            var exclusiveB = b.Where(p => !setA.Contains(p)).ToList();

            if (exclusiveA.Count == 0 || exclusiveB.Count == 0) return 0;

            int ownA = exclusiveA.Count(p => (p.X - mx) * dx + (p.Y - my) * dy <= 0);
            int ownB = exclusiveB.Count(p => (p.X - mx) * dx + (p.Y - my) * dy > 0);

            return Math.Min((double)ownA / exclusiveA.Count, (double)ownB / exclusiveB.Count);
        }

        // 火力覆盖：路径每格周围 2 格内、可建造（非障碍、非路径、非门）的空格数，按路径长度平均。
        private static double ComputeKillzone(List<Point> path, MapData map)
        {
            var pathSet = new HashSet<Point>(path);

            int slots = 0;
            foreach (Point cell in path)
            {
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int x = cell.X + dx;
                    int y = cell.Y + dy;
                    if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
                    if (map.Obstacles[x, y]) continue;

                    var candidate = new Point(x, y);
                    if (pathSet.Contains(candidate)) continue;
                    if (candidate.Equals(map.Spawn) || candidate.Equals(map.Exit)) continue;
                    slots++;
                }
            }

            return path.Count == 0 ? 0 : (double)slots / path.Count;
        }

        private static bool PassesOtherHalf(
            bool switched, double overlap, double halfSeparation, double delta)
            => switched
               && overlap <= Profile.OverlapLimit
               && halfSeparation >= Profile.HalfSeparationLimit
               && delta >= Profile.DeltaMin
               && delta <= Profile.DeltaMax;

        private static double ScoreSwitch(
            bool switched, double overlap, double halfSeparation, double delta, double killzoneGain)
        {
            if (!switched) return 0;

            double deltaScore = Math.Clamp(delta / Profile.DeltaMax, 0, 1);
            double killzoneScore = Math.Clamp(killzoneGain, 0, 1);
            return 0.35 * halfSeparation
                   + 0.30 * (1.0 - overlap)
                   + 0.25 * deltaScore
                   + 0.10 * killzoneScore;
        }

        private static void EnumerateCombinations(
            List<Point> cells, int size, int maxCombos, Func<int[], bool> visit)
        {
            if (size <= 0 || cells.Count < size) return;

            var pick = new int[size];
            int visited = 0;

            bool Walk(int depth, int start)
            {
                if (depth == size)
                {
                    visited++;
                    bool keepGoing = visit(pick);
                    return keepGoing && visited < maxCombos;
                }

                for (int i = start; i < cells.Count; i++)
                {
                    pick[depth] = i;
                    if (!Walk(depth + 1, i + 1)) return false;
                }

                return true;
            }

            Walk(0, 0);
        }

        // ---------------- 单次封锁评估 ----------------

        private static SwitchReport EvaluateSwitches(
            MapData map,
            List<Point> basePath,
            int baseCost,
            double baseKillzone,
            int n,
            int maxCombos,
            int blockCost)
        {
            var report = new SwitchReport { N = n };

            var candidates = basePath
                .Where(p => !p.Equals(map.Spawn) && !p.Equals(map.Exit))
                .ToList();

            if (candidates.Count < n) return report;

            var blocked = new HashSet<Point>();
            double bestScore = double.NegativeInfinity;
            SwitchSample? best = null;
            var baseSet = new HashSet<Point>(basePath);

            EnumerateCombinations(candidates, n, maxCombos, pick =>
            {
                blocked.Clear();
                foreach (int index in pick) blocked.Add(candidates[index]);
                report.Combos++;

                List<Point> nextPath = FindPath(
                    map.Spawn, map.Exit, map.Obstacles, map.Width, map.Height,
                    blocked, blockCost, out int nextCost);

                if (nextPath.Count == 0) return true;   // 软代价下不会发生，保险起见

                bool tolled = nextPath.Any(cell => blocked.Contains(cell));
                bool routeChanged = !nextPath.SequenceEqual(basePath);

                if (tolled) report.Tolled++;
                if (!tolled && routeChanged) report.Switched++;
                if (nextCost == baseCost) report.EqualCost++;

                double overlap = JaccardOverlap(basePath, nextPath);
                double halfSeparation = HalfSeparation(basePath, nextPath);
                double shift = CentroidShift(basePath, nextPath);
                double delta = (double)(nextCost - baseCost) / Math.Max(1, baseCost);
                double killzone = ComputeKillzone(nextPath, map);
                double killzoneGain = (killzone - baseKillzone) / Math.Max(0.001, baseKillzone);

                bool switched = !tolled && routeChanged;
                if (PassesOtherHalf(switched, overlap, halfSeparation, delta)) report.Passing++;

                double score = ScoreSwitch(switched, overlap, halfSeparation, delta, killzoneGain);
                if (score <= bestScore) return true;

                bestScore = score;
                best = new SwitchSample
                {
                    Moves = nextPath.Count - 1,
                    Cost = nextCost,
                    Tolled = tolled,
                    Overlap = overlap,
                    HalfSeparation = halfSeparation,
                    CentroidShift = shift,
                    Delta = delta,
                    KillzoneGain = killzoneGain,
                    Score = score,
                    Blockers = blocked.ToList(),
                    NewPath = nextPath
                };

                return true;
            });

            report.Best = best;
            return report;
        }

        private static MapAnalysis AnalyzeMap(MapData map, int maxN, int maxCombos)
        {
            var analysis = new MapAnalysis();

            List<Point> basePath = FindPath(
                map.Spawn, map.Exit, map.Obstacles, map.Width, map.Height,
                null, 0, out int baseCost);

            analysis.HasPath = basePath.Count > 0;
            if (!analysis.HasPath) return analysis;

            analysis.PathMoves = basePath.Count - 1;
            analysis.BaseCost = baseCost;
            analysis.PrintedMatchesCanonical = map.Path.Count == basePath.Count
                                               && map.Path.SequenceEqual(basePath);

            (int flow, List<Point> cut) = ComputeMinVertexCut(
                map.Obstacles, map.Width, map.Height, map.Spawn, map.Exit);
            analysis.MinCut = flow;
            analysis.Choke = cut;

            analysis.Killzone = ComputeKillzone(basePath, map);

            // 替代走廊探测：把主路内部格设为极高代价（近似硬墙）后取得的最优路。
            // 探针同样会抬高低价共享格（θ 的茎段），因此要把这些格的溢价扣回去。
            var inner = new HashSet<Point>(basePath.Where(p => !p.Equals(map.Spawn) && !p.Equals(map.Exit)));
            List<Point> alternative = FindPath(
                map.Spawn, map.Exit, map.Obstacles, map.Width, map.Height,
                inner, ProbeBlockCost, out int alternativeCost);

            int sharedProbeCells = alternative.Count(cell => inner.Contains(cell));
            int adjustedAlternativeCost = alternativeCost - sharedProbeCells * (ProbeBlockCost - 1);

            bool hasAlternative = alternative.Count > 0 && !alternative.SequenceEqual(basePath);
            analysis.AlternativeCost = hasAlternative ? adjustedAlternativeCost : -1;
            analysis.SwitchPrediction = hasAlternative
                ? Math.Max(0, (int)Math.Ceiling(
                    (double)(adjustedAlternativeCost - baseCost) / Math.Max(1, AnalysisBlockCost - 1)))
                : -1;

            for (int n = 1; n <= maxN; n++)
            {
                analysis.Reports.Add(EvaluateSwitches(
                    map, basePath, baseCost, analysis.Killzone, n, maxCombos, AnalysisBlockCost));
            }

            return analysis;
        }

        // ---------------- 输出 ----------------

        private static void PrintAnalysis(MapData map, MapAnalysis analysis)
        {
            Console.WriteLine();
            Console.WriteLine($"=== P1 strategic analysis (soft-cost model, W = {AnalysisBlockCost}) ===");

            if (!analysis.HasPath)
            {
                Console.WriteLine("No route between gates.");
                return;
            }

            Console.WriteLine($"Cheapest path: {analysis.PathMoves} moves / cost {analysis.BaseCost}"
                              + $" (printed path matches: {(analysis.PrintedMatchesCanonical ? "yes" : "no")})");
            Console.WriteLine($"Natural min-cut (diagnostic): {analysis.MinCut}"
                              + (analysis.Choke.Count > 0
                                  ? "; choke cells = " + string.Join(", ", analysis.Choke)
                                  : "; no finite choke"));
            Console.WriteLine(analysis.SwitchPrediction >= 0
                ? $"Disjoint alternative corridor: cost {analysis.AlternativeCost}"
                  + $" -> predicted n_switch = {analysis.SwitchPrediction}"
                : "Disjoint alternative corridor: none (all alternatives share the main route)");
            Console.WriteLine($"Killzone exposure: {analysis.Killzone:F2} tower slots per path cell");

            foreach (SwitchReport report in analysis.Reports)
            {
                if (report.Best == null)
                {
                    Console.WriteLine($"Blocking n={report.N}: no candidate combos.");
                    continue;
                }

                SwitchSample best = report.Best.Value;
                Console.WriteLine(
                    $"Blocking n={report.N}: combos {report.Combos}, tolled {report.Tolled}, "
                    + $"switched {report.Switched}, equal-cost {report.EqualCost}");
                Console.WriteLine(
                    $"  best: overlap {best.Overlap:F2}, half-sep {best.HalfSeparation:F2}, "
                    + $"shift {best.CentroidShift:F1}, delta {best.Delta:+0.0%;-0.0%}, "
                    + $"killzone {best.KillzoneGain:+0.0%;-0.0%} -> other-half "
                    + (PassesOtherHalf(!best.Tolled, best.Overlap, best.HalfSeparation, best.Delta)
                        ? "PASS" : "FAIL"));
                Console.WriteLine(
                    $"  blockers: {string.Join(", ", best.Blockers)}; new path {best.Moves} moves / cost {best.Cost}");
            }
        }

        private static readonly string[] CsvHeader =
        {
            "seed", "status", "width", "height", "obstacles", "printed_moves", "canonical_moves",
            "printed_matches", "turns", "mincut_natural", "choke_count", "w", "n", "combos",
            "tolled", "switched", "equal_cost", "best_overlap", "best_half_sep", "best_shift",
            "best_delta", "best_killzone_gain", "passes_other_half", "cert_blockers", "cert_moves",
            "n_switch_pred"
        };

        // 统一补齐列数，避免不同状态行错位。
        private static void AddRow(List<string> rows, List<string> fields)
        {
            while (fields.Count < CsvHeader.Length) fields.Add(string.Empty);
            rows.Add(string.Join(",", fields));
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

        private static void RunAnalyzerBatch(string[] args)
        {
            int count = 200;
            int maxN = Profile.AnalysisN;
            int maxCombos = Profile.ComboCap;
            int baseSeed = Settings.RandomSeed;
            AnalysisBlockCost = Profile.BlockCost;
            string csvPath = Path.Combine("analysis", "baseline_v3_soft.csv");

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--count":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCount) && parsedCount > 0)
                        {
                            count = parsedCount;
                            i++;
                        }
                        break;
                    case "--nmax":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedN)
                            && parsedN >= 1 && parsedN <= 3)
                        {
                            maxN = parsedN;
                            i++;
                        }
                        break;
                    case "--combos":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCombos)
                            && parsedCombos > 0)
                        {
                            maxCombos = parsedCombos;
                            i++;
                        }
                        break;
                    case "--seed":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedSeed))
                        {
                            baseSeed = parsedSeed;
                            i++;
                        }
                        break;
                    case "--w":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedW) && parsedW >= 2)
                        {
                            AnalysisBlockCost = parsedW;
                            i++;
                        }
                        break;
                    case "--csv":
                        if (i + 1 < args.Length)
                        {
                            csvPath = args[i + 1];
                            i++;
                        }
                        break;
                }
            }

            Console.WriteLine("=== P1 analyzer (soft-cost blocking) ===");
            Console.WriteLine($"maps: {count} | n = 1..{maxN} | W = {AnalysisBlockCost} | "
                              + $"combo cap: {maxCombos}/n | base seed: {baseSeed}");
            Console.WriteLine($"csv: {Path.GetFullPath(csvPath)}");
            Console.WriteLine();

            var rows = new List<string> { string.Join(",", CsvHeader) };

            var kCounts = new SortedDictionary<int, int>();
            var passCounts = new int[maxN + 1];
            var comboCnt = new int[maxN + 1];
            var tolledCnt = new int[maxN + 1];
            var switchedCnt = new int[maxN + 1];
            var equalCostCnt = new int[maxN + 1];
            var overlapSum = new double[maxN + 1];
            var halfSepSum = new double[maxN + 1];
            var deltaSum = new double[maxN + 1];
            var shiftSum = new double[maxN + 1];

            int generated = 0;
            int analysable = 0;
            int generationFailures = 0;
            int printedMatches = 0;
            var predictions = new List<int>();

            for (int i = 0; i < count; i++)
            {
                int seed = unchecked(baseSeed + i * 7919);
                var random = new Random(seed);
                MapData? map = GenerateMap(random, seed);

                if (map == null)
                {
                    generationFailures++;
                    AddRow(rows, new List<string>
                    {
                        Num(seed), "generation_failed",
                        Num(Settings.MapWidth), Num(Settings.MapHeight)
                    });
                    continue;
                }

                generated++;
                MapAnalysis analysis = AnalyzeMap(map, maxN, maxCombos);

                if (!analysis.HasPath)
                {
                    AddRow(rows, new List<string>
                    {
                        Num(seed), "unreachable", Num(map.Width), Num(map.Height),
                        Num(map.ObstacleCount), Num(map.Path.Count - 1)
                    });
                    continue;
                }

                analysable++;
                if (analysis.PrintedMatchesCanonical) printedMatches++;
                if (analysis.SwitchPrediction >= 0) predictions.Add(analysis.SwitchPrediction);

                kCounts.TryGetValue(analysis.MinCut, out int kSeen);
                kCounts[analysis.MinCut] = kSeen + 1;

                foreach (SwitchReport report in analysis.Reports)
                {
                    int n = report.N;
                    comboCnt[n] += report.Combos;
                    tolledCnt[n] += report.Tolled;
                    switchedCnt[n] += report.Switched;
                    equalCostCnt[n] += report.EqualCost;

                    var row = new List<string>
                    {
                        Num(seed), "ok", Num(map.Width), Num(map.Height), Num(map.ObstacleCount),
                        Num(map.Path.Count - 1), Num(analysis.PathMoves),
                        analysis.PrintedMatchesCanonical ? "1" : "0",
                        Num(map.Metrics.Turns), Num(analysis.MinCut), Num(analysis.Choke.Count),
                        Num(AnalysisBlockCost), Num(n), Num(report.Combos),
                        Num(report.Tolled), Num(report.Switched), Num(report.EqualCost)
                    };

                    if (report.Best == null)
                    {
                        for (int pad = 0; pad < 5; pad++) row.Add(string.Empty);
                        row.Add("0");
                        row.Add(string.Empty);
                        row.Add(string.Empty);
                        row.Add(Num(analysis.SwitchPrediction));
                        AddRow(rows, row);
                        continue;
                    }

                    SwitchSample best = report.Best.Value;
                    bool passes = PassesOtherHalf(
                        !best.Tolled, best.Overlap, best.HalfSeparation, best.Delta);

                    if (passes) passCounts[n]++;
                    overlapSum[n] += best.Overlap;
                    halfSepSum[n] += best.HalfSeparation;
                    deltaSum[n] += best.Delta;
                    shiftSum[n] += best.CentroidShift;

                    row.Add(Num(best.Overlap, "F3"));
                    row.Add(Num(best.HalfSeparation, "F3"));
                    row.Add(Num(best.CentroidShift, "F2"));
                    row.Add(Num(best.Delta, "F3"));
                    row.Add(Num(best.KillzoneGain, "F3"));
                    row.Add(passes ? "1" : "0");
                    row.Add("\"" + string.Join("|", best.Blockers) + "\"");
                    row.Add(Num(best.Moves));
                    row.Add(Num(analysis.SwitchPrediction));
                    AddRow(rows, row);
                }

                if ((i + 1) % 25 == 0)
                    Console.WriteLine($"  processed {i + 1}/{count} (generated {generated}, failures {generationFailures})");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine("=== summary ===");
            Console.WriteLine($"maps requested {count} | generated {generated} | generation failures {generationFailures}");
            Console.WriteLine("natural min-cut distribution: "
                              + string.Join(", ", kCounts.Select(pair => $"k={pair.Key}:{pair.Value}")));
            Console.WriteLine($"printed path equals canonical path: {printedMatches}/{analysable}"
                              + $" ({(double)printedMatches / Math.Max(1, analysable):P1})");

            if (predictions.Count > 0)
            {
                Console.WriteLine("n_switch prediction: "
                                  + string.Join(", ", predictions
                                      .GroupBy(value => value)
                                      .OrderBy(group => group.Key)
                                      .Select(group => $"n={group.Key}:{group.Count()}")));
            }
            else
            {
                Console.WriteLine("n_switch prediction: none (no disjoint alternative corridor)");
            }

            for (int n = 1; n <= maxN; n++)
            {
                if (comboCnt[n] == 0) continue;

                double passRate = (double)passCounts[n] / Math.Max(1, analysable);
                double tolledRate = (double)tolledCnt[n] / comboCnt[n];
                double switchedRate = (double)switchedCnt[n] / comboCnt[n];
                double equalRate = (double)equalCostCnt[n] / comboCnt[n];
                double avgOverlap = overlapSum[n] / Math.Max(1, analysable);
                double avgHalfSep = halfSepSum[n] / Math.Max(1, analysable);
                double avgDelta = deltaSum[n] / Math.Max(1, analysable);
                double avgShift = shiftSum[n] / Math.Max(1, analysable);

                Console.WriteLine(
                    $"n={n}: other-half pass {passCounts[n]}/{analysable} ({passRate:P1}) | "
                    + $"avg overlap {avgOverlap:F2} | avg half-sep {avgHalfSep:F2} | "
                    + $"avg delta {avgDelta:+0.0%;-0.0%} | avg shift {avgShift:F1} cells | "
                    + $"tolled {tolledRate:P1} | switched {switchedRate:P1} | equal-cost {equalRate:P1}");
            }

            Console.WriteLine();
            Console.WriteLine("legend: switched = route fully avoids blocked cells; "
                              + "tolled = route still steps on blocked cells and pays W; "
                              + "other-half pass = switched AND overlap<=0.35 AND half-sep>=0.70 AND delta in [8%,35%]");
        }

        // 尺寸扫描：11x11 → 16x16，每个尺寸各自批量分析。
        private static void RunSizeSweep(string[] args)
        {
            int from = 11;
            int to = 16;
            int count = 50;
            int maxN = Profile.AnalysisN;
            int maxCombos = Profile.ComboCap;
            int baseSeed = Settings.RandomSeed;
            string directory = Path.Combine("analysis", "sweep");
            AnalysisBlockCost = Profile.BlockCost;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--from":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedFrom) && parsedFrom >= 4)
                        {
                            from = parsedFrom;
                            i++;
                        }
                        break;
                    case "--to":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedTo) && parsedTo >= 4)
                        {
                            to = parsedTo;
                            i++;
                        }
                        break;
                    case "--count":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCount) && parsedCount > 0)
                        {
                            count = parsedCount;
                            i++;
                        }
                        break;
                    case "--nmax":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedN)
                            && parsedN >= 1 && parsedN <= 3)
                        {
                            maxN = parsedN;
                            i++;
                        }
                        break;
                    case "--combos":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCombos)
                            && parsedCombos > 0)
                        {
                            maxCombos = parsedCombos;
                            i++;
                        }
                        break;
                    case "--seed":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedSeed))
                        {
                            baseSeed = parsedSeed;
                            i++;
                        }
                        break;
                    case "--w":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedW) && parsedW >= 2)
                        {
                            AnalysisBlockCost = parsedW;
                            i++;
                        }
                        break;
                    case "--out":
                        if (i + 1 < args.Length)
                        {
                            directory = args[i + 1];
                            i++;
                        }
                        break;
                }
            }

            Console.WriteLine("=== P1 size sweep (soft-cost blocking) ===");
            Console.WriteLine($"sizes {from}x{from} .. {to}x{to} | {count} maps per size | "
                              + $"W = {AnalysisBlockCost} | n = 1..{maxN}");

            Directory.CreateDirectory(directory);

            for (int size = from; size <= to; size++)
            {
                Settings.MapWidth = size;
                Settings.MapHeight = size;

                Console.WriteLine();
                Console.WriteLine($"------------------- {size} x {size} -------------------");

                RunAnalyzerBatch(new[]
                {
                    "sweep",
                    "--count", Num(count),
                    "--nmax", Num(maxN),
                    "--combos", Num(maxCombos),
                    "--seed", Num(unchecked(baseSeed + size * 101)),
                    "--w", Num(AnalysisBlockCost),
                    "--csv", Path.Combine(directory, $"sweep_{size}x{size}.csv")
                });
            }
        }

        private static void WaitForExitKey()
        {
            if (Console.IsInputRedirected) return;
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
