using AutomatedLine.Application.Configuration;

namespace AutomatedLine.Tests;

/// <summary>
/// 设备配置 DTO 的 X/Y/Z 绝对坐标测试（Y/Z 为 v2.38 新增，供状态机现查）。
/// </summary>
public class DevicePositionTests
{
    /// <summary>DTO 应能携带并读取 Y/Z 绝对坐标。</summary>
    [Fact]
    public void DeviceConfigDto_Carries_PositionYZ()
    {
        var dto = new DeviceConfigDto { PositionX = 1000, PositionY = 200, PositionZ = 2000 };

        Assert.Equal(1000d, dto.PositionX);
        Assert.Equal(200d, dto.PositionY);
        Assert.Equal(2000d, dto.PositionZ);
    }
}
