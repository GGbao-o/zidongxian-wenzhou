using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Application.Transfer;

/// <summary>
/// 斜床 Y 二次偏移计算（《应用层设计.md》§7.2）。
/// YOffset = Round( (顶尖距离 − 工件长度) / 2 + 孔径偏移 )；大孔 122、小孔 55。
/// </summary>
public static class LatheYOffset
{
    /// <summary>大孔（PlugHole=100）对应的孔径偏移取值判定。</summary>
    public const int BigHolePlug = 100;

    /// <summary>
    /// 计算某工件的 Y 二次偏移量（取整）。
    /// </summary>
    /// <param name="options">该斜床的参数</param>
    /// <param name="plugHole">工件堵孔尺寸（100=大孔，70=小孔）</param>
    /// <param name="length">工件长度</param>
    /// <returns>Y 偏移量（取整）</returns>
    public static int Compute(LatheTransferOptions options, int plugHole, double length)
    {
        var holeOffset = plugHole == BigHolePlug ? options.BigHoleOffset : options.SmallHoleOffset;
        // 四舍五入（.5 进位），不用 Math.Round 默认的银行家舍入
        return (int)Math.Round((options.TailstockCenterDistance - length) / 2.0 + holeOffset, MidpointRounding.AwayFromZero);
    }
}
