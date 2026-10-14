using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace RVNEX.Services;

internal static class ThemePalette
{
    internal static bool ResolveIsDark(string preference, UISettings systemSettings) => preference switch
    {
        "Dark" => true,
        "Light" => false,
        _ => IsSystemDark(systemSettings)
    };

    internal static bool IsSystemDark(UISettings systemSettings)
    {
        Color background = systemSettings.GetColorValue(UIColorType.Background);
        return (background.R + background.G + background.B) / 3 < 128;
    }

    internal static void Apply(ResourceDictionary resources, bool isDark, UISettings systemSettings, bool highContrast)
    {
        IReadOnlyDictionary<string, Color> colors = highContrast
            ? HighContrastColors(systemSettings)
            : isDark ? DarkColors : LightColors;

        foreach ((string key, Color color) in colors)
        {
            if (resources.TryGetValue(key, out object? value) && value is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }

    private static IReadOnlyDictionary<string, Color> DarkColors { get; } = CreatePalette(
        background: "#171D21", surface: "#20282D", surfaceAlt: "#263238", surfaceRaised: "#2D3A40",
        border: "#3B4A50", text: "#EAF0F2", secondary: "#B2C0C4", tertiary: "#91A2A7",
        accent: "#3CC8BE", accentHover: "#64D8CF", accentPressed: "#1BA59D", accentText: "#102322",
        buttonFill: "#263238", buttonHover: "#34434A", buttonPressed: "#3D5056", buttonText: "#EAF0F2",
        focus: "#64D8CF", selection: "#173A3B", success: "#6BD29A", warning: "#FFD166", error: "#FF9298",
        dangerFill: "#A9343E", dangerText: "#FFFFFF");

    private static IReadOnlyDictionary<string, Color> LightColors { get; } = CreatePalette(
        background: "#F3F6F7", surface: "#FFFFFF", surfaceAlt: "#E9EFF1", surfaceRaised: "#F7FAFA",
        border: "#D3DEE1", text: "#17252B", secondary: "#485B61", tertiary: "#61747A",
        accent: "#007873", accentHover: "#006762", accentPressed: "#005650", accentText: "#FFFFFF",
        buttonFill: "#E9EFF1", buttonHover: "#DCE6E8", buttonPressed: "#CFDDE0", buttonText: "#17252B",
        focus: "#006762", selection: "#D9EFED", success: "#176A43", warning: "#805A00", error: "#B4232F",
        dangerFill: "#B4232F", dangerText: "#FFFFFF");

    private static IReadOnlyDictionary<string, Color> CreatePalette(
        string background, string surface, string surfaceAlt, string surfaceRaised,
        string border, string text, string secondary, string tertiary,
        string accent, string accentHover, string accentPressed, string accentText,
        string buttonFill, string buttonHover, string buttonPressed, string buttonText,
        string focus, string selection, string success, string warning, string error,
        string dangerFill, string dangerText) => new Dictionary<string, Color>(StringComparer.Ordinal)
    {
        ["RvnexBackgroundBrush"] = FromHex(background),
        ["RvnexSurfaceBrush"] = FromHex(surface),
        ["RvnexSurfaceAltBrush"] = FromHex(surfaceAlt),
        ["RvnexSurfaceRaisedBrush"] = FromHex(surfaceRaised),
        ["RvnexBorderBrush"] = FromHex(border),
        ["RvnexTextPrimaryBrush"] = FromHex(text),
        ["RvnexTextSecondaryBrush"] = FromHex(secondary),
        ["RvnexTextTertiaryBrush"] = FromHex(tertiary),
        ["RvnexAccentBrush"] = FromHex(accent),
        ["RvnexAccentHoverBrush"] = FromHex(accentHover),
        ["RvnexAccentPressedBrush"] = FromHex(accentPressed),
        ["RvnexAccentTextBrush"] = FromHex(accentText),
        ["RvnexButtonFillBrush"] = FromHex(buttonFill),
        ["RvnexButtonHoverBrush"] = FromHex(buttonHover),
        ["RvnexButtonPressedBrush"] = FromHex(buttonPressed),
        ["RvnexButtonTextBrush"] = FromHex(buttonText),
        ["RvnexFocusBrush"] = FromHex(focus),
        ["RvnexSelectionBrush"] = FromHex(selection),
        ["RvnexNavigationIdleBrush"] = Color.FromArgb(0, 0, 0, 0),
        ["RvnexSuccessBrush"] = FromHex(success),
        ["RvnexWarningBrush"] = FromHex(warning),
        ["RvnexErrorBrush"] = FromHex(error),
        ["RvnexDangerFillBrush"] = FromHex(dangerFill),
        ["RvnexDangerTextBrush"] = FromHex(dangerText)
    };

    private static IReadOnlyDictionary<string, Color> HighContrastColors(UISettings settings)
    {
        Color background = settings.UIElementColor(UIElementType.Window);
        Color text = settings.UIElementColor(UIElementType.WindowText);
        Color button = settings.UIElementColor(UIElementType.ButtonFace);
        Color buttonText = settings.UIElementColor(UIElementType.ButtonText);
        Color highlight = settings.UIElementColor(UIElementType.Highlight);
        Color highlightText = settings.UIElementColor(UIElementType.HighlightText);
        Color focus = settings.UIElementColor(UIElementType.Hotlight);
        Color border = settings.UIElementColor(UIElementType.WindowText);

        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["RvnexBackgroundBrush"] = settings.UIElementColor(UIElementType.PageBackground),
            ["RvnexSurfaceBrush"] = background,
            ["RvnexSurfaceAltBrush"] = button,
            ["RvnexSurfaceRaisedBrush"] = settings.UIElementColor(UIElementType.PopupBackground),
            ["RvnexBorderBrush"] = border,
            ["RvnexTextPrimaryBrush"] = text,
            ["RvnexTextSecondaryBrush"] = text,
            ["RvnexTextTertiaryBrush"] = text,
            ["RvnexAccentBrush"] = highlight,
            ["RvnexAccentHoverBrush"] = highlight,
            ["RvnexAccentPressedBrush"] = highlight,
            ["RvnexAccentTextBrush"] = highlightText,
            ["RvnexButtonFillBrush"] = button,
            ["RvnexButtonHoverBrush"] = highlight,
            ["RvnexButtonPressedBrush"] = highlight,
            ["RvnexButtonTextBrush"] = buttonText,
            ["RvnexFocusBrush"] = focus,
            ["RvnexSelectionBrush"] = highlight,
            ["RvnexNavigationIdleBrush"] = Color.FromArgb(0, 0, 0, 0),
            ["RvnexSuccessBrush"] = text,
            ["RvnexWarningBrush"] = text,
            ["RvnexErrorBrush"] = text,
            ["RvnexDangerFillBrush"] = button,
            ["RvnexDangerTextBrush"] = buttonText
        };
    }

    private static Color FromHex(string value) => Color.FromArgb(
        255,
        Convert.ToByte(value.Substring(1, 2), 16),
        Convert.ToByte(value.Substring(3, 2), 16),
        Convert.ToByte(value.Substring(5, 2), 16));
}
