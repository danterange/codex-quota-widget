using System.Windows;
using System.Windows.Media;
using CodexQuotaWidget.Models;
using MediaColor = System.Windows.Media.Color;

namespace CodexQuotaWidget.Services;

/// <summary>
/// 通过替换动态资源字典中的语义画刷切换小组件主题，避免 WPF 冻结共享画刷后阻止运行时改色。
/// </summary>
internal static class WidgetThemePalette
{
    private static readonly Palette Night = new()
    {
        Line = "#51A8C7E8", PrimaryText = "#F4F7FB", SecondaryText = "#BAC7DB", MutedText = "#7E8BA1",
        Accent = "#5BA7FF", AccentHover = "#8CC5FF", AccentSoft = "#243B60", Control = "#202736",
        ControlHover = "#31415A", ControlPressed = "#161D2A", ControlBorder = "#465775", ControlDisabled = "#252C38",
        Panel = "#111722", PanelBorder = "#344258", Surface = "#172130", Card = "#1A2435", CardBorder = "#3A4D68",
        Weekly = "#B39CFF", WeeklySoft = "#342B5C", Success = "#55D6BE", QuotaGreen = "#55D6BE", QuotaPurple = "#B39CFF", QuotaYellow = "#FFE000", QuotaRed = "#FF7B72", Rail = "#182235", RailHover = "#293B57",
        RailSelected = "#234A78", SummaryCard = "#2A18243A", QuotaCard = "#2D14243A", CreditCard = "#2A1C2A3D",
        ProgressTrack = "#3556647A", ChromeHover = "#2EFFFFFF", ChromePressed = "#46FFFFFF", ChromeActive = "#304D9FFF",
        DangerHover = "#BCB73A4A", OnAccent = "#07111F", Tooltip = "#F41A2433", TooltipBorder = "#9C8AB5D6", TooltipText = "#F4F7FB",
        TransparentSurface = ["#B5192A40", "#B2172030", "#B42E2648"], OpaqueSurface = ["#192A40", "#172030", "#2C2744"]
    };

    private static readonly Palette Day = new()
    {
        Line = "#6B8BA7C4", PrimaryText = "#142033", SecondaryText = "#4A5B72", MutedText = "#708097",
        Accent = "#2563EB", AccentHover = "#1D4ED8", AccentSoft = "#D9E8FF", Control = "#F7FAFE",
        ControlHover = "#E9F1FB", ControlPressed = "#DDEAF8", ControlBorder = "#B5C7DC", ControlDisabled = "#E3EAF2",
        Panel = "#F2F7FC", PanelBorder = "#C2D1E1", Surface = "#FFFFFF", Card = "#FFFFFF", CardBorder = "#C7D6E8",
        Weekly = "#7652D6", WeeklySoft = "#EAE3FC", Success = "#087F6B", QuotaGreen = "#087F6B", QuotaPurple = "#6E43C5", QuotaYellow = "#846100", QuotaRed = "#B52C37", Rail = "#F5F9FD", RailHover = "#E7F0FA",
        RailSelected = "#DCEBFF", SummaryCard = "#EFFFFFFF", QuotaCard = "#F8FFFFFF", CreditCard = "#F2F8FCFF",
        ProgressTrack = "#D5E1ED", ChromeHover = "#DDEBFA", ChromePressed = "#CDE0F5", ChromeActive = "#D9E8FF",
        DangerHover = "#FFE1E4", OnAccent = "#FFFFFF", Tooltip = "#FA172335", TooltipBorder = "#FF78A0C9", TooltipText = "#F7FAFF",
        TransparentSurface = ["#EAF7FAFF", "#EAF0F6FD", "#EAF5F0FF"], OpaqueSurface = ["#F7FAFF", "#F1F6FD", "#F8F4FF"]
    };

    private static readonly Palette Glass = new()
    {
        Line = "#8B93D6FF", PrimaryText = "#F7FAFF", SecondaryText = "#D0DAEB", MutedText = "#93A4BD",
        Accent = "#75B8FF", AccentHover = "#A9D5FF", AccentSoft = "#395B8C", Control = "#6B233650",
        ControlHover = "#84516F97", ControlPressed = "#94506D8B", ControlBorder = "#7F8BAED2", ControlDisabled = "#3A4A5C72",
        Panel = "#701B2940", PanelBorder = "#7E91B1D6", Surface = "#6826354D", Card = "#A0273A57", CardBorder = "#608AAAD1",
        Weekly = "#C2A9FF", WeeklySoft = "#8B3E807A", Success = "#75E4CF", QuotaGreen = "#75E4CF", QuotaPurple = "#C2A9FF", QuotaYellow = "#FFE452", QuotaRed = "#FF9A92", Rail = "#54283E61", RailHover = "#7049637D",
        RailSelected = "#94315D9A", SummaryCard = "#9A243A58", QuotaCard = "#A6293D5C", CreditCard = "#9E243B5A",
        ProgressTrack = "#5DCCE0FF", ChromeHover = "#4DFFFFFF", ChromePressed = "#68FFFFFF", ChromeActive = "#704A8CD0",
        DangerHover = "#C4C84D65", OnAccent = "#07111F", Tooltip = "#FA142138", TooltipBorder = "#C084C6FF", TooltipText = "#F7FAFF",
        TransparentSurface = ["#6C20334F", "#6218253B", "#70462F62"], OpaqueSurface = ["#263D5A", "#1E304A", "#45315E"]
    };

    /// <summary>将指定主题的语义色替换进动态资源字典；已加载控件会重新解析令牌而不会保留被冻结的旧画刷。</summary>
    internal static void Apply(WidgetTheme theme)
    {
        if (System.Windows.Application.Current?.Resources is not ResourceDictionary resources)
        {
            return;
        }

        var palette = SystemParameters.HighContrast
            ? CreateHighContrastPalette()
            : theme switch
            {
                WidgetTheme.Day => Day,
                WidgetTheme.Glass => Glass,
                _ => Night
            };
        SetSolid(resources, "LineBrush", palette.Line);
        SetSolid(resources, "PanelBrush", palette.Panel);
        SetSolid(resources, "PanelBorderBrush", palette.PanelBorder);
        SetSolid(resources, "SurfaceBrush", palette.Surface);
        SetSolid(resources, "CardBrush", palette.Card);
        SetSolid(resources, "CardBorderBrush", palette.CardBorder);
        SetSolid(resources, "PrimaryTextBrush", palette.PrimaryText);
        SetSolid(resources, "SecondaryTextBrush", palette.SecondaryText);
        SetSolid(resources, "MutedTextBrush", palette.MutedText);
        SetSolid(resources, "AccentBrush", palette.Accent);
        SetSolid(resources, "AccentHoverBrush", palette.AccentHover);
        SetSolid(resources, "AccentSoftBrush", palette.AccentSoft);
        SetSolid(resources, "ControlBrush", palette.Control);
        SetSolid(resources, "ControlHoverBrush", palette.ControlHover);
        SetSolid(resources, "ControlPressedBrush", palette.ControlPressed);
        SetSolid(resources, "ControlBorderBrush", palette.ControlBorder);
        SetSolid(resources, "ControlDisabledBrush", palette.ControlDisabled);
        SetSolid(resources, "WeeklyBrush", palette.Weekly);
        SetSolid(resources, "WeeklySoftBrush", palette.WeeklySoft);
        SetSolid(resources, "SuccessBrush", palette.Success);
        SetSolid(resources, "QuotaGreenBrush", palette.QuotaGreen);
        SetSolid(resources, "QuotaPurpleBrush", palette.QuotaPurple);
        SetSolid(resources, "QuotaYellowBrush", palette.QuotaYellow);
        SetSolid(resources, "QuotaRedBrush", palette.QuotaRed);
        SetSolid(resources, "RailBrush", palette.Rail);
        SetSolid(resources, "RailHoverBrush", palette.RailHover);
        SetSolid(resources, "RailSelectedBrush", palette.RailSelected);
        SetSolid(resources, "SummaryCardBrush", palette.SummaryCard);
        SetSolid(resources, "QuotaCardBrush", palette.QuotaCard);
        SetSolid(resources, "CreditCardBrush", palette.CreditCard);
        SetSolid(resources, "ProgressTrackBrush", palette.ProgressTrack);
        SetSolid(resources, "ChromeHoverBrush", palette.ChromeHover);
        SetSolid(resources, "ChromePressedBrush", palette.ChromePressed);
        SetSolid(resources, "ChromeActiveBrush", palette.ChromeActive);
        SetSolid(resources, "DangerHoverBrush", palette.DangerHover);
        SetSolid(resources, "OnAccentBrush", palette.OnAccent);
        SetSolid(resources, "TooltipBrush", palette.Tooltip);
        SetSolid(resources, "TooltipBorderBrush", palette.TooltipBorder);
        SetSolid(resources, "TooltipTextBrush", palette.TooltipText);
        SetGradient(resources, "GlassBrush", palette.TransparentSurface);
        SetGradient(resources, "OpaqueGlassBrush", palette.OpaqueSurface);
    }

    /// <summary>依据系统是否接受 DWM 材质选择透明或实色表面，保证旧系统上不会因为透明背景丢失文字对比。</summary>
    internal static string GetSurfaceResourceKey(bool backdropAvailable)
    {
        return backdropAvailable ? "GlassBrush" : "OpaqueGlassBrush";
    }

    /// <summary>返回标题栏是否应使用深色系统按钮提示，以便日间主题获得正确的系统级对比度。</summary>
    internal static bool UsesDarkCaption(WidgetTheme theme)
    {
        return theme != WidgetTheme.Day;
    }

    /// <summary>在 Windows 高对比度模式下改用系统语义色并关闭透明层次，避免自定义玻璃色降低辅助技术和系统配色的可读性。</summary>
    private static Palette CreateHighContrastPalette()
    {
        var window = System.Windows.SystemColors.WindowColor.ToString();
        var text = System.Windows.SystemColors.WindowTextColor.ToString();
        var muted = System.Windows.SystemColors.GrayTextColor.ToString();
        var accent = System.Windows.SystemColors.HighlightColor.ToString();
        var onAccent = System.Windows.SystemColors.HighlightTextColor.ToString();
        var control = System.Windows.SystemColors.ControlColor.ToString();
        var border = System.Windows.SystemColors.WindowFrameColor.ToString();
        return new Palette
        {
            Line = border, PrimaryText = text, SecondaryText = text, MutedText = muted,
            Accent = accent, AccentHover = accent, AccentSoft = control, Control = control,
            ControlHover = control, ControlPressed = control, ControlBorder = border, ControlDisabled = control,
            Panel = window, PanelBorder = border, Surface = window, Card = window, CardBorder = border,
            Weekly = accent, WeeklySoft = window, Success = text, QuotaGreen = text, QuotaPurple = text, QuotaYellow = text, QuotaRed = text, Rail = window, RailHover = control,
            RailSelected = accent, SummaryCard = window, QuotaCard = window, CreditCard = window,
            ProgressTrack = control, ChromeHover = control, ChromePressed = control, ChromeActive = accent,
            DangerHover = accent, OnAccent = onAccent, Tooltip = window, TooltipBorder = border, TooltipText = text,
            TransparentSurface = [window, window, window], OpaqueSurface = [window, window, window]
        };
    }

    /// <summary>替换指定的现有纯色画刷；缺失资源是应用资源配置错误，应立即暴露而不是悄悄跳过。</summary>
    private static void SetSolid(ResourceDictionary resources, string key, string color)
    {
        if (resources[key] is not SolidColorBrush)
        {
            throw new InvalidOperationException($"Theme resource '{key}' must be a SolidColorBrush.");
        }

        resources[key] = new SolidColorBrush(ParseColor(color));
    }

    /// <summary>克隆并替换指定渐变的每个色标；主题资源声明固定为三段以保持窗口材质的空间层次一致。</summary>
    private static void SetGradient(ResourceDictionary resources, string key, IReadOnlyList<string> colors)
    {
        if (resources[key] is not LinearGradientBrush brush || brush.GradientStops.Count != colors.Count)
        {
            throw new InvalidOperationException($"Theme resource '{key}' must be a three-stop LinearGradientBrush.");
        }

        var updated = brush.Clone();
        for (var index = 0; index < colors.Count; index++)
        {
            updated.GradientStops[index].Color = ParseColor(colors[index]);
        }
        resources[key] = updated;
    }

    /// <summary>集中解析 XAML 同样支持的十六进制颜色文本，避免主题表中混入未检查的画刷实例。</summary>
    private static MediaColor ParseColor(string value)
    {
        return System.Windows.Media.ColorConverter.ConvertFromString(value) is MediaColor color
            ? color
            : throw new InvalidOperationException($"Invalid theme color '{value}'.");
    }

    /// <summary>保存一个主题所需的语义色及两组窗口渐变，避免把原始颜色散落进窗口逻辑。</summary>
    private sealed class Palette
    {
        internal required string Line { get; init; }
        internal required string PrimaryText { get; init; }
        internal required string SecondaryText { get; init; }
        internal required string MutedText { get; init; }
        internal required string Accent { get; init; }
        internal required string AccentHover { get; init; }
        internal required string AccentSoft { get; init; }
        internal required string Control { get; init; }
        internal required string ControlHover { get; init; }
        internal required string ControlPressed { get; init; }
        internal required string ControlBorder { get; init; }
        internal required string ControlDisabled { get; init; }
        internal required string Panel { get; init; }
        internal required string PanelBorder { get; init; }
        internal required string Surface { get; init; }
        internal required string Card { get; init; }
        internal required string CardBorder { get; init; }
        internal required string Weekly { get; init; }
        internal required string WeeklySoft { get; init; }
        internal required string Success { get; init; }
        internal required string QuotaGreen { get; init; }
        internal required string QuotaPurple { get; init; }
        internal required string QuotaYellow { get; init; }
        internal required string QuotaRed { get; init; }
        internal required string Rail { get; init; }
        internal required string RailHover { get; init; }
        internal required string RailSelected { get; init; }
        internal required string SummaryCard { get; init; }
        internal required string QuotaCard { get; init; }
        internal required string CreditCard { get; init; }
        internal required string ProgressTrack { get; init; }
        internal required string ChromeHover { get; init; }
        internal required string ChromePressed { get; init; }
        internal required string ChromeActive { get; init; }
        internal required string DangerHover { get; init; }
        internal required string OnAccent { get; init; }
        internal required string Tooltip { get; init; }
        internal required string TooltipBorder { get; init; }
        internal required string TooltipText { get; init; }
        internal required IReadOnlyList<string> TransparentSurface { get; init; }
        internal required IReadOnlyList<string> OpaqueSurface { get; init; }
    }
}
