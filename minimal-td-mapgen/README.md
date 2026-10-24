# Minimal Tower Defense Map Generator

极简塔防地图生成器。双引擎：默认 **V3 拒绝采样**（形态多样），可选 **θ 骨架构造式**（玩法性质可证明）。

输出可人读、可 diff 的极简文本地图，并提供多层回读校验。同一组参数逐字节复现。

- 无外部依赖，单个 .NET 控制台项目
- 地图以纯文本 `.map` 保存，便于版本管理与人工审阅
- 生成结果附带证书（JSON）、清单（JSON）与回归检查

## 环境要求

- .NET SDK 10.0 或更高

## 快速开始

```powershell
dotnet build -c Release            # 期望 0 警告 0 错误
dotnet run   -c Release            # 默认 = V3 引擎，生成一张 12×12 地图
```

## 目录结构

```
.
├── README.md
├── LICENSE
├── .gitignore
├── SKILL.md                       # 供 AI agent 使用的技能说明
├── MapGeneratorV3_1.csproj
├── src/
│   ├── MapGeneratorV3_1.cs         # 入口 / Settings / 主流程 / CLI / 数据结构
│   ├── MapProfile.cs               # 全部可调参数的唯一载体
│   ├── MapAnalyzer.cs              # 判据、min-cut、换路率、批量 CSV
│   ├── MapSkeleton.cs              # θ 骨架、装饰、证书、导出、回归 A1–A13
│   └── MapLoader.cs                # 文本地图解析与四层回读校验
└── docs/
    └── P6-pin-pocket-feasibility.md   # pin/pocket 换路率可调性实验规划
```

## CLI

| 命令 | 作用 |
|---|---|
| （无参数） | V3 生成一张 12×12 地图并打印路径与摘要 |
| `--size N --seed N` | 覆盖尺寸（方形）与种子，适用于主流程 |
| `--engine v3\|theta` | 指定引擎 |
| `--tier 1\|2` | θ 难度档（仅 `--engine theta` 有效） |
| `--theta --from 11 --to 16 --count 50` | θ 批量生成 + 可行性矩阵 |
| `--theta ... --regress` | 跑 A1–A13 回归 |
| `--theta ... --csv <path> --cert <path>` | 导出逐图 CSV 与证书 JSON |
| `--theta ... --export <dir> [--bare]` | 导出极简文本地图（默认装饰后） |
| `--load <file> \| --load-dir <dir> [--cert <path>]` | 回读校验，失败退出码 1 |
| `--analyze [--count N]` | 批量生成并输出分析 CSV |
| `--sweep [--from 11] [--to 16]` | 尺寸扫描 |
| `--set k=v` | θ 参数覆盖（`density` / `kz-min` / `delta-window`） |

## 两个引擎

| | V3 拒绝采样（默认） | θ 骨架（`--engine theta`） |
|---|---|---|
| 方式 | 随机撒障碍 + 多判据筛选，最多 10000 次 | 构造式，1 次成型 |
| 形态 | 转弯 4~13，U 型 1~2 | 恒 2 次转弯，无 U 型 |
| 挡 n 格换半场 | 约 0.7%（11/100 张有解） | tier1/tier2 分别为 100% / 0%→100% |
| 最小割 | k=1 占 76% | 恒 k=2 |
| 确定可复现 | 是（P0 内核统一后） | 是（逐字节） |

## 校验架构（6 层）

| 层 | 位置 | 作用 |
|---|---|---|
| 1 寻路内核 | `FindPath` | 字典序 Dijkstra，保证生成与运行时路径一致 |
| 2 骨架 | `BuildThetaSkeleton` | 保证 k=2、ΔC 与档位匹配 |
| 3 装饰门禁 | `DecorateTheta` | 逐格校验主路成本、ΔC、KZ 比 |
| 4 分析 | `AnalyzeMap` | 计算 min-cut、换路率等**度量**，不参与接受判定 |
| 5 记录 | 证书 / CSV / `.map` 头行 | 承载度量结果 |
| 6 回归 | A1–A13 + `MapLoader` 四层回读 | 防漂移、防篡改 |

## 地图文本格式

```
size=11 tier=1 seed=20270052 w=4 deltaC=2 k=2 obs=31 hash=5925d167512bce30
.....#.....
###########
+++++++++++
E#########S
...
path=10,4 10,3 9,3 ...
```

`#` 障碍 / `.` 空地 / `+` 主路 / `S` 起点 / `E` 终点；`hash` = 网格正文 sha256 前 16 位。

## 验证命令

```powershell
dotnet run -c Release -- --theta --from 11 --to 16 --count 50 --regress `
  --csv analysis/theta/sweep.csv --cert analysis/theta/cert.json `
  --export analysis/theta/maps
# 期望：A1–A13 全 PASS、regression: PASS、退出码 0

dotnet run -c Release -- --load-dir analysis/theta/maps --cert analysis/theta/cert.json
# 期望：PASS 600 | FAIL 0 | unlisted 0 | cert-uncovered 0
```

## 已知局限

1. **换路率只有 0/100 两态**：判据 `n×(W-1) > ΔC` 在 θ 拓扑下是确定的阶跃，无中间值；预测方案见 `docs/P6-pin-pocket-feasibility.md`。
2. **装饰门禁在直廊下空转**：实测 600 张 `rejected` 均值 0——路径外落子本就不改变主路成本。
3. **换路率不可离线复验**：`.map` 不记录该指标，`MapLoader` 只重算 k 与 ΔC。
4. **小尺寸布局重复**：θ 在 11×11 上 100 张仅约 51 个不同布局（装饰饱和）。
5. **V3 障碍数恒定**：100 张全部 36/144，无多样性。

## 约束

- `Nullable=enable`，`ImplicitUsings=disable`；Release 必须 0 警告。
- 种子公式：`seed = 20261022 + size*101 + tier*7919 + i*31`（导出的文件与证书一一对应）。
- 同一 `(size, tier, seed, engine, 参数)` 必须**逐字节**复现；禁用时钟、`Guid` 等非种子派生随机源。

## 许可

MIT，详见 [LICENSE](LICENSE)。
