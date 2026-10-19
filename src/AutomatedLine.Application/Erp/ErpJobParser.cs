using System.Globalization;
using AutomatedLine.Domain.Workpieces;

namespace AutomatedLine.Application.Erp;

/// <summary>将ERP逗号分隔行转换为原始工件数据；不做PLC倍率换算，不启动工件。</summary>
public static class ErpJobParser
{
    /// <summary>解析前八列并按现有录入规则校验；额外列忽略，失败保留原行供修正。</summary>
    /// <param name="line">一个非空原始行，字段中不能含分隔逗号</param>
    /// <param name="info">成功时的工件数据，含新运行期Guid</param>
    /// <param name="error">失败字段和原因</param>
    /// <returns>合法返回true，非法返回false</returns>
    public static bool TryParse(string line, out WorkpieceInfo? info, out string? error)
    {
        info = null;
        error = null;
        var columns = line.Split(',').Select(value => value.Trim()).ToArray();
        if (columns.Length < 8) return Reject($"至少需要8列，实际{columns.Length}列", out error);
        if (columns[1].Length == 0) return Reject("第2列版号不能为空", out error);
        if (!int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var serial) || serial < 0)
            return Reject("第3列序号必须为非负整数（>=0）", out error);
        if (!ReadNumber(columns[3], out var length) || length <= 0)
            return Reject("第4列长度必须为有限正数", out error);
        if (!ReadNumber(columns[4], out var diameter) || diameter <= 0)
            return Reject("第5列直径必须为有限正数", out error);
        if (!int.TryParse(columns[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hole) || hole is not (70 or 100))
            return Reject("第6列堵孔只能为70或100", out error);
        if (!int.TryParse(columns[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode)
            || mode < WorkpieceInfo.LatheMachiningModeMin || mode > WorkpieceInfo.LatheMachiningModeMax)
            return Reject("第7列斜床工艺必须为1至10的整数", out error);
        if (!ReadNumber(columns[7], out var thickness))
            return Reject("第8列堵厚必须为有限数值", out error);

        info = new WorkpieceInfo
        {
            PrintContent = columns[0], PlateNo = columns[1], SerialNo = serial,
            Length = length, Diameter = diameter, PlugHole = hole,
            LatheMachiningMode = mode, PlugThickness = thickness
        };
        return true;
    }

    // ERP固定使用小数点，不能随服务器区域设置改变解析结果；NaN/Infinity不能进入加工数据。
    private static bool ReadNumber(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool Reject(string message, out string? error)
    {
        error = message;
        return false;
    }
}
