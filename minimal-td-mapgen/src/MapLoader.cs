using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MinimalTowerDefenseMapV3
{
    internal static partial class Program
    {
        // ============================================================
        // P5：地图回读校验（P4 极简文本格式的读取侧）
        //
        // 格式（写出器见 MapSkeleton.cs）：
        //   size=.. tier=.. seed=.. w=.. deltaC=.. k=.. obs=.. hash=..
        //   <size 行字符网格>    # 障碍 / . 空地 / + 主路 / S 起点 / E 终点
        //   path=x,y x,y ...
        //
        // 校验分三层：
        //   自洽层：结构、字符集、obs 计数、hash、path 连通性与端点；
        //   复算层：用 P0 内核重算路径逐格比对（硬断言，暴露 tie-break 不一致）、
        //           AnalyzeMap 重算 k（硬断言）、ProbeCorridors 重算 ΔC（硬断言）；
        //   交叉层：可选 --cert 证书与目录 manifest 索引，按 (size, tier, seed) 逐行比对
        //           w/ΔC/k/obs 与有效标记。
        // 头行永远不覆盖文件内容：复算只用于「比对」，不用于「修正」。
        // ============================================================

        private sealed class CertRow
        {
            public int Size;
            public int Tier;
            public int Seed;
            public bool Valid;
            public int BlockCost;
            public int Delta;
            public int MinCut;
            public double ObsRatio;
            public string Reason = string.Empty;
        }

        private static bool TryReadCert(string filePath, out Dictionary<(int, int, int), CertRow> index,
            out string reason)
        {
            index = new Dictionary<(int, int, int), CertRow>();
            reason = string.Empty;

            if (!File.Exists(filePath))
            {
                reason = "cert file not found";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("rows", out JsonElement rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                reason = "cert json has no 'rows' array";
                return false;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                var cert = new CertRow();
                bool ok = TryInt(row, "size", out cert.Size)
                          && TryInt(row, "tier", out cert.Tier)
                          && TryInt(row, "seed", out cert.Seed)
                          && TryInt(row, "w", out cert.BlockCost)
                          && TryInt(row, "deltaC", out cert.Delta)
                          && TryInt(row, "k", out cert.MinCut)
                          && TryDouble(row, "obsRatio", out cert.ObsRatio);

                if (!ok)
                {
                    reason = "cert row missing a numeric field (size/tier/seed/w/deltaC/k/obsRatio)";
                    return false;
                }

                cert.Valid = row.TryGetProperty("valid", out JsonElement valid)
                             && valid.ValueKind == JsonValueKind.True;
                if (row.TryGetProperty("reason", out JsonElement why) && why.ValueKind == JsonValueKind.String)
                    cert.Reason = why.GetString() ?? string.Empty;

                index[(cert.Size, cert.Tier, cert.Seed)] = cert;
            }

            if (index.Count == 0)
            {
                reason = "cert json has no rows";
                return false;
            }

            return true;
        }

        private static bool TryInt(JsonElement row, string key, out int value)
        {
            value = 0;
            return row.TryGetProperty(key, out JsonElement element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt32(out value);
        }

        private static bool TryDouble(JsonElement row, string key, out double value)
        {
            value = 0;
            return row.TryGetProperty(key, out JsonElement element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetDouble(out value);
        }

        // manifest.json 是导出的同批索引：与证书形成两个独立来源，
        // 证书来自生成阶段，manifest 来自落盘阶段，两者都应与文件头行一致。
        private sealed class ManifestRow
        {
            public int Size;
            public int Tier;
            public int Seed;
            public int BlockCost;
            public int Delta;
            public int MinCut;
            public int ObsCells;
            public string Hash = string.Empty;
        }

        private static bool TryReadManifest(string dir, out Dictionary<(int, int, int), ManifestRow> index,
            out List<string> files, out string reason)
        {
            index = new Dictionary<(int, int, int), ManifestRow>();
            files = new List<string>();
            reason = string.Empty;

            string manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                reason = "manifest.json not found";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!document.RootElement.TryGetProperty("maps", out JsonElement maps)
                || maps.ValueKind != JsonValueKind.Array)
            {
                reason = "manifest json has no 'maps' array";
                return false;
            }

            foreach (JsonElement row in maps.EnumerateArray())
            {
                var entry = new ManifestRow();
                bool ok = TryInt(row, "size", out entry.Size)
                          && TryInt(row, "tier", out entry.Tier)
                          && TryInt(row, "seed", out entry.Seed)
                          && TryInt(row, "w", out entry.BlockCost)
                          && TryInt(row, "deltaC", out entry.Delta)
                          && TryInt(row, "k", out entry.MinCut)
                          && TryInt(row, "obs", out entry.ObsCells);

                if (!ok
                    || !row.TryGetProperty("hash", out JsonElement hash) || hash.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("file", out JsonElement file) || file.ValueKind != JsonValueKind.String)
                {
                    reason = "manifest row missing a field (size/tier/seed/w/deltaC/k/obs/hash/file)";
                    return false;
                }

                entry.Hash = hash.GetString() ?? string.Empty;
                string relative = (file.GetString() ?? string.Empty)
                    .Replace('/', Path.DirectorySeparatorChar);
                if (relative.Length == 0)
                {
                    reason = "manifest row has an empty file path";
                    return false;
                }

                index[(entry.Size, entry.Tier, entry.Seed)] = entry;
                files.Add(Path.Combine(dir, relative));
            }

            if (files.Count == 0)
            {
                reason = "manifest json has no maps";
                return false;
            }

            return true;
        }

        private sealed class LoadedMap
        {
            public MapData Map = new MapData();
            public int DeclaredSize;
            public int DeclaredSeed;
            public int Tier;
            public int BlockCost;
            public int DeltaC;
            public int DeclaredMinCut;
        }

        // 行序固定为 头行 + size 行网格 + 1 行 path；未知头行 key 一律忽略（前向兼容）。
        // cert / manifest 为 null 时跳过对应交叉层。
        private static bool TryLoadMap(string filePath, Dictionary<(int, int, int), CertRow>? cert,
            Dictionary<(int, int, int), ManifestRow>? manifest,
            out LoadedMap loaded, out string reason)
        {
            loaded = new LoadedMap();
            reason = string.Empty;

            if (!File.Exists(filePath))
            {
                reason = "file not found";
                return false;
            }

            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length < 2)
            {
                reason = "file is empty";
                return false;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string token in lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0)
                {
                    reason = $"header token without '=': '{token}'";
                    return false;
                }
                fields[token.Substring(0, eq)] = token.Substring(eq + 1);
            }

            bool NeedInt(string key, out int value)
            {
                value = 0;
                return fields.TryGetValue(key, out string? text)
                       && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }

            string[] intKeys = { "size", "tier", "seed", "w", "deltaC", "k", "obs" };
            foreach (string key in intKeys)
            {
                if (!NeedInt(key, out _))
                {
                    reason = $"header missing or invalid '{key}'";
                    return false;
                }
            }

            if (!fields.TryGetValue("hash", out string? hash) || hash.Length == 0)
            {
                reason = "header missing or invalid 'hash'";
                return false;
            }

            NeedInt("size", out int size);
            NeedInt("tier", out int tier);
            NeedInt("seed", out int seed);
            NeedInt("w", out int blockCost);
            NeedInt("deltaC", out int deltaC);
            NeedInt("k", out int declaredMinCut);
            NeedInt("obs", out int declaredObs);

            // 头行一旦可解析就先记下标识，供证书反向覆盖判定使用
            //（即使后续校验失败，这张地图在“文件存在”意义上仍然是存在的）。
            loaded.DeclaredSize = size;
            loaded.DeclaredSeed = seed;
            loaded.Tier = tier;
            loaded.BlockCost = blockCost;
            loaded.DeltaC = deltaC;
            loaded.DeclaredMinCut = declaredMinCut;

            if (size < 2)
            {
                reason = $"size < 2 ({size})";
                return false;
            }

            // 文件名约定 <n>x<n>/t<tier>_<seed>.map：符合约定时用文件名反证头行。
            // seed / tier / w 在文件内不可自证（θ 拓扑下 k 与 ΔC 都对 W 不敏感），
            // 文件名是可离线验证的第二来源，故只要符合约定就强校验；不合约定则跳过。
            if (TryParseNameConvention(filePath, out int nameSize, out int nameTier, out int nameSeed))
            {
                if (nameSize != size)
                {
                    reason = $"filename implies size {nameSize}, header says {size}";
                    return false;
                }

                if (nameTier != tier)
                {
                    reason = $"filename implies tier {nameTier}, header says {tier}";
                    return false;
                }

                if (nameSeed != seed)
                {
                    reason = $"filename implies seed {nameSeed}, header says {seed}";
                    return false;
                }
            }

            if (lines.Length != size + 2)
            {
                reason = $"expected {size + 2} lines (header + {size} grid rows + path), got {lines.Length}";
                return false;
            }

            var spawn = new Point(-1, -1);
            var exit = new Point(-1, -1);
            int spawnCount = 0;
            int exitCount = 0;
            int obstacleCells = 0;
            var obstacles = new bool[size, size];

            for (int y = 0; y < size; y++)
            {
                string row = lines[1 + y];
                if (row.Length != size)
                {
                    reason = $"grid row {y} has {row.Length} cells, expected {size}";
                    return false;
                }

                for (int x = 0; x < size; x++)
                {
                    char c = row[x];
                    switch (c)
                    {
                        case '#':
                            obstacles[x, y] = true;
                            obstacleCells++;
                            break;
                        case '.':
                        case '+':
                            break;
                        case 'S':
                            spawn = new Point(x, y);
                            spawnCount++;
                            break;
                        case 'E':
                            exit = new Point(x, y);
                            exitCount++;
                            break;
                        default:
                            reason = $"grid row {y} column {x} is '{c}', expected one of # . + S E";
                            return false;
                    }
                }
            }

            if (spawnCount != 1 || exitCount != 1)
            {
                reason = $"expected exactly one 'S' and one 'E', got S={spawnCount} E={exitCount}";
                return false;
            }

            if (declaredObs != obstacleCells)
            {
                reason = $"obs {declaredObs} does not match {obstacleCells} '#' cells";
                return false;
            }

            // hash 只覆盖网格正文（行间 \n、无尾随换行），与写出器逐字对齐。
            string grid = string.Join("\n", lines.Skip(1).Take(size));
            string computedHash = GridHash(grid);
            if (!string.Equals(hash, computedHash, StringComparison.Ordinal))
            {
                reason = $"hash mismatch: header {hash}, computed {computedHash}";
                return false;
            }

            string pathLine = lines[size + 1];
            if (!pathLine.StartsWith("path=", StringComparison.Ordinal))
            {
                reason = "missing 'path=' line";
                return false;
            }

            var cells = new List<Point>();
            var seen = new HashSet<Point>();
            foreach (string token in pathLine.Substring(5).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Split(',');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int px)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int py))
                {
                    reason = $"path token '{token}' is not 'x,y'";
                    return false;
                }

                int index = cells.Count;
                if (px < 0 || px >= size || py < 0 || py >= size)
                {
                    reason = $"path cell {index} ({px},{py}) is outside the map";
                    return false;
                }

                if (obstacles[px, py])
                {
                    reason = $"path cell {index} ({px},{py}) is an obstacle";
                    return false;
                }

                var cell = new Point(px, py);
                if (!seen.Add(cell))
                {
                    reason = $"path cell {index} ({px},{py}) repeats an earlier cell";
                    return false;
                }

                if (cells.Count > 0)
                {
                    Point previous = cells[cells.Count - 1];
                    if (Math.Abs(previous.X - px) + Math.Abs(previous.Y - py) != 1)
                    {
                        reason = $"path cell {index} ({px},{py}) is not 4-connected to previous {previous}";
                        return false;
                    }
                }

                cells.Add(cell);
            }

            if (cells.Count == 0)
            {
                reason = "path is empty";
                return false;
            }

            if (!cells[0].Equals(spawn))
            {
                reason = $"path starts at {cells[0]}, expected spawn {spawn}";
                return false;
            }

            if (!cells[cells.Count - 1].Equals(exit))
            {
                reason = $"path ends at {cells[cells.Count - 1]}, expected exit {exit}";
                return false;
            }

            // ---- 复算层（只比对，不覆盖文件内容）----
            List<Point> kernelPath = FindPath(spawn, exit, obstacles, size, size, null, 0, out _);
            if (kernelPath.Count != cells.Count)
            {
                reason = $"recomputed path has {kernelPath.Count} cells, file has {cells.Count}";
                return false;
            }

            for (int i = 0; i < cells.Count; i++)
            {
                if (!kernelPath[i].Equals(cells[i]))
                {
                    reason = $"recomputed path differs at index {i}: file {cells[i]} vs kernel {kernelPath[i]}";
                    return false;
                }
            }

            loaded.Map = new MapData
            {
                Width = size,
                Height = size,
                Obstacles = obstacles,
                Spawn = spawn,
                Exit = exit,
                Path = cells,
                Metrics = AnalyzePath(cells),
                PathRatio = (double)(cells.Count - 1) / Math.Max(1, ManhattanDistance(spawn, exit)),
                AttemptsUsed = 1,
                ObstacleCount = obstacleCells,
                Seed = seed
            };

            // 分析器的 W 必须与头行一致，否则 n=1/2 判据会用错代价。
            AnalysisBlockCost = blockCost;
            MapAnalysis analysis = AnalyzeMap(loaded.Map, Profile.AnalysisN, Profile.ComboCap);
            if (analysis.MinCut != declaredMinCut)
            {
                reason = $"min-cut mismatch: header k {declaredMinCut}, recomputed {analysis.MinCut}";
                return false;
            }

            // ---- 交叉层之一（清单）：落盘阶段的索引必须与文件头行同值 ----
            if (manifest != null)
            {
                if (!manifest.TryGetValue((size, tier, seed), out ManifestRow? listed))
                {
                    reason = $"manifest has no entry for size {size} tier {tier} seed {seed}";
                    return false;
                }

                if (!string.Equals(listed.Hash, hash, StringComparison.Ordinal))
                {
                    reason = $"manifest hash {listed.Hash} vs header hash {hash}";
                    return false;
                }

                if (listed.BlockCost != blockCost)
                {
                    reason = $"manifest w {listed.BlockCost} vs header w {blockCost}";
                    return false;
                }

                if (listed.Delta != deltaC)
                {
                    reason = $"manifest deltaC {listed.Delta} vs header deltaC {deltaC}";
                    return false;
                }

                if (listed.MinCut != declaredMinCut)
                {
                    reason = $"manifest k {listed.MinCut} vs header k {declaredMinCut}";
                    return false;
                }

                if (listed.ObsCells != obstacleCells)
                {
                    reason = $"manifest obs {listed.ObsCells} vs grid {obstacleCells}";
                    return false;
                }
            }

            // ---- 交叉层之二（证书）：生成阶段记录，必须与同一份地图一致 ----
            if (cert != null)
            {
                if (!cert.TryGetValue((size, tier, seed), out CertRow? stored))
                {
                    reason = $"cert has no row for size {size} tier {tier} seed {seed}";
                    return false;
                }

                if (!stored.Valid)
                {
                    reason = $"cert marks this map invalid: {stored.Reason}";
                    return false;
                }

                if (stored.BlockCost != blockCost)
                {
                    reason = $"cert w {stored.BlockCost} vs header w {blockCost}";
                    return false;
                }

                if (stored.Delta != deltaC)
                {
                    reason = $"cert deltaC {stored.Delta} vs header deltaC {deltaC}";
                    return false;
                }

                if (stored.MinCut != declaredMinCut)
                {
                    reason = $"cert k {stored.MinCut} vs header k {declaredMinCut}";
                    return false;
                }

                // 证书里的 obsRatio 保留 4 位小数，比对时先按同一精度取整。
                double actualRatio = Math.Round((double)obstacleCells / (size * size), 4);
                if (Math.Abs(actualRatio - stored.ObsRatio) > 0.00005)
                {
                    reason = $"cert obsRatio {stored.ObsRatio} vs computed {actualRatio}";
                    return false;
                }
            }

            // ΔC 重算：600 张全量实证零分歧，升为硬断言。
            // 探针失败同样判负——无法复算就等于无法证明拓扑成立。
            if (!ProbeCorridors(obstacles, size, spawn, exit,
                    out _, out int cheapCost, out _, out int expensiveCost))
            {
                reason = "corridor probe failed";
                return false;
            }

            if (expensiveCost - cheapCost != deltaC)
            {
                reason = $"deltaC mismatch: header {deltaC}, recomputed {expensiveCost - cheapCost}"
                         + $" (cheap {cheapCost})";
                return false;
            }

            return true;
        }

        // ------------------------ CLI ------------------------

        private static void RunLoadCheck(string[] args)
        {
            bool directory = args[0].TrimStart('-').Equals("load-dir", StringComparison.OrdinalIgnoreCase);

            string target = string.Empty;
            string certPath = string.Empty;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].TrimStart('-').Equals("cert", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    certPath = args[i + 1];
                    i++;
                    continue;
                }

                if (target.Length == 0) target = args[i];
            }

            if (target.Length == 0)
            {
                Console.WriteLine("usage: --load <file.map> [--cert <cert.json>]"
                                  + "  |  --load-dir <dir> [--cert <cert.json>]");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine("=== P5 map load check ===");

            Dictionary<(int, int, int), CertRow>? cert = null;
            if (certPath.Length > 0)
            {
                if (!TryReadCert(certPath, out Dictionary<(int, int, int), CertRow> parsed, out string certReason))
                {
                    Console.WriteLine($"cert: {certPath} FAIL ({certReason})");
                    Environment.ExitCode = 1;
                    return;
                }

                cert = parsed;
                Console.WriteLine($"cert: {Path.GetFullPath(certPath)} ({parsed.Count} rows)");
            }

            if (!directory)
            {
                bool ok = TryLoadMap(target, cert, null, out LoadedMap single, out string singleReason);
                Console.WriteLine($"{target}: {(ok ? "PASS" : "FAIL")}");
                if (ok) PrintLoadedMap(single);
                else Console.WriteLine($"  reason: {singleReason}");

                Environment.ExitCode = ok ? 0 : 1;
                return;
            }

            if (!Directory.Exists(target))
            {
                Console.WriteLine($"directory not found: {target}");
                Environment.ExitCode = 1;
                return;
            }

            // 目录模式优先用 manifest 作为索引；缺失或损坏时回退扫盘，并跳过清单交叉层。
            Dictionary<(int, int, int), ManifestRow>? manifest = null;
            List<string> files;
            string source;
            List<string> unlisted = new List<string>();

            if (TryReadManifest(target, out Dictionary<(int, int, int), ManifestRow> listed,
                    out List<string> listedFiles, out _))
            {
                manifest = listed;
                files = listedFiles;
                source = "manifest.json";

                // manifest 同时是枚举源：若漏列某张地图，它就会静默逃过全部检查。
                // 故用扫盘结果反查清单覆盖，未列出的文件单独计为失败。
                var listedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in listedFiles) listedPaths.Add(Path.GetFullPath(file));
                foreach (string file in CollectMapFiles(target))
                {
                    if (!listedPaths.Contains(Path.GetFullPath(file))) unlisted.Add(file);
                }
            }
            else
            {
                files = CollectMapFiles(target);
                source = "*.map glob";
            }

            int pass = 0;
            int failed = 0;
            var loadedKeys = new HashSet<(int, int, int)>();

            Console.WriteLine($"dir: {Path.GetFullPath(target)}");
            Console.WriteLine($"source: {source} ({files.Count} maps)");

            foreach (string file in files)
            {
                bool ok = TryLoadMap(file, cert, manifest, out LoadedMap loaded, out string reason);

                // 反向覆盖按“文件存在”判定：头行解析成功即记为已覆盖，与是否通过校验无关。
                if (loaded.DeclaredSize > 0)
                    loadedKeys.Add((loaded.DeclaredSize, loaded.Tier, loaded.DeclaredSeed));

                if (ok) pass++;
                else
                {
                    failed++;
                    Console.WriteLine($"  FAIL {Path.GetRelativePath(target, file)}: {reason}");
                }
            }

            // 反向覆盖：证书宣称有效的地图必须都有对应文件，否则说明导出不全或证书过期。
            int uncovered = 0;
            if (cert != null)
            {
                foreach (KeyValuePair<(int, int, int), CertRow> entry in cert)
                {
                    if (!entry.Value.Valid || loadedKeys.Contains(entry.Key)) continue;

                    uncovered++;
                    if (uncovered <= 5)
                    {
                        Console.WriteLine($"  FAIL cert row size {entry.Key.Item1} tier {entry.Key.Item2} "
                                          + $"seed {entry.Key.Item3}: no matching map file");
                    }
                }
            }

            foreach (string file in unlisted)
            {
                failed++;
                Console.WriteLine($"  FAIL {Path.GetRelativePath(target, file)}: not listed in manifest.json");
            }

            Console.WriteLine($"maps {files.Count}: PASS {pass} | FAIL {failed}"
                              + (manifest == null ? " | manifest cross-check skipped"
                                  : $" | unlisted {unlisted.Count}")
                              + (cert == null ? string.Empty : $" | cert-uncovered {uncovered}"));
            Console.WriteLine(failed == 0 && uncovered == 0 ? "load check: PASS" : "load check: FAIL");

            Environment.ExitCode = failed == 0 && uncovered == 0 ? 0 : 1;
        }

        private static void PrintLoadedMap(LoadedMap loaded)
        {
            MapData map = loaded.Map;
            Console.WriteLine($"  size {map.Width} tier {loaded.Tier} seed {map.Seed} w {loaded.BlockCost} "
                              + $"deltaC {loaded.DeltaC} k {loaded.DeclaredMinCut} obs {map.ObstacleCount}");
            Console.WriteLine($"  spawn {map.Spawn} exit {map.Exit} moves {map.Path.Count - 1} "
                              + $"turns {map.Metrics.Turns} ratio {map.PathRatio:F2}");
        }

        // 只识别 <dir>/<n>x<n>/t<tier>_<seed>.map 这一种约定；不合约定返回 false（引擎可自由重命名）。
        private static bool TryParseNameConvention(string filePath, out int size, out int tier, out int seed)
        {
            size = 0;
            tier = 0;
            seed = 0;

            string? folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(filePath)));
            string leaf = Path.GetFileNameWithoutExtension(filePath);
            if (folder == null) return false;

            int separator = folder.IndexOf('x');
            if (separator <= 0 || separator == folder.Length - 1) return false;
            if (!int.TryParse(folder.Substring(0, separator), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out size)) return false;
            if (!int.TryParse(folder.Substring(separator + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int folderHeight) || folderHeight != size) return false;

            if (leaf.Length < 4 || leaf[0] != 't') return false;
            int underscore = leaf.IndexOf('_', 1);
            if (underscore <= 1 || underscore == leaf.Length - 1) return false;
            if (!int.TryParse(leaf.Substring(1, underscore - 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out tier)) return false;
            if (!int.TryParse(leaf.Substring(underscore + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out seed)) return false;

            return true;
        }

        // 无 manifest 时的回退：递归扫 *.map 并排序，保证输出顺序稳定。
        private static List<string> CollectMapFiles(string dir)
        {
            var files = new List<string>(
                Directory.EnumerateFiles(dir, "*.map", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            return files;
        }
    }
}
