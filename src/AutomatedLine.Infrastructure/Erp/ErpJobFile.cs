using System.Text;
using AutomatedLine.Application.Erp;

namespace AutomatedLine.Infrastructure.Erp;

/// <summary>ERP磁盘文件读写；按写完批次后读取约定工作，清理采用同目录临时文件替换。</summary>
public sealed class ErpJobFile : IErpJobFile
{
    /// <summary>注册中文旧编码支持，允许参数配置为gbk。</summary>
    public ErpJobFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <inheritdoc />
    public async Task<string[]?> ReadAsync(string path, string encoding, CancellationToken cancellationToken)
    {
        // 仅“没有文件”作为正常等待；权限/解码失败应向应用层报告，不能被误当成空文件。
        try
        {
            return await File.ReadAllLinesAsync(path, ResolveEncoding(encoding), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <inheritdoc />
    public async Task WriteRemainingAsync(string path, IReadOnlyList<string> lines, string encoding, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 先完整写好剩余数据再替换，写临时文件失败不会提前截断ERP原文件。
            await File.WriteAllLinesAsync(temporaryPath, lines, ResolveEncoding(encoding), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            // 清理仅针对本次生成的临时文件；失败不遮盖原有读写异常。
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Encoding ResolveEncoding(string name)
    {
        var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        // UTF8不生成BOM，整批接收后的空文件应真正为0字节；读取仍可识别ERP已有BOM。
        return encoding.CodePage == 65001 ? new UTF8Encoding(false, true) : encoding;
    }
}
