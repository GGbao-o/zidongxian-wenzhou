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

    /// <summary>斜床：半径按0.9537换算后取整，三轴偏移仍参与目标计算。</summary>
    [Fact]
    public void ToAbsoluteTarget_Lathe_Uses_SingleCoefficient()
    {
        var crane = CraneWithOffset(10, -20, 30);

        var (x, y, z) = CraneCoordinate.ToAbsoluteTarget(
            DeviceType.LatheMachine, posX: 1000, posY: 200, posZ: 2000, crane, diameter: 103.1);

        Assert.Equal(1010, x);
        Assert.Equal(180, y);
        Assert.Equal(1976, z);   // 2030 - Round(51.55/0.9537)=2030-54
    }

    /// <summary>双头镗采用双系数补偿，避免仍沿用斜床分支或旧半径公式。</summary>
    [Fact]
    public void ToAbsoluteTarget_Boring_Uses_TwoCoefficients()
    {
        var crane = CraneWithOffset(10, -20, 30);

        var (x, y, z) = CraneCoordinate.ToAbsoluteTarget(
            DeviceType.BoringMachine, posX: 1000, posY: 200, posZ: 2000, crane, diameter: 103.1);

        Assert.Equal(1010, x);
        Assert.Equal(180, y);
        Assert.Equal(1916, z);   // 2030 - Round(51.55/0.9537+51.55/0.866)=2030-114
    }

    /// <summary>逐类核对固定直径的取整补偿，覆盖两类公式和双头镗所属分支。</summary>
    /// <param name="type">当前自动搬运支持的设备类型。</param>
    /// <param name="expectedDrop">直径103.1时的预期补偿。</param>
    [Theory]
    [InlineData(DeviceType.LatheMachine, 54d)]
    [InlineData(DeviceType.GrinderMachine, 54d)]
    [InlineData(DeviceType.BoringMachine, 114d)]
    [InlineData(DeviceType.LoadRack, 114d)]
    [InlineData(DeviceType.MarkerMachine, 114d)]
    [InlineData(DeviceType.UnloadRack, 114d)]
    public void ZDrop_Matches_DeviceFormula(DeviceType type, double expectedDrop)
    {
        Assert.Equal(expectedDrop, CraneCoordinate.ZDrop(type, 103.1));
    }
}
