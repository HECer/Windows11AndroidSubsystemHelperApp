using System.Windows;
using System.Windows.Media;

namespace WsaHelper.Desktop;

internal static class DesktopTheme
{
    internal static void Apply(FrameworkElement owner, bool dark)
    {
        var colors = dark
            ? new[] { "#141D19", "#1C2821", "#17231C", "#EFF5F0", "#ACBDB0", "#34463A", "#97D7AC", "#102A1A", "#2A4231", "#29392E", "#EEAAAA", "#432B2B", "#E5C27E", "#3B3322" }
            : new[] { "#F6F8F6", "#FFFFFF", "#EDF2EE", "#192D24", "#5C6C63", "#DCE5DE", "#176747", "#FFFFFF", "#E2EEE5", "#EAF0EC", "#AA3434", "#FBEEEE", "#855A16", "#F9F0DE" };
        string[] keys = ["Canvas", "Surface", "Sidebar", "Ink", "Muted", "Line", "Accent", "OnAccent", "Tint", "Hover", "Danger", "DangerTint", "Warning", "WarningTint"];
        for (var i = 0; i < keys.Length; i++) owner.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if (!SystemParameters.HighContrast) return;
        foreach (var key in new[] { "Canvas", "Surface", "Sidebar", "Tint", "Hover", "DangerTint", "WarningTint" }) owner.Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "Ink", "Muted", "Line", "Danger", "Warning", "Accent" }) owner.Resources[key] = SystemColors.WindowTextBrush;
        owner.Resources["OnAccent"] = SystemColors.WindowBrush;
    }
}
