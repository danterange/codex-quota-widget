namespace CodexQuotaWidget.Models;

/// <summary>保存不会包含账号凭据的本地小组件偏好。</summary>
public sealed record WidgetSettings(int RefreshIntervalSeconds = 10);
