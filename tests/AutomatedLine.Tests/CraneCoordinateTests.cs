using AutomatedLine.Application.Transfer;
using AutomatedLine.Domain.Cranes;
using AutomatedLine.Domain.Devices;

namespace AutomatedLine.Tests;

/// <summary>
/// 天车坐标换算测试：设备 X/Y/Z + 本车偏移，Z 再减下压量。
/// </summary>
public class CraneCoordinateTests
{
    /// <summary>构造一台天车并给定三轴偏移。</summary>
    private static Crane CraneWithOffset(double ox, double oy, double oz)
    {
        var crane = new Crane(1, 1, "天车1");
        crane.OffsetX = ox;
        crane.OffsetY = oy;
        crane.OffsetZ = oz;
        return crane;
    }

    /// <summary>斜床/研磨：Z 下压 = Round(d/2)；X/Y 加偏移。</summary>
    [Fact]
    public void ToAbsoluteTarget_Lathe_Uses_HalfDiameter()
    {
        var crane = CraneWithOffset(10, -20, 30);

        var (x, y, z) = CraneCoordinate.ToAbsoluteTarget(
            DeviceType.LatheMachine, posX: 1000, posY: 200, posZ: 2000, crane, diameter: 103.1);

        Assert.Equal(1010, x);
        Assert.Equal(180, y);
        Assert.Equal(1978, z);   // 2030 - Round(103.1/2)=2030-52
    }

    /// <summary>带托料架双头镗与斜床相同，按半径下压，三轴偏移仍参与目标计算。</summary>
    [Fact]
    public void ToAbsoluteTarget_Boring_Uses_HalfDiameter()
    {
        var crane = CraneWithOffset(10, -20, 30);

        var (x, y, z) = CraneCoordinate.ToAbsoluteTarget(
            DeviceType.BoringMachine, posX: 1000, posY: 200, posZ: 2000, crane, diameter: 103.1);

        Assert.Equal(1010, x);
        Assert.Equal(180, y);
        Assert.Equal(1978, z);   // 2030 - Round(103.1/2)=2030-52
    }

    /// <summary>Z 下压量单独可算（供测试/日志）。</summary>
    [Fact]
    public void ZDrop_Matches_Formulas()
    {
        Assert.Equal(52d, CraneCoordinate.ZDrop(DeviceType.LatheMachine, 103.1));
        Assert.Equal(52d, CraneCoordinate.ZDrop(DeviceType.GrinderMachine, 103.1));
        Assert.Equal(52d, CraneCoordinate.ZDrop(DeviceType.BoringMachine, 103.1));
        // 料架仍沿用支撑结构的双系数公式，双头镗调整不能改变料架取放高度。
        Assert.Equal(114d, CraneCoordinate.ZDrop(DeviceType.LoadRack, 103.1));
    }
}
