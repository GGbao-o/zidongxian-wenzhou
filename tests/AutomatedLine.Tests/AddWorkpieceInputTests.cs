using System.Globalization;
using AutomatedLine.Domain.Workpieces;
using AutomatedLine.Ui.ViewModels;

namespace AutomatedLine.Tests;

/// <summary>验证新增和编辑工件确认时解析小数，输入中间状态和错误文本不会使用旧数值。</summary>
public sealed class AddWorkpieceInputTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    /// <summary>固定小数语法，避免开发机区域设置改变非法文本案例的含义。</summary>
    public AddWorkpieceInputTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    /// <summary>恢复当前测试上下文的区域设置，不影响其他测试。</summary>
    public void Dispose() => CultureInfo.CurrentCulture = _originalCulture;

    /// <summary>确认时保留三个尺寸的小数，同时不格式化输入文本或破坏序号0。</summary>
    [Fact]
    public void Confirm_PreservesDecimalsAndOriginalText()
    {
        var vm = ValidInput();
        Assert.True(vm.Validate());
        var info = vm.BuildWorkpieceInfo();
        Assert.Equal(180.3, info.Diameter);
        Assert.Equal(1200.1, info.Length);
        Assert.Equal(25.5, info.PlugThickness);
        Assert.Equal(0, info.SerialNo);
        Assert.Equal("180.30", vm.DiameterText);
        Assert.Equal("1200.10", vm.LengthText);
        Assert.Equal("25.50", vm.PlugThicknessText);
    }

    /// <summary>覆盖曾成功输入后的清空、错误字符及非有限值；三个尺寸均不能回退到旧值。</summary>
    /// <param name="text">应拒绝的输入。</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("bad")]
    [InlineData("180..3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e309")]
    [InlineData("1,2")]
    public void InvalidDimension_CannotSubmitPreviousValue(string text)
    {
        foreach (var field in new[] { "diameter", "length", "thickness" })
        {
            var vm = ValidInput();
            Assert.True(vm.Validate());
            switch (field)
            {
                case "diameter": vm.DiameterText = text; break;
                case "length": vm.LengthText = text; break;
                case "thickness": vm.PlugThicknessText = text; break;
            }
            Assert.False(vm.Validate());
            Assert.Throws<InvalidOperationException>(() => vm.BuildWorkpieceInfo());
        }
    }

    /// <summary>修复文本输入不放宽直径及长度必须为正数的原业务规则。</summary>
    /// <param name="text">非正尺寸。</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-1.5")]
    public void DiameterAndLength_StillRequirePositiveValues(string text)
    {
        var vm = ValidInput();
        vm.DiameterText = text;
        Assert.False(vm.Validate());
        vm = ValidInput();
        vm.LengthText = text;
        Assert.False(vm.Validate());
    }

    /// <summary>堵厚仍只要求可解析数值，不在本次输入修复中增加正数范围限制。</summary>
    /// <param name="text">原有规则允许的有限堵厚。</param>
    /// <param name="expected">确认后数值。</param>
    [Theory]
    [InlineData("0", 0d)]
    [InlineData("-2.5", -2.5d)]
    public void Thickness_KeepsExistingRange(string text, double expected)
    {
        var vm = ValidInput();
        vm.PlugThicknessText = text;
        Assert.True(vm.Validate());
        Assert.Equal(expected, vm.BuildWorkpieceInfo().PlugThickness);
    }

    /// <summary>错误文本修正后即可确认，验证失败不会改变原输入。</summary>
    [Fact]
    public void CorrectedText_CanBeSubmitted()
    {
        var vm = ValidInput();
        vm.DiameterText = ".";
        Assert.False(vm.Validate());
        Assert.Equal(".", vm.DiameterText);
        vm.DiameterText = ".5";
        Assert.True(vm.Validate());
        Assert.Equal(0.5, vm.BuildWorkpieceInfo().Diameter);
    }

    /// <summary>新增与编辑共用解析规则，按当前区域回填且保留原工件身份及小数。</summary>
    /// <param name="culture">当前区域。</param>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("de-DE")]
    public void Edit_RoundTripsDecimalsAndIdentity(string culture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        var original = new WorkpieceInfo
        {
            PlateNo = "EDIT", SerialNo = 0, Diameter = 180.3, Length = 1200.1,
            PlugThickness = 25.5, PlugHole = 100, LatheMachiningMode = 3
        };
        var vm = new AddWorkpieceViewModel();
        vm.LoadFrom(original);
        Assert.True(vm.IsEditMode);
        Assert.False(vm.IdentityEditable);
        Assert.Equal(original.Diameter.ToString(CultureInfo.CurrentCulture), vm.DiameterText);
        Assert.True(vm.Validate());
        var info = vm.BuildWorkpieceInfo();
        Assert.Equal(original.RuntimeId, info.RuntimeId);
        Assert.Equal(original.Diameter, info.Diameter);
        Assert.Equal(original.Length, info.Length);
        Assert.Equal(original.PlugThickness, info.PlugThickness);
        Assert.Equal(original.LatheMachiningMode, info.LatheMachiningMode);
    }

    private static AddWorkpieceViewModel ValidInput() => new()
    {
        PlateNo = "P", SerialNo = "0", DiameterText = "180.30", LengthText = "1200.10",
        PlugThicknessText = "25.50", PlugHole = 70, LatheMachiningMode = 1
    };
}
