using System.Windows;
using System.Windows.Media;

namespace Dlss5AmdSwapper.Services;

/// <summary>Semantic colours shared by every window, popup and the offscreen theme checks.</summary>
public static class ThemeService
{
    public static void Apply(ResourceDictionary resources, bool light)
    {
        var brushes = new Dictionary<string, (string Dark, string Light)>
        {
            ["BgBrush"] = ("#090B10", "#F1F3F7"),
            ["PanelBrush"] = ("#10141C", "#FFFFFF"),
            ["CardBrush"] = ("#151A24", "#FFFFFF"),
            ["CardHoverBrush"] = ("#1B2230", "#E5E9F1"),
            ["LineBrush"] = ("#252D3D", "#CDD3DF"),
            ["ControlBorderBrush"] = ("#778295", "#68758A"),
            ["TextBrush"] = ("#F7F8FA", "#18212F"),
            ["MutedBrush"] = ("#929BAD", "#526178"),
            ["AccentBrush"] = ("#FF5847", "#B92F22"),
            ["Accent2Brush"] = ("#FF8A4B", "#994000"),
            ["AccentForegroundBrush"] = ("#18212F", "#FFFFFF"),
            ["PrimaryTextBrush"] = ("#18212F", "#FFFFFF"),
            ["SuccessBrush"] = ("#5EE29A", "#16723F"),
            ["WarningBrush"] = ("#FFC45C", "#865100"),
            ["DangerBrush"] = ("#FF6472", "#B62D40")
        };
        foreach (var (key, colors) in brushes)
        {
            var brush = new SolidColorBrush(Parse(light ? colors.Light : colors.Dark));
            brush.Freeze();
            resources[key] = brush;
        }

        resources["HeroStartColor"] = Parse(light ? "#FFF0EA" : "#291719");
        resources["HeroMiddleColor"] = Parse(light ? "#FFFFFF" : "#141821");
        resources["HeroEndColor"] = Parse(light ? "#F4F6FA" : "#10151D");
        resources["PrimaryStartColor"] = Parse(light ? "#B02B22" : "#FF5545");
        resources["PrimaryEndColor"] = Parse(light ? "#A43B10" : "#FF7B45");
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}
