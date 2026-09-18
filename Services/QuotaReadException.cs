namespace CodexQuotaWidget.Services;

/// <summary>只携带预定义的可公开提示，不把服务端原始日志或账号信息传入 UI。</summary>
public sealed class QuotaReadException : Exception
{
    /// <summary>将读取失败转换为用户可操作的短提示和说明。</summary>
    public QuotaReadException(string message, string detail) : base(message)
    {
        Detail = detail;
    }

    public string Detail { get; }
}
