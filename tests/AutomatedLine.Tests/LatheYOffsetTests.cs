using AutomatedLine.Application.Configuration;
using AutomatedLine.Application.Transfer;

namespace AutomatedLine.Tests;

/// <summary>
/// 斜床 Y 二次偏移计算测试（《应用层设计.md》§7.2）。
/// </summary>
public class LatheYOffsetTests
{
    /// <summary>大孔(100)：YOffset = Round((顶尖距离-长度)/2 + 122)。</summary>
    [Fact]
    public void Compute_BigHole()
    {
        var opt = new LatheTransferOptions { TailstockCenterDistance = 800, BigHoleOffset = 122, SmallHoleOffset = 55 };
        var y = LatheYOffset.Compute(opt, plugHole: 100, length: 700);
        Assert.Equal(172, y);   // (800-700)/2 + 122 = 50+122
    }

    /// <summary>小孔(70)：YOffset = Round((顶尖距离-长度)/2 + 55)。</summary>
    [Fact]
    public void Compute_SmallHole()
    {
        var opt = new LatheTransferOptions { TailstockCenterDistance = 800, BigHoleOffset = 122, SmallHoleOffset = 55 };
        var y = LatheYOffset.Compute(opt, plugHole: 70, length: 690);
        Assert.Equal(110, y);   // (800-690)/2 + 55 = 55+55
    }

    /// <summary>结果取整（四舍五入）。</summary>
    [Fact]
    public void Compute_Rounds()
    {
        var opt = new LatheTransferOptions { TailstockCenterDistance = 801, BigHoleOffset = 122, SmallHoleOffset = 55 };
        var y = LatheYOffset.Compute(opt, plugHole: 100, length: 700);
        Assert.Equal(173, y);   // (801-700)/2=50.5 + 122 = 172.5 → 173
    }
}
