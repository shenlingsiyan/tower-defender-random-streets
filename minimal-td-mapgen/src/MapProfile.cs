namespace MinimalTowerDefenseMapV3
{
    internal static partial class Program
    {
        // ============================================================
        // P3 参数载体
        // θ 生成器的参数原先散落在 MapAnalyzer / MapSkeleton 的常量里，
        // 现在统一收到这里：数值与旧常量逐位相同，行为不变。
        // ============================================================

        // 档位规格。N 是 MinN 语义：至少封锁 N 格才要求改道
        //（挡得越多长廊只会更划算，因此 tier1 在 n=2 必然仍改道，不存在「恰好 N 格」）。
        private sealed class TierSpec
        {
            public readonly int N;
            public double KillzoneMinRatio;

            public TierSpec(int n, double killzoneMinRatio)
            {
                N = n;
                KillzoneMinRatio = killzoneMinRatio;
            }
        }

        private sealed class GenProfile
        {
            // 判据阈值（原 MapAnalyzer 常量）
            public int BlockCost = 10;                 // W：玩家占格后的通行代价
            public double OverlapLimit = 0.35;         // 新旧路线最大允许重叠
            public double HalfSeparationLimit = 0.70;  // 各自落在自己一侧的最低比例
            public double DeltaMin = 0.08;             // 改道后最低成本增幅
            public double DeltaMax = 0.35;             // 改道后最高成本增幅
            public int AnalysisN = 2;                  // 默认扫描 n = 1..2
            public int ComboCap = 4000;                // 单张地图每个 n 的组合上限

            // 骨架
            public int ThetaBlockCost = 4;             // 兜底 W（实际取值由 TryFitTiers 按尺寸拟合）
            public int ThetaMinSize = 11;
            public int MaxThetaW = 16;                 // 拟合时 W 的搜索上界

            // 装饰
            public double TargetDensity = 0.25;        // 障碍软目标比例

            public readonly TierSpec[] Tiers =
            {
                new TierSpec(1, 1.3),
                new TierSpec(2, 1.3)
            };
        }

        // 用属性而非字段：跨文件的静态字段初始化顺序会让空引用分析报 CS8602。
        private static GenProfile Profile => _profile ??= new GenProfile();
        private static GenProfile? _profile;

        private static TierSpec TierOf(int tier) => Profile.Tiers[tier - 1];
    }
}
