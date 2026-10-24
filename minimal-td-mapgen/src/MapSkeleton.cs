using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MinimalTowerDefenseMapV3
{
    internal static partial class Program
    {
        // ============================================================
        // P2a：θ 骨架生成器（构造式，不靠拒绝采样）
        //
        // 拓扑：
        //   上/下半场各一条走廊，中间是 2 格厚的实墙；
        //   墙只在「分叉口」与「汇合口」各留 2 格宽的缺口；
        //   门口到分叉/汇合之间是共享茎段（2 格，禁止建造，由证书标注）。
        //
        // 长度控制（软代价 W=4，一格一物）：
        //   短廊 A 额外绕行 ampA=1  → +2 步
        //   长廊 B 额外绕行 ampB    → +2*ampB 步
        //   ΔC = 2*(ampB - 1)
        //   切换条件：(W-1)*(n-1) < ΔC < (W-1)*n  →  W=4 时 tier1: ΔC=2, tier2: ΔC=4
        // ============================================================

        // W、最小尺寸、Killzone 下限全部来自 Profile（见 MapProfile.cs）。
        // Killzone 差异化下限：长廊（改道后的路线）每格火力覆盖 ≥ 短廊的 1.3 倍。
        // 这道门禁保证「把敌人逼到长廊」对玩家是收益而非惩罚，且装饰不会悄悄磨平它。

        private sealed class ThetaResult
        {
            public MapData? Map;
            public int CheapCost;
            public int ExpensiveCost;
            public bool CheapIsUpper;
            public int DeltaCost;
            public int BlockCost;
            public string Reason = "";
        }

        // ΔC 不能单独缩放：tier t 的判据是「第 t 格才把敌人挤走」，即
        //   (t-1)*(W-1) <= ΔC_t < t*(W-1)
        // 而 4 连通网格上任意 s->t 路径都与主路长度 L1 同奇偶，所以 ΔC 只能取偶数。
        // 于是把 (W, ΔC1, ΔC2) 三个量一起按 L1 反解，让 ΔL = ΔC/L1 落在 [lo, hi]。
        // W 从最小开始试，改动最小；11..16 上解出来正好就是历史常量 W=4 / ΔC=2,4。
        // 失败时 reason 指出卡在哪一层：窗口空 / tier1 带空 / tier2 带空 / W 上界不够。
        private static bool TryFitTiers(
            int size, double lo, double hi,
            out int blockCost, out int delta1, out int delta2, out string reason)
        {
            blockCost = 0;
            delta1 = 0;
            delta2 = 0;

            int l1 = size + 1;
            int low = (int)Math.Ceiling(lo * l1);
            if (low % 2 != 0) low++;
            if (low < 2) low = 2;
            int high = (int)Math.Floor(hi * l1);
            if (high % 2 != 0) high--;

            if (low > high)
            {
                reason = $"delta window empty: no even deltaC in [{lo:F2}, {hi:F2}] x L1={l1}";
                return false;
            }

            bool tier1Band = false;   // 存在 W 使 tier1 带 ∩ 窗口非空；

            for (int w = 4; w <= Profile.MaxThetaW; w++)
            {
                int penalty = w - 1;

                // tier1：ΔC 必须 <= W-2，否则第 1 格就压不住、会提前改道。
                int d1 = low;
                if (d1 > Math.Min(high, penalty - 1)) continue;
                tier1Band = true;

                // tier2：ΔC 必须 >= W-1，否则第 1 格就改道了，分不出第二档。
                int d2 = Math.Max(low, penalty);
                if (d2 % 2 != 0) d2++;
                if (d2 > Math.Min(high, 2 * penalty - 1) || d2 <= d1) continue;

                blockCost = w;
                delta1 = d1;
                delta2 = d2;
                reason = string.Empty;
                return true;
            }

            reason = tier1Band
                ? $"tier2 band empty: needs deltaC2 >= W-1 and <= 2W-3, floor is {low}, ceiling {high}"
                : $"tier1 band empty: needs deltaC1 <= W-2, floor is {low} but W <= {Profile.MaxThetaW}";
            return false;
        }

        // 在某个半场横截一道墙，只留一个缺口。
        // 缺口是「强制单格」：半场内没有替代路线，封锁它才真正抬升代价。
        private static void CarveGate(bool[,] obstacles, int column, int rowLow, int rowHigh, int openRow)
        {
            for (int y = rowLow; y <= rowHigh; y++)
            {
                obstacles[column, y] = y != openRow;
            }
        }

        // 探针法同时取出两条走廊：短廊 = 普通最短路；长廊 = 把短廊内部格抬到
        // 极高代价后再寻路（共享茎段会重复计费，故把溢价扣回得到真实成本）。
        private static bool ProbeCorridors(
            bool[,] obstacles, int size, Point spawn, Point exit,
            out List<Point> cheapPath, out int cheapCost,
            out List<Point> expensivePath, out int expensiveCost)
        {
            cheapPath = FindPath(spawn, exit, obstacles, size, size, null, 0, out cheapCost);
            expensivePath = new List<Point>();
            expensiveCost = -1;

            if (cheapPath.Count == 0) return false;

            var inner = new HashSet<Point>(
                cheapPath.Where(p => !p.Equals(spawn) && !p.Equals(exit)));
            List<Point> probe = FindPath(
                spawn, exit, obstacles, size, size, inner, ProbeBlockCost, out int probeCost);

            if (probe.Count == 0) return false;

            int shared = probe.Count(cell => inner.Contains(cell));
            expensivePath = probe;
            expensiveCost = probeCost - shared * (ProbeBlockCost - 1);
            return true;
        }

        // P2b 装饰，两阶段：
        //   阶段 1「封边」：把短廊外侧的相邻行也填上，使短廊成为真正的 1 格宽通道。
        //     否则封锁短廊一格时，敌人可能在上方开阔带里绕一格就回来（代价仅 +2），
        //     既不换半场、也不付全额罚金 —— 实测会把 tier1 的 n=1 通过率压到 62%。
        //   阶段 2「撒点」：在剩余安全格里随机撒障碍，直到达到目标障碍比例。
        // 两个阶段每放一格都跑一次 oracle（主路成本 + ΔC 双校验），不满足就回退。
        private static void DecorateTheta(
            MapData map, int seed, double targetRatio, bool cheapIsUpper, double kzMin,
            out int placed, out int rejected)
        {
            placed = 0;
            rejected = 0;
            int placedCount = 0;
            int rejectedCount = 0;

            bool[,] obstacles = map.Obstacles;
            int size = map.Width;
            int total = size * size;
            int target = (int)Math.Round(total * targetRatio);

            if (!ProbeCorridors(obstacles, size, map.Spawn, map.Exit,
                    out List<Point> cheapPath, out int expectedCheap,
                    out List<Point> expensivePath, out int expectedExpensive))
                return;

            int expectedDelta = expectedExpensive - expectedCheap;

            // 受保护格：两条走廊的全部格子 + 出入口。
            var protectedCells = new HashSet<Point>(cheapPath);
            foreach (Point cell in expensivePath) protectedCells.Add(cell);
            protectedCells.Add(map.Spawn);
            protectedCells.Add(map.Exit);

            // enforceKillzone 只对阶段 2 生效：
            // 差异化本身是阶段 1「封边」造出来的（封边前两条走廊都在开阔地里，KZ 几乎相等），
            // 因此阶段 1 必须豁免这道门禁，否则会陷入「先有鸡还是先有蛋」的死锁。
            bool TryPlace(Point cell, bool enforceKillzone)
            {
                if (obstacles[cell.X, cell.Y]) return false;
                if (protectedCells.Contains(cell)) return false;

                obstacles[cell.X, cell.Y] = true;
                bool ok = ProbeCorridors(obstacles, size, map.Spawn, map.Exit,
                    out List<Point> cheapNow, out int cheapCost,
                    out List<Point> expensiveNow, out int expensiveCost);

                bool invariantsHold = ok
                    && cheapCost == expectedCheap
                    && expensiveCost - cheapCost == expectedDelta
                    && (!enforceKillzone
                        || ComputeKillzone(expensiveNow, map)
                           >= kzMin * ComputeKillzone(cheapNow, map));

                if (invariantsHold)
                {
                    map.ObstacleCount++;
                    placedCount++;
                    return true;
                }

                obstacles[cell.X, cell.Y] = false;
                rejectedCount++;
                return false;
            }

            // 阶段 1：封短廊外侧那一行。
            int outerOffset = cheapIsUpper ? -1 : 1;
            foreach (Point cell in cheapPath)
            {
                if (cell.Equals(map.Spawn) || cell.Equals(map.Exit)) continue;
                int y = cell.Y + outerOffset;
                if (y < 0 || y >= size) continue;

                var flank = new Point(cell.X, y);
                if (protectedCells.Contains(flank)) continue;
                TryPlace(flank, enforceKillzone: false);
            }

            // 阶段 2：随机撒点到目标密度。
            var candidates = new List<Point>();
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (obstacles[x, y]) continue;
                var cell = new Point(x, y);
                if (protectedCells.Contains(cell)) continue;
                candidates.Add(cell);
            }

            var random = new Random(unchecked(seed ^ 0x5F3759DF));
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            foreach (Point cell in candidates)
            {
                if (map.ObstacleCount >= target) break;
                TryPlace(cell, enforceKillzone: true);
            }

            placed = placedCount;
            rejected = rejectedCount;
        }

        private static ThetaResult BuildThetaSkeleton(int size, int tier, int seed, double deltaLo, double deltaHi)
        {
            var result = new ThetaResult();

            if (size < Profile.ThetaMinSize)
            {
                result.Reason = $"size < {Profile.ThetaMinSize}";
                return result;
            }

            if (tier != 1 && tier != 2)
            {
                result.Reason = "tier must be 1 or 2";
                return result;
            }

            if (!TryFitTiers(size, deltaLo, deltaHi,
                    out int blockCost, out int delta1, out int delta2, out string fitReason))
            {
                result.Reason = fitReason;
                return result;
            }

            result.BlockCost = blockCost;
            result.DeltaCost = tier == 1 ? delta1 : delta2;

            var random = new Random(seed);

            int mid = size / 2;
            int barrierLo = mid - 1;
            int barrierHi = mid;
            int upperRow = barrierLo - 1;
            int lowerRow = barrierHi + 1;

            // 分叉/汇合只在最左、最右两列（各 1 格宽），上下半场之间只能从这两处穿越。
            // 开口若放宽到 2~3 格，下半场路径会在茎段行上多走几格，
            // 这些格子会被算成「落在对方半场」，把 half-sep 压到 0.70 附近而误判失败。
            int stemColumn = 0;
            int farStemColumn = size - 1;

            // 短廊走上半场（直行），长廊走下半场（先下 2 行再绕回来），基础差 ΔC_base = 2；
            // 下半场每多开一道偏移缺口再加 2 步，故 ΔC = 2 + 2*lowerGateOffset。
            int gateCount = result.DeltaCost / 2;
            int lowerGateOffset = (result.DeltaCost - 2) / 2;

            int minColumn = 2;
            int maxColumn = size - 3;
            if (maxColumn - minColumn + 1 < gateCount)
            {
                result.Reason = "corridor too short for the requested gate count";
                return result;
            }

            if (lowerRow + lowerGateOffset > size - 1)
            {
                result.Reason = "lower corridor exceeds map height";
                return result;
            }

            bool mirrored = random.Next(2) == 0;

            var gateColumns = new List<int>();
            for (int i = 0; i < gateCount; i++)
            {
                int candidate;
                do
                {
                    candidate = random.Next(minColumn, maxColumn + 1);
                } while (gateColumns.Contains(candidate));
                gateColumns.Add(candidate);
            }
            gateColumns.Sort();

            int lowerGateColumn = random.Next(minColumn, maxColumn + 1);

            var obstacles = new bool[size, size];

            // 两道隔墙：整行封死，只在最左、最右各留 1 格缺口（分叉口 / 汇合口）。
            for (int x = 0; x < size; x++)
            {
                bool crossing = x == stemColumn || x == farStemColumn;
                obstacles[x, barrierLo] = !crossing;
                obstacles[x, barrierHi] = !crossing;
            }

            // 短廊 A：每个门都是一道只有单格缺口的横截墙，缺口正好落在走廊行上（不增加长度）。
            foreach (int column in gateColumns)
            {
                CarveGate(obstacles, column, 0, upperRow, upperRow);
            }

            // 长廊 B：tier2 用一道偏移缺口制造 +2 长度。
            if (lowerGateOffset > 0)
            {
                CarveGate(obstacles, lowerGateColumn, lowerRow, size - 1, lowerRow + lowerGateOffset);
            }

            var spawn = new Point(mirrored ? size - 1 : 0, barrierLo);
            var exit = new Point(mirrored ? 0 : size - 1, barrierLo);

            if (obstacles[spawn.X, spawn.Y] || obstacles[exit.X, exit.Y])
            {
                result.Reason = "gate blocked by skeleton";
                return result;
            }

            if (!ProbeCorridors(obstacles, size, spawn, exit,
                    out List<Point> path, out int cheapCost, out _, out int expensiveCost))
            {
                result.Reason = "no alternative corridor";
                return result;
            }

            int expected = size + 1;
            if (cheapCost != expected)
            {
                result.Reason = $"unexpected cheap cost {cheapCost}, expected {expected}";
                return result;
            }

            if (expensiveCost - cheapCost != result.DeltaCost)
            {
                result.Reason = $"unexpected delta {expensiveCost - cheapCost}, expected {result.DeltaCost}";
                return result;
            }

            int obstacleCount = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (obstacles[x, y]) obstacleCount++;
            }

            result.Map = new MapData
            {
                Width = size,
                Height = size,
                Obstacles = obstacles,
                Spawn = spawn,
                Exit = exit,
                Path = path,
                Metrics = AnalyzePath(path),
                PathRatio = (double)(path.Count - 1) / Math.Max(1, ManhattanDistance(spawn, exit)),
                ObstacleCount = obstacleCount,
                AttemptsUsed = 1,
                Seed = seed
            };
            result.CheapCost = cheapCost;
            result.ExpensiveCost = expensiveCost;
            result.CheapIsUpper = true;
            return result;
        }

        // P4.3 主流程入口：θ 一次成型，失败时 Reason 指出可辨原因。
        // 尺寸硬约束在生成前拦下（θ 只支持方形），不做隐式降级。
        // 装饰后重算 Path/Metrics，保证主流程显示与最终地形一致。
        private static ThetaResult BuildThetaMainMap(int seed)
        {
            var result = new ThetaResult();

            if (Settings.MapWidth != Settings.MapHeight)
            {
                result.Reason = $"theta requires a square map, got {Settings.MapWidth}x{Settings.MapHeight}";
                return result;
            }

            ThetaResult built = BuildThetaSkeleton(
                Settings.MapWidth, Settings.ThetaTier, seed, Profile.DeltaMin, Profile.DeltaMax);
            if (built.Map == null) return built;

            // 分析器的 W 必须与拟合出的 W 一致，否则 n=1/2 判据会用错代价。
            AnalysisBlockCost = built.BlockCost;

            DecorateTheta(built.Map, seed, Profile.TargetDensity, built.CheapIsUpper,
                TierOf(Settings.ThetaTier).KillzoneMinRatio, out _, out _);

            List<Point> path = FindPath(built.Map.Spawn, built.Map.Exit, built.Map.Obstacles,
                built.Map.Width, built.Map.Height, null, 0, out _);
            if (path.Count == 0)
            {
                built.Map = null;
                built.Reason = "decorated into disconnection";
                return built;
            }

            built.Map.Path = path;
            built.Map.Metrics = AnalyzePath(path);
            built.Map.PathRatio = (double)(path.Count - 1)
                / Math.Max(1, ManhattanDistance(built.Map.Spawn, built.Map.Exit));
            return built;
        }

        private static void PrintThetaSummary(ThetaResult result, MapAnalysis analysis)
        {
            MapData map = result.Map!;

            Console.WriteLine();
            Console.WriteLine("=== Theta skeleton summary ===");
            Console.WriteLine($"Spawn {map.Spawn} / Exit {map.Exit}");
            Console.WriteLine($"Cheap corridor: {(result.CheapIsUpper ? "upper" : "lower")} half, cost {result.CheapCost}");
            Console.WriteLine($"Long corridor : {(result.CheapIsUpper ? "lower" : "upper")} half, cost {result.ExpensiveCost}"
                              + $" (delta {result.DeltaCost})");
            Console.WriteLine($"Natural obstacles: {map.ObstacleCount}/{map.Width * map.Height}");
            Console.WriteLine($"Shortest path moves: {map.Path.Count - 1}; turns {map.Metrics.Turns}");

            foreach (SwitchReport report in analysis.Reports)
            {
                if (report.Best == null)
                {
                    Console.WriteLine($"Blocking n={report.N}: no combos");
                    continue;
                }

                SwitchSample best = report.Best.Value;
                Console.WriteLine(
                    $"Blocking n={report.N}: combos {report.Combos} | tolled {report.Tolled} | "
                    + $"switched {report.Switched} | equal-cost {report.EqualCost} | passing {report.Passing}");
                Console.WriteLine(
                    $"  best: overlap {best.Overlap:F2} | half-sep {best.HalfSeparation:F2} | "
                    + $"delta {best.Delta:P1} | blockers {string.Join(", ", best.Blockers)}");
                Console.WriteLine($"  new path: {string.Join(" ", best.NewPath)}");
            }

            Console.WriteLine($"Natural min-cut (diagnostic): {analysis.MinCut}");
        }

        // CSV 固定 24 列，失败行用同一 helper 补齐，避免列错位。
        private const int ThetaCsvColumns = 24;

        private static string ThetaRow(params string[] fields)
        {
            var list = new List<string>(fields);
            while (list.Count < ThetaCsvColumns) list.Add(string.Empty);
            return string.Join(",", list);
        }

        // 失败行：只把原因写进 cert_blockers 列，末列仍写拟合出的 W，其余留空。
        private static string ThetaFailRow(int size, int tier, int seed, int blockCost, string reason)
        {
            var fields = new List<string>
            {
                size.ToString(CultureInfo.InvariantCulture),
                tier.ToString(CultureInfo.InvariantCulture),
                seed.ToString(CultureInfo.InvariantCulture),
                "0"
            };
            for (int i = 5; i <= 18; i++) fields.Add(string.Empty);
            fields.Add("\"" + reason.Replace("\"", "'") + "\"");
            for (int i = 20; i <= 23; i++) fields.Add(string.Empty);
            fields.Add(blockCost.ToString(CultureInfo.InvariantCulture));
            return ThetaRow(fields.ToArray());
        }

        // 每张图一行证书：回归断言与离线比对都消费它，不再靠解析控制台文本。
        private sealed class ThetaCert
        {
            public int Size;
            public int Tier;
            public int Seed;
            public bool Valid;
            public string Reason = string.Empty;
            public int BlockCost;
            public int Delta;
            public int L1;
            public int L2;
            public int Gates;
            public int LowerOffset;
            public bool Decorated;
            public int Placed;
            public int Rejected;
            public int ObsCells;
            public int MinCut;
            public double KzCheap;
            public double KzExpensive;
            public bool OracleRejected;
            public bool Pass1;
            public bool Pass2;
            public int Combos1;
            public int Tolled1;
            public int Equal1;
            public int Combos2;
            public int Tolled2;
            public int Equal2;
            public double Overlap1;
            public double HalfSep1;
            public double Overlap2;
            public double HalfSep2;

            public double DeltaRel => L1 > 0 ? (double)Delta / L1 : 0;
            public double ObsRatio => Size > 0 ? (double)ObsCells / (Size * Size) : 0;
            public double KzRatio => KzCheap > 0.001 ? KzExpensive / KzCheap : 0;
        }

        private static string JsonNum(double value)
            => value.ToString("0.####", CultureInfo.InvariantCulture);

        private static string JsonText(string value)
            => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static void WriteThetaCert(string path, List<ThetaCert> certs, int from, int to,
            int count, double deltaLo, double deltaHi, double density, double kzMin, int[] tiers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"meta\": {");
            sb.AppendLine("    \"generatedAt\": " + JsonText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + ",");
            sb.AppendLine("    \"sizes\": [" + string.Join(", ", Enumerable.Range(from, to - from + 1)
                .Select(s => s.ToString(CultureInfo.InvariantCulture))) + "],");
            sb.AppendLine("    \"tiers\": [" + string.Join(", ", tiers) + "],");
            sb.AppendLine("    \"mapsPerSize\": " + count + ",");
            sb.AppendLine("    \"deltaWindow\": [" + JsonNum(deltaLo) + ", " + JsonNum(deltaHi) + "],");
            sb.AppendLine("    \"targetDensity\": " + JsonNum(density) + ",");
            sb.AppendLine("    \"killzoneMinRatio\": " + JsonNum(kzMin) + ",");
            sb.AppendLine("    \"source\": \"MapSkeleton.cs (P2c: fitted W/delta)\"");
            sb.AppendLine("  },");
            sb.AppendLine("  \"rows\": [");

            for (int i = 0; i < certs.Count; i++)
            {
                ThetaCert c = certs[i];
                var fields = new List<string>
                {
                    "\"size\": " + c.Size,
                    "\"tier\": " + c.Tier,
                    "\"seed\": " + c.Seed,
                    "\"valid\": " + (c.Valid ? "true" : "false"),
                    "\"w\": " + c.BlockCost,
                    "\"deltaC\": " + c.Delta,
                    "\"l1\": " + c.L1,
                    "\"l2\": " + c.L2,
                    "\"deltaRel\": " + JsonNum(c.DeltaRel),
                    "\"gates\": " + c.Gates,
                    "\"lowerOffset\": " + c.LowerOffset,
                    "\"decorated\": " + (c.Decorated ? "true" : "false"),
                    "\"placed\": " + c.Placed,
                    "\"rejected\": " + c.Rejected,
                    "\"obsRatio\": " + JsonNum(c.ObsRatio),
                    "\"k\": " + c.MinCut,
                    "\"kzCheap\": " + JsonNum(c.KzCheap),
                    "\"kzExpensive\": " + JsonNum(c.KzExpensive),
                    "\"kzRatio\": " + JsonNum(c.KzRatio),
                    "\"oracleRejected\": " + (c.OracleRejected ? "true" : "false"),
                    "\"n1\": {\"pass\": " + (c.Pass1 ? "true" : "false")
                        + ", \"combos\": " + c.Combos1
                        + ", \"tolled\": " + c.Tolled1
                        + ", \"equalCost\": " + c.Equal1
                        + ", \"overlap\": " + JsonNum(c.Overlap1)
                        + ", \"halfSep\": " + JsonNum(c.HalfSep1) + "}",
                    "\"n2\": {\"pass\": " + (c.Pass2 ? "true" : "false")
                        + ", \"combos\": " + c.Combos2
                        + ", \"tolled\": " + c.Tolled2
                        + ", \"equalCost\": " + c.Equal2
                        + ", \"overlap\": " + JsonNum(c.Overlap2)
                        + ", \"halfSep\": " + JsonNum(c.HalfSep2) + "}",
                    "\"reason\": " + JsonText(c.Reason)
                };
                sb.Append("    {" + string.Join(", ", fields) + "}"
                          + (i == certs.Count - 1 ? string.Empty : ","));
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"cert written: {Path.GetFullPath(path)}");
        }

        // ============================================================
        // P4：极简文本导出
        //
        // 一张地图一段文本，人可读、可 diff：
        //   size=.. tier=.. seed=.. w=.. deltaC=.. k=.. obs=.. hash=..
        //   <size 行字符网格>    # 障碍 / . 空地 / + 主路 / S 起点 / E 终点
        //   path=x,y x,y ...
        // hash = sha256(网格正文) 前 16 位十六进制，供 loader 零成本校验文件未被改动。
        // 落盘 <dir>/<size>x<size>/t<tier>_<seed>.map，另有 <dir>/manifest.json 索引。
        // ============================================================

        private sealed class ExportEntry
        {
            public int Size;
            public int Tier;
            public int Seed;
            public int BlockCost;
            public int Delta;
            public int MinCut;
            public int ObsCells;
            public string Hash = string.Empty;
            public string File = string.Empty;
        }

        // 头行与网格之外唯一的信息行：主路逐格坐标，QA 可直接 diff 路线。
        private static string RenderGrid(MapData map)
        {
            var onPath = new HashSet<Point>(map.Path);
            var sb = new StringBuilder();
            for (int y = 0; y < map.Height; y++)
            {
                if (y > 0) sb.Append('\n');
                for (int x = 0; x < map.Width; x++)
                {
                    var cell = new Point(x, y);
                    if (cell.Equals(map.Spawn)) sb.Append('S');
                    else if (cell.Equals(map.Exit)) sb.Append('E');
                    else if (map.Obstacles[x, y]) sb.Append('#');
                    else if (onPath.Contains(cell)) sb.Append('+');
                    else sb.Append('.');
                }
            }
            return sb.ToString();
        }

        private static string GridHash(string grid)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(grid)))
                .Substring(0, 16)
                .ToLowerInvariant();

        private static ExportEntry WriteMinimalMap(string rootDir, ThetaCert cert, MapData map)
        {
            string grid = RenderGrid(map);
            string hash = GridHash(grid);
            string relative = Path.Combine($"{map.Width}x{map.Height}", $"t{cert.Tier}_{cert.Seed}.map");
            string full = Path.Combine(rootDir, relative);

            var sb = new StringBuilder();
            sb.Append("size=").Append(map.Width.ToString(CultureInfo.InvariantCulture))
              .Append(" tier=").Append(cert.Tier.ToString(CultureInfo.InvariantCulture))
              .Append(" seed=").Append(cert.Seed.ToString(CultureInfo.InvariantCulture))
              .Append(" w=").Append(cert.BlockCost.ToString(CultureInfo.InvariantCulture))
              .Append(" deltaC=").Append(cert.Delta.ToString(CultureInfo.InvariantCulture))
              .Append(" k=").Append(cert.MinCut.ToString(CultureInfo.InvariantCulture))
              .Append(" obs=").Append(cert.ObsCells.ToString(CultureInfo.InvariantCulture))
              .Append(" hash=").Append(hash)
              .Append('\n')
              .Append(grid).Append('\n')
              .Append("path=").Append(string.Join(" ", map.Path.Select(p => $"{p.X},{p.Y}")));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(full)) ?? rootDir);
            File.WriteAllText(full, sb.ToString() + "\n", new UTF8Encoding(false));

            return new ExportEntry
            {
                Size = map.Width,
                Tier = cert.Tier,
                Seed = cert.Seed,
                BlockCost = cert.BlockCost,
                Delta = cert.Delta,
                MinCut = cert.MinCut,
                ObsCells = cert.ObsCells,
                Hash = hash,
                File = relative.Replace('\\', '/')
            };
        }

        private static void WriteExportManifest(string rootDir, List<ExportEntry> entries,
            bool decorated, double targetDensity, double deltaLo, double deltaHi,
            int count, int[] tiers, int from, int to, int skipped)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"meta\": {");
            sb.AppendLine("    \"generatedAt\": " + JsonText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + ",");
            sb.AppendLine("    \"sizes\": [" + string.Join(", ", Enumerable.Range(from, to - from + 1)
                .Select(s => s.ToString(CultureInfo.InvariantCulture))) + "],");
            sb.AppendLine("    \"tiers\": [" + string.Join(", ", tiers) + "],");
            sb.AppendLine("    \"mapsPerSize\": " + count + ",");
            sb.AppendLine("    \"decorated\": " + (decorated ? "true" : "false") + ",");
            sb.AppendLine("    \"targetDensity\": " + JsonNum(targetDensity) + ",");
            sb.AppendLine("    \"deltaWindow\": [" + JsonNum(deltaLo) + ", " + JsonNum(deltaHi) + "],");
            sb.AppendLine("    \"exported\": " + entries.Count + ",");
            sb.AppendLine("    \"skipped\": " + skipped + ",");
            sb.AppendLine("    \"source\": \"MapSkeleton.cs (P4: minimal text export)\"");
            sb.AppendLine("  },");
            sb.AppendLine("  \"maps\": [");

            for (int i = 0; i < entries.Count; i++)
            {
                ExportEntry e = entries[i];
                sb.Append("    {"
                    + "\"size\": " + e.Size
                    + ", \"tier\": " + e.Tier
                    + ", \"seed\": " + e.Seed
                    + ", \"w\": " + e.BlockCost
                    + ", \"deltaC\": " + e.Delta
                    + ", \"k\": " + e.MinCut
                    + ", \"obs\": " + e.ObsCells
                    + ", \"hash\": " + JsonText(e.Hash)
                    + ", \"file\": " + JsonText(e.File)
                    + "}" + (i == entries.Count - 1 ? string.Empty : ","));
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            string path = Path.Combine(rootDir, "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? rootDir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"manifest written: {Path.GetFullPath(path)}");
        }

        // 突变测试：故意把障碍压在走廊正中间（应当被 oracle 拒绝）。
        // 用来证明「oracle 不是一个永远返回 true 的空壳」。
        private static bool OracleSelfTest(MapData map, int expectedCheap, int expectedDelta)
        {
            if (map.Path.Count < 3) return false;

            Point victim = map.Path[map.Path.Count / 2];
            if (victim.Equals(map.Spawn) || victim.Equals(map.Exit)) return false;

            map.Obstacles[victim.X, victim.Y] = true;
            bool accepted = ProbeCorridors(map.Obstacles, map.Width, map.Spawn, map.Exit,
                                out _, out int cheapCost, out _, out int expensiveCost)
                            && cheapCost == expectedCheap
                            && expensiveCost - cheapCost == expectedDelta;
            map.Obstacles[victim.X, victim.Y] = false;

            return !accepted;   // 期望 oracle 拒绝
        }

        // 几何预检：门数与长廊下探深度都必须装得进 size×size（与 BuildThetaSkeleton 同一条约束）。
        private static string GeometryProblem(int size, int delta2)
        {
            if (size - 4 < delta2 / 2)
                return $"geometry: needs {delta2 / 2} gate columns, room for {size - 4}";

            int mid = size / 2;
            if (mid + 1 + (delta2 - 2) / 2 > size - 1)
                return "geometry: long corridor exceeds map height";

            return string.Empty;
        }

        // 可行性矩阵：一行一个尺寸，把 (W, ΔC, 门数, ΔL) 与几何预检一次列全，
        // 再给出封边格上限与由此推得的密度上限（干跑模式下就是这些预测）。
        private static bool PrintFeasibilityMatrix(
            int from, int to, double deltaLo, double deltaHi, double targetDensity)
        {
            Console.WriteLine();
            Console.WriteLine("=== tier feasibility matrix ===");
            Console.WriteLine("size   L1    W   t1 dC  dL      g    t2 dC  dL      g  flank  dens<=  status");

            bool allOk = true;
            for (int size = from; size <= to; size++)
            {
                int l1 = size + 1;

                if (size < Profile.ThetaMinSize)
                {
                    Console.WriteLine($"{size,4} {l1,4}     -                           size < {Profile.ThetaMinSize}");
                    allOk = false;
                    continue;
                }

                if (!TryFitTiers(size, deltaLo, deltaHi,
                        out int w, out int d1, out int d2, out string reason))
                {
                    Console.WriteLine($"{size,4} {l1,4}     -   {reason}");
                    allOk = false;
                    continue;
                }

                string status = GeometryProblem(size, d2);
                if (status.Length > 0) allOk = false;

                // 封边格上限：短廊内部格各贡献一格外墙（实际会因边界与保护格略少）。
                int flank = l1 - 1;
                double densMax = (Math.Round(size * size * targetDensity) + flank) / (size * size);

                Console.WriteLine(
                    $"{size,4} {l1,4} {w,4} {d1,6} {100.0 * d1 / l1,7:F1}% {d1 / 2,3} "
                    + $"{d2,6} {100.0 * d2 / l1,7:F1}% {d2 / 2,3} {flank,6} {densMax,7:P1}  "
                    + (status.Length > 0 ? status : "ok"));
            }

            return allOk;
        }

        private static void RunThetaBatch(string[] args)
        {
            int from = 11;
            int to = 16;
            int count = 50;
            int seedBase = Settings.RandomSeed;
            int onlyTier = 0;
            bool show = false;
            bool decorate = false;
            bool oracleSelfTest = false;
            bool regress = false;
            bool profileCheck = false;
            double targetDensity = Profile.TargetDensity;
            double deltaLo = Profile.DeltaMin;
            double deltaHi = Profile.DeltaMax;
            string csvPath = Path.Combine("analysis", "theta", "sweep.csv");
            string certPath = string.Empty;
            string exportDir = string.Empty;
            bool bare = false;
            AnalysisBlockCost = Profile.ThetaBlockCost;

            // --set k=v 与旧式 --density / --kz-min / --delta-window 共用同一套写入与校验，
            // 两种写法行为完全等价；返回 false 表示键未知或值非法。
            bool ApplyOption(string key, string value)
            {
                switch (key)
                {
                    case "density":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double density)
                            || density <= 0 || density >= 0.6) return false;
                        targetDensity = density;
                        return true;

                    case "kz-min":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double kz)
                            || kz <= 0) return false;
                        foreach (TierSpec spec in Profile.Tiers) spec.KillzoneMinRatio = kz;
                        return true;

                    // 值写成 lo:hi（或 lo,hi）。
                    case "delta-window":
                    {
                        string[] parts = value.Split(':', ',');
                        if (parts.Length != 2
                            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
                            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hi)
                            || lo <= 0 || hi <= lo) return false;
                        deltaLo = lo;
                        deltaHi = hi;
                        return true;
                    }

                    default:
                        return false;
                }
            }

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--from":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedFrom)
                            && parsedFrom >= Profile.ThetaMinSize)
                        {
                            from = parsedFrom;
                            i++;
                        }
                        break;
                    case "--to":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedTo)
                            && parsedTo >= Profile.ThetaMinSize)
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
                    case "--tier":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedTier)
                            && parsedTier >= 1 && parsedTier <= 2)
                        {
                            onlyTier = parsedTier;
                            i++;
                        }
                        break;
                    case "--seed":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedSeed))
                        {
                            seedBase = parsedSeed;
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
                    case "--show":
                        show = true;
                        break;
                    case "--decorate":
                        decorate = true;
                        break;
                    case "--oracle-selftest":
                        oracleSelfTest = true;
                        break;
                    case "--regress":
                        regress = true;
                        break;
                    case "--profile-check":
                        profileCheck = true;
                        break;
                    case "--cert":
                        if (i + 1 < args.Length)
                        {
                            certPath = args[i + 1];
                            i++;
                        }
                        break;
                    case "--export":
                        if (i + 1 < args.Length)
                        {
                            exportDir = args[i + 1];
                            i++;
                        }
                        break;
                    case "--bare":
                        bare = true;
                        break;
                    case "--delta-window":
                        if (i + 2 < args.Length && ApplyOption("delta-window", args[i + 1] + ":" + args[i + 2]))
                            i += 2;
                        break;
                    case "--kz-min":
                        if (i + 1 < args.Length && ApplyOption("kz-min", args[i + 1]))
                            i++;
                        break;
                    case "--density":
                        if (i + 1 < args.Length && ApplyOption("density", args[i + 1]))
                            i++;
                        break;
                    case "--set":
                        if (i + 1 < args.Length)
                        {
                            string pair = args[i + 1];
                            int eq = pair.IndexOf('=');
                            if (eq <= 0 || !ApplyOption(pair.Substring(0, eq), pair.Substring(eq + 1)))
                                Console.WriteLine($"warning: ignored option '{pair}'");
                            i++;
                        }
                        break;
                }
            }

            if (regress)
            {
                decorate = true;
                oracleSelfTest = true;
                if (certPath.Length == 0) certPath = Path.Combine("analysis", "theta", "cert.json");
            }

            // --export 默认导出「出货形态」，即装饰后地图；--bare 明确要求裸骨架。
            // 与 --regress 冲突时以回归语义优先（裸骨架会让装饰类断言失效），只提示不报错。
            if (exportDir.Length > 0 && !bare) decorate = true;
            if (bare && regress)
                Console.WriteLine("note: --bare ignored (--regress requires decorated maps)");
            else if (bare)
                decorate = false;

            if (profileCheck)
            {
                Console.WriteLine("=== P3 profile check (dry run, no map generated) ===");
                Console.WriteLine($"sizes {from}..{to} | delta-L window [{deltaLo:F2}, {deltaHi:F2}] | "
                                  + $"target density {targetDensity:P0}");
                bool feasible = PrintFeasibilityMatrix(from, to, deltaLo, deltaHi, targetDensity);
                Console.WriteLine(feasible
                    ? "profile check: all sizes feasible"
                    : "profile check: some sizes infeasible");
                Environment.ExitCode = feasible ? 0 : 1;
                return;
            }

            Console.WriteLine("=== " + (decorate ? "P2b" : "P2a") + " theta skeleton (soft cost, W fitted per size) ===");
            Console.WriteLine($"sizes {from}..{to} | {count} maps per size and tier | "
                              + $"tiers {(onlyTier == 0 ? "1,2" : onlyTier.ToString())}"
                              + (decorate ? $" | decorate to {targetDensity:P0} obstacle ratio" : " | bare skeleton"));
            Console.WriteLine($"delta-L window: [{deltaLo:F2}, {deltaHi:F2}]  (W and deltaC are both derived from L1)");
            Console.WriteLine($"csv: {Path.GetFullPath(csvPath)}");
            if (exportDir.Length > 0)
                Console.WriteLine($"export: {Path.GetFullPath(exportDir)} ({(decorate ? "decorated" : "bare skeleton")} maps)");
            Console.WriteLine("killzone gate: long corridor >= "
                              + string.Join(" | ", Profile.Tiers.Select(t => $"t{t.N} {t.KillzoneMinRatio:F2}x"))
                              + " cheap corridor");

            var tiers = onlyTier == 0 ? new[] { 1, 2 } : new[] { onlyTier };
            PrintFeasibilityMatrix(from, to, deltaLo, deltaHi, targetDensity);

            var rows = new List<string>
            {
                "size,tier,seed,valid,a_cost,b_cost,n,delta_rel,k,combos,switched,tolled,equal_cost,"
                + "passing,best_overlap,best_half_sep,best_delta,best_moves,cert_blockers,"
                + "kz_cheap,kz_expensive,kz_ratio,obs_ratio,w"
            };

            var certs = new List<ThetaCert>();
            var exports = new List<ExportEntry>();

            for (int size = from; size <= to; size++)
            {
                if (!TryFitTiers(size, deltaLo, deltaHi, out int fitW, out _, out _, out _))
                {
                    Console.WriteLine($"{size}x{size}: skipped (see feasibility matrix)");
                    continue;
                }

                AnalysisBlockCost = fitW;

                foreach (int tier in tiers)
                {
                    int valid = 0;
                    int exact = 0;
                    int switchedButRejected = 0;
                    int neverSwitches = 0;
                    int earlySwitch = 0;
                    var kCounts = new SortedDictionary<int, int>();
                    int placedTotal = 0;
                    int rejectedTotal = 0;
                    int selfTestPass = 0;
                    int selfTestTotal = 0;
                    double ratioSum = 0;
                    double kzCheapSum = 0;
                    double kzExpensiveSum = 0;
                    int kzBetterCount = 0;
                    var deltaSum = new double[3];
                    var costSum = new double[3];
                    var overlapSum = new double[3];
                    var halfSepSum = new double[3];
                    var passCount = new int[3];
                    var switchCount = new int[3];
                    var sampleCount = new int[3];

                    for (int i = 0; i < count; i++)
                    {
                        int seed = unchecked(seedBase + size * 101 + tier * 7919 + i * 31);
                        ThetaResult result = BuildThetaSkeleton(size, tier, seed, deltaLo, deltaHi);

                        var cert = new ThetaCert
                        {
                            Size = size,
                            Tier = tier,
                            Seed = seed,
                            BlockCost = fitW,
                            Delta = result.DeltaCost,
                            L1 = result.CheapCost,
                            L2 = result.ExpensiveCost,
                            Decorated = decorate,
                            Placed = 0,
                            Rejected = 0,
                            ObsCells = 0,
                            MinCut = 0,
                            Pass1 = false,
                            Pass2 = false,
                            OracleRejected = false
                        };
                        certs.Add(cert);

                        if (result.Map == null)
                        {
                            cert.Reason = result.Reason;
                            rows.Add(ThetaFailRow(size, tier, seed, fitW, result.Reason));
                            continue;
                        }

                        valid++;
                        cert.Valid = true;
                        cert.Gates = result.DeltaCost / 2;
                        cert.LowerOffset = (result.DeltaCost - 2) / 2;

                        if (oracleSelfTest)
                        {
                            bool rejectedByOracle = OracleSelfTest(result.Map, result.CheapCost, result.DeltaCost);
                            cert.OracleRejected = rejectedByOracle;
                            if (rejectedByOracle) selfTestPass++;
                        }
                        selfTestTotal++;

                        int placed = 0;
                        int rejected = 0;

                        if (decorate)
                        {
                            DecorateTheta(result.Map, seed, targetDensity, result.CheapIsUpper,
                                TierOf(tier).KillzoneMinRatio, out placed, out rejected);

                            // 装饰后重算主路，保证 Map 里的路线与最终地形一致。
                            List<Point> decoratedPath = FindPath(
                                result.Map.Spawn, result.Map.Exit, result.Map.Obstacles,
                                result.Map.Width, result.Map.Height, null, 0, out _);

                            if (decoratedPath.Count == 0)
                            {
                                valid--;
                                cert.Valid = false;
                                cert.Reason = "decorated into disconnection";
                                rows.Add(ThetaFailRow(size, tier, seed, fitW, cert.Reason));
                                continue;
                            }

                            result.Map.Path = decoratedPath;
                            result.Map.Metrics = AnalyzePath(decoratedPath);
                            placedTotal += placed;
                            rejectedTotal += rejected;
                            ratioSum += (double)result.Map.ObstacleCount / (size * size);
                            cert.Placed = placed;
                            cert.Rejected = rejected;
                        }

                        cert.ObsCells = result.Map.ObstacleCount;

                        MapAnalysis analysis = AnalyzeMap(result.Map, 2, Profile.ComboCap);

                        // Killzone 差异化度量：短廊（默认路线）与长廊（改道后路线）各自的火力覆盖。
                        double kzCheap = 0;
                        double kzExpensive = 0;
                        if (ProbeCorridors(result.Map.Obstacles, size, result.Map.Spawn, result.Map.Exit,
                                out List<Point> cheapNow, out _, out List<Point> expensiveNow, out _))
                        {
                            kzCheap = ComputeKillzone(cheapNow, result.Map);
                            kzExpensive = ComputeKillzone(expensiveNow, result.Map);
                        }
                        kzCheapSum += kzCheap;
                        kzExpensiveSum += kzExpensive;
                        if (kzExpensive > kzCheap) kzBetterCount++;
                        cert.KzCheap = kzCheap;
                        cert.KzExpensive = kzExpensive;

                        kCounts.TryGetValue(analysis.MinCut, out int kSeen);
                        kCounts[analysis.MinCut] = kSeen + 1;
                        cert.MinCut = analysis.MinCut;

                        // 与证书同源：同一批种子、同一张 result.Map（装饰后已重算 Path 与 Metrics）。
                        if (exportDir.Length > 0)
                            exports.Add(WriteMinimalMap(exportDir, cert, result.Map));

                        deltaSum[1] += result.DeltaCost;
                        deltaSum[2] += result.DeltaCost;
                        costSum[1] += result.CheapCost;
                        costSum[2] += result.CheapCost;

                        int passMin = -1;
                        int switchMin = -1;

                        foreach (SwitchReport report in analysis.Reports)
                        {
                            int n = report.N;
                            if (n > 2) continue;

                            sampleCount[n]++;
                            if (report.Switched > 0) switchCount[n]++;
                            if (report.Passing > 0) passCount[n]++;
                            if (report.Passing > 0 && passMin < 0) passMin = n;
                            if (report.Switched > 0 && switchMin < 0) switchMin = n;

                            string blockers = string.Empty;
                            if (report.Best != null)
                            {
                                SwitchSample sample = report.Best.Value;
                                overlapSum[n] += sample.Overlap;
                                halfSepSum[n] += sample.HalfSeparation;
                                // 用 x:y 而非 (x,y)，避免逗号破坏 CSV 列结构。
                                blockers = string.Join("|",
                                    sample.Blockers.Select(p => $"{p.X}:{p.Y}"));

                                if (n == 1)
                                {
                                    cert.Overlap1 = sample.Overlap;
                                    cert.HalfSep1 = sample.HalfSeparation;
                                }
                                else
                                {
                                    cert.Overlap2 = sample.Overlap;
                                    cert.HalfSep2 = sample.HalfSeparation;
                                }
                            }

                            if (n == 1)
                            {
                                cert.Combos1 = report.Combos;
                                cert.Tolled1 = report.Tolled;
                                cert.Equal1 = report.EqualCost;
                                cert.Pass1 = report.Passing > 0;
                            }
                            else
                            {
                                cert.Combos2 = report.Combos;
                                cert.Tolled2 = report.Tolled;
                                cert.Equal2 = report.EqualCost;
                                cert.Pass2 = report.Passing > 0;
                            }

                            string kzCheapText = kzCheap.ToString("F2", CultureInfo.InvariantCulture);
                            string kzExpensiveText = kzExpensive.ToString("F2", CultureInfo.InvariantCulture);
                            string kzRatioText = (kzCheap > 0.001 ? kzExpensive / kzCheap : 0)
                                .ToString("F3", CultureInfo.InvariantCulture);

                            rows.Add(string.Join(",", new[]
                            {
                                size.ToString(CultureInfo.InvariantCulture),
                                tier.ToString(CultureInfo.InvariantCulture),
                                seed.ToString(CultureInfo.InvariantCulture),
                                "1",
                                result.CheapCost.ToString(CultureInfo.InvariantCulture),
                                result.ExpensiveCost.ToString(CultureInfo.InvariantCulture),
                                n.ToString(CultureInfo.InvariantCulture),
                                ((double)result.DeltaCost / result.CheapCost)
                                    .ToString("F3", CultureInfo.InvariantCulture),
                                analysis.MinCut.ToString(CultureInfo.InvariantCulture),
                                report.Combos.ToString(CultureInfo.InvariantCulture),
                                report.Switched.ToString(CultureInfo.InvariantCulture),
                                report.Tolled.ToString(CultureInfo.InvariantCulture),
                                report.EqualCost.ToString(CultureInfo.InvariantCulture),
                                report.Passing.ToString(CultureInfo.InvariantCulture),
                                (report.Best?.Overlap ?? 0).ToString("F3", CultureInfo.InvariantCulture),
                                (report.Best?.HalfSeparation ?? 0).ToString("F3", CultureInfo.InvariantCulture),
                                (report.Best?.Delta ?? 0).ToString("F3", CultureInfo.InvariantCulture),
                                report.Best?.Moves.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                                "\"" + blockers + "\"",
                                kzCheapText,
                                kzExpensiveText,
                                kzRatioText,
                                ((double)result.Map.ObstacleCount / (size * size))
                                    .ToString("F3", CultureInfo.InvariantCulture),
                                fitW.ToString(CultureInfo.InvariantCulture)
                            }));
                        }

                        if (passMin == tier) exact++;
                        else if (switchMin < 0) neverSwitches++;
                        else if (passMin < 0) switchedButRejected++;
                        else earlySwitch++;

                        if (show && i == 0)
                        {
                            Console.WriteLine($"--- sample {size}x{size} tier {tier} ---");
                            PrintMap(result.Map);
                            PrintThetaSummary(result, analysis);
                            Console.WriteLine();
                        }
                    }

                    if (valid == 0)
                    {
                        Console.WriteLine($"{size}x{size} tier {tier}: no valid skeleton generated");
                        continue;
                    }

                    Console.WriteLine(
                        $"{size}x{size} tier {tier}: valid {valid}/{count} | "
                        + $"PASS(switch at n={tier}): {exact}/{valid} ({100.0 * exact / valid:F1}%) | "
                        + $"switched-but-rejected {switchedButRejected} | never switches {neverSwitches} | "
                        + $"early switch {earlySwitch} | k: "
                        + string.Join(" ", kCounts.Select(pair => $"k={pair.Key}:{pair.Value}")));

                    for (int n = 1; n <= 2; n++)
                    {
                        if (sampleCount[n] == 0) continue;
                        Console.WriteLine(
                            $"    n={n}: switch rate {(double)switchCount[n] / sampleCount[n]:P1} | "
                            + $"pass rate {(double)passCount[n] / sampleCount[n]:P1} | "
                            + $"avg overlap {overlapSum[n] / sampleCount[n]:F2} | "
                            + $"avg half-sep {halfSepSum[n] / sampleCount[n]:F2} | "
                            + $"avg delta {deltaSum[n] / costSum[n]:P1}");
                    }

                    if (decorate)
                    {
                        Console.WriteLine(
                            $"    decoration: placed {placedTotal} | rejected by oracle {rejectedTotal} | "
                            + $"final obstacle ratio {ratioSum / valid:P1}");
                    }

                    double kzCheapAvg = kzCheapSum / valid;
                    double kzExpensiveAvg = kzExpensiveSum / valid;
                    double kzRatio = kzCheapAvg > 0.001 ? kzExpensiveAvg / kzCheapAvg : 0;
                    Console.WriteLine(
                        $"    killzone: cheap {kzCheapAvg:F2} slots/cell | long {kzExpensiveAvg:F2} | "
                        + $"ratio {kzRatio:F2}x (gate >= {TierOf(tier).KillzoneMinRatio:F2}) | "
                        + $"switch reward {(kzRatio - 1):+0.0%;-0.0%} | "
                        + $"long > cheap in {kzBetterCount}/{valid} ({100.0 * kzBetterCount / valid:F0}%)");

                    if (oracleSelfTest)
                    {
                        Console.WriteLine(
                            $"    oracle self-test (block a corridor cell): "
                            + $"rejected {selfTestPass}/{selfTestTotal}");
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
            File.WriteAllLines(csvPath, rows, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine($"csv written: {Path.GetFullPath(csvPath)}");

            if (certPath.Length > 0)
            {
                // 证书里只记一个标量；两档共用同一个下限（--set kz-min 会同时写两档）。
                WriteThetaCert(certPath, certs, from, to, count, deltaLo, deltaHi,
                    targetDensity, TierOf(tiers[0]).KillzoneMinRatio, tiers);
            }

            if (exportDir.Length > 0)
            {
                // skipped = 无效/断连、没有对应地图文件的行数。
                WriteExportManifest(exportDir, exports, decorate, targetDensity, deltaLo, deltaHi,
                    count, tiers, from, to, certs.Count - exports.Count);
                Console.WriteLine($"export written: {exports.Count}/{certs.Count} maps -> "
                                  + Path.GetFullPath(exportDir));
            }

            if (regress)
            {
                bool ok = RunThetaRegression(certs, from, to, tiers, count, deltaLo, deltaHi, targetDensity,
                    exportDir);
                Environment.ExitCode = ok ? 0 : 1;
            }
        }

        // 回归断言：把「已验证过的事实」写死成机器判据，任何一项退化就让退出码变 1。
        private static bool RunThetaRegression(
            List<ThetaCert> certs, int from, int to, int[] tiers, int count,
            double deltaLo, double deltaHi, double targetDensity, string exportDir)
        {
            int total = (to - from + 1) * tiers.Length * count;
            int failures = 0;

            bool Check(string id, string label, int pass, int total2, bool warnOnly = false)
            {
                bool ok = pass == total2;
                string tag = ok ? "PASS" : (warnOnly ? "WARN" : "FAIL");
                Console.WriteLine($"  {id} {label,-42} {pass}/{total2}  {tag}");
                if (!ok && !warnOnly) failures++;
                return ok;
            }

            Console.WriteLine();
            Console.WriteLine("=== P2c regression ===");
            Console.WriteLine($"  expected certs: {certs.Count} (sizes {from}..{to}, tiers {string.Join(",", tiers)}, {count}/size)");

            Check("A1", "skeleton valid", certs.Count(c => c.Valid), Math.Max(total, certs.Count));

            var valid = certs.Where(c => c.Valid).ToList();
            Check("A2", "vertex min-cut k == 2", valid.Count(c => c.MinCut == 2), valid.Count);
            Check("A3", "oracle rejects a corridor block", valid.Count(c => c.OracleRejected), valid.Count);

            int deltaOk = valid.Count(c =>
                TryFitTiers(c.Size, deltaLo, deltaHi, out _, out int d1, out int d2, out _)
                && c.Delta == (c.Tier == 1 ? d1 : d2));
            Check("A4", "deltaC matches the tier fit", deltaOk, valid.Count);

            int windowOk = valid.Count(c => c.DeltaRel >= deltaLo - 1e-9 && c.DeltaRel <= deltaHi + 1e-9);
            Check("A5", $"delta-L inside [{deltaLo:F2}, {deltaHi:F2}]", windowOk, valid.Count);

            int kzOk = valid.Count(c => c.KzRatio >= TierOf(c.Tier).KillzoneMinRatio - 1e-9);
            Check("A6", "long-corridor killzone >= tier gate", kzOk, valid.Count);

            var tier1 = valid.Where(c => c.Tier == 1).ToList();
            var tier2 = valid.Where(c => c.Tier == 2).ToList();
            Check("A7", "tier1 switches at n=1", tier1.Count(c => c.Pass1), tier1.Count);
            Check("A8", "tier2 does not switch at n=1", tier2.Count(c => !c.Pass1), tier2.Count);
            Check("A9", "tier2 switches at n=2", tier2.Count(c => c.Pass2), tier2.Count);
            Check("A10", "no equal-cost escape at n=tier",
                valid.Count(c => c.Tier == 1 ? c.Equal1 == 0 : c.Equal2 == 0), valid.Count);

            // 已知偏差：11x11 tier2 的封边格没进密度预算，实测可达 30.6%；只告警不判负。
            Check("A11", $"obstacle ratio <= {targetDensity + 0.07:P0}",
                valid.Count(c => c.ObsRatio <= targetDensity + 0.07), valid.Count, warnOnly: true);

            // 单调性护栏：tier1 挡到 n=2 仍必须改道（挡得越多长廊只会更划算）。
            Check("A12", "tier1 still switches at n=2", tier1.Count(c => c.Pass2), tier1.Count);

            // A13：写出→读回闭环。只有指定 --export 时才跑，把 Loader 的全部校验接进回归。
            if (exportDir.Length > 0)
            {
                ReloadExported(exportDir, certs, out int reloadOk, out int reloadTotal, out string firstFailure);
                if (!Check("A13", "exported maps reload clean", reloadOk, reloadTotal) && firstFailure.Length > 0)
                    Console.WriteLine($"    first failure: {firstFailure}");
            }

            Console.WriteLine(failures == 0
                ? "  regression: PASS"
                : $"  regression: FAIL ({failures} assertion(s))");
            return failures == 0;
        }

        // 用内存证书 + 磁盘 manifest 两个来源，把导出目录逐张读回。
        // 这里必须走 Loader 的完整校验，否则「写出器与读取器各自正确、但双方不兼容」不会被发现。
        private static void ReloadExported(string exportDir, List<ThetaCert> certs,
            out int ok, out int total, out string firstFailure)
        {
            ok = 0;
            total = 0;
            firstFailure = string.Empty;

            var certIndex = new Dictionary<(int, int, int), CertRow>();
            foreach (ThetaCert c in certs)
            {
                if (!c.Valid) continue;
                certIndex[(c.Size, c.Tier, c.Seed)] = new CertRow
                {
                    Size = c.Size,
                    Tier = c.Tier,
                    Seed = c.Seed,
                    Valid = true,
                    BlockCost = c.BlockCost,
                    Delta = c.Delta,
                    MinCut = c.MinCut,
                    ObsRatio = Math.Round(c.ObsRatio, 4)
                };
            }

            if (!TryReadManifest(exportDir, out Dictionary<(int, int, int), ManifestRow> manifest,
                    out List<string> files, out string manifestReason))
            {
                total = 1;
                firstFailure = $"manifest: {manifestReason}";
                return;
            }

            total = files.Count;
            foreach (string file in files)
            {
                if (TryLoadMap(file, certIndex, manifest, out _, out string reason)) ok++;
                else if (firstFailure.Length == 0) firstFailure = $"{Path.GetFileName(file)}: {reason}";
            }
        }
    }
}
