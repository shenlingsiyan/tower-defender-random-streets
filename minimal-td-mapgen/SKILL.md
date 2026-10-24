---
name: v4-mapgen
description: 生成与校验极简塔防地图。当用户需要生成塔防地图、调整地图尺寸或难度档、导出 .map 资产、回读校验地图文件、复跑 A1–A13 回归，或诊断最小割/换路率指标时使用。
---

# V4 地图生成器技能

## 何时启用

- 要一张或一批塔防地图（`.map` 文本）
- 要校验外部给来的地图文件是否被篡改或损坏
- 要改生成逻辑后确认没有破坏既有不变量
- 要解释/诊断 `k`、`ΔC`、换路率、Killzone 等指标

## 快速命令

```powershell
# 一张地图（默认 V3 引擎，形态多样）
dotnet run -c Release

# 一张 θ 地图（玩法性质可证明，tier 决定难度）
dotnet run -c Release -- --engine theta --size 12 --tier 2 --seed 20261022

# 批量资产 + 证书 + 回归
dotnet run -c Release -- --theta --from 11 --to 16 --count 50 --regress `
  --csv out/sweep.csv --cert out/cert.json --export out/maps

# 回读校验（含证书与清单交叉）
dotnet run -c Release -- --load-dir out/maps --cert out/cert.json

# 只出可行性矩阵，不生成地图
dotnet run -c Release -- --theta --from 11 --to 16 --profile-check
```

## 文件职责

| 文件 | 职责 | 改这里的风险 |
|---|---|---|
| `src/MapGeneratorV3_1.cs` | 入口、Settings、CLI、数据结构 | 低（但别在 θ 分支调用 `PrintSummary`，它会打印假的 PASS） |
| `src/MapProfile.cs` | 全部可调参数 | 低 |
| `src/MapAnalyzer.cs` | 判据、min-cut、换路率、CSV | **高**：改判据会改历史基线 |
| `src/MapSkeleton.cs` | 骨架、装饰、证书、导出、回归 | **高**：改骨架会动 k 与 ΔC |
| `src/MapLoader.cs` | 解析与四层校验 | 中：改校验口径会改 PASS 定义 |

## 硬约定

1. .NET 10.0，`Nullable=enable`，`ImplicitUsings=disable`；Release 必须 **0 警告 0 错误**。
2. 全部可调参数只能放 `MapProfile`，不得散落成局部常量。
3. 种子公式固定：`seed = 20261022 + size*101 + tier*7919 + i*31`。导出文件与证书按同一公式一一对应，改公式等于让证书失效。
4. 同一 `(size, tier, seed, engine, 参数)` 必须**逐字节**复现；禁用时钟、`Guid` 等非种子派生随机源。
5. 换路率等指标在**层 4（`AnalyzeMap`）**计算，属度量而非门禁；不要把它塞进入接受条件而不改回归。

## 改生成逻辑后的必做步骤

```powershell
dotnet build -c Release --nologo -v q      # 必须 0 警告
dotnet run -c Release -- --theta --from 11 --to 16 --count 50 --regress --export out/maps
dotnet run -c Release -- --load-dir out/maps
```

两项都要 PASS，且 `--export` 的两次运行 SHA256 必须相同。

## 已知陷阱（不要重复踩）

1. **直廊下装饰门禁空转**：两条走廊都是直线时，路径外落子不改变主路成本，`rejected` 实测恒为 0。别以为门禁在把关。
2. **换路率是二值**：`n×(W-1) > ΔC` 在 θ 拓扑下是确定的阶跃，只有 0% 或 100%。要中间值需改封边比例（见 `docs/P6-pin-pocket-feasibility.md`）。
3. **θ 地图不满足 V3 形态判据**：转弯恒 2、U 型 0、路径比 1.13~1.20，跑旧形状窗口必然全失败。
4. **换路率不能离线复验**：`.map` 不写该指标，`MapLoader` 只重算 `k` 与 `ΔC`。
5. **`manifest.json` 的 `generatedAt` 是时间戳**，不可复现；可复现的只有地图文件本体。
