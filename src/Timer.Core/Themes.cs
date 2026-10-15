using System.Globalization;
using System.Text.Json;

namespace Timer.Core;

/// <summary>
/// A theme is a name, whether it sits on a light or a dark base (so the standard controls know what to expect), and a colour for each
/// named part of the app. Everything else about how a theme looks, including the pill's break and overtime tints, is worked out from these.
/// </summary>
public sealed record ThemeDefinition(string Name, bool Dark, IReadOnlyDictionary<string, string> Colours, bool BuiltIn = false, string BlockStyle = ThemeBlockStyle.AsChosen,
    double BlockTint = ThemeBlockStyle.DefaultTint, double BlockEdge = ThemeBlockStyle.DefaultEdge)
{
    public string Colour(string token) => Colours.TryGetValue(token, out var value) ? value : throw new KeyNotFoundException("The theme has no colour for " + token);
}

/// <summary>How account colours are drawn on the timeline and week bars.</summary>
public static class ThemeBlockStyle
{
    /// <summary>The account's own colour, exactly as chosen (very dark colours are still lifted so they show).</summary>
    public const string AsChosen = "asChosen";
    /// <summary>The account's colour softened towards the theme's surface, so blocks sit calmly in it.</summary>
    public const string Softened = "softened";
    /// <summary>
    /// A calm, tinted block: the theme's surface with a little of the account's colour in it, and a stronger edge down the left side in the account's own colour.
    /// <see cref="ThemeDefinition.BlockTint"/> is how much account colour the fill takes, and <see cref="ThemeDefinition.BlockEdge"/> how far the edge is pushed
    /// towards the theme's text colour (0 keeps the account's colour exactly).
    /// </summary>
    public const string Tinted = "tinted";
    public const double DefaultTint = .3, DefaultEdge = .2;
    public static bool IsKnown(string? style) => style is AsChosen or Softened or Tinted;
}

public static class ThemeTokens
{
    /// <summary>The coloured parts of the app a theme sets, in the order the editor lists them.</summary>
    public static readonly string[] Names =
    {
        "Surface", "Panel", "Ink", "Muted", "Line", "Hover", "Selected", "Live", "Idle", "Overtime", "BudgetWarning", "Break", "Finish", "Start", "Accent", "AccentInk", "Clock", "PillSurface", "PillInk", "PillBreak", "PillOvertime", "ShadowColor"
    };
    public static string Describe(string token) => token switch
    {
        "Surface" => "Window background", "Panel" => "Side panel and controls", "Ink" => "Text", "Muted" => "Quiet text", "Line" => "Lines and borders",
        "Hover" => "Hovered row", "Selected" => "Selected row", "Live" => "Tracking", "Idle" => "Not tracking", "Overtime" => "Overtime",
        "BudgetWarning" => "Budget nearly used", "Break" => "Breaks", "Finish" => "Finish day", "Start" => "Start day", "Accent" => "Accent (tabs, main button)", "AccentInk" => "Text on the accent", "Clock" => "Clock when idle", "PillSurface" => "Pill while recording", "PillInk" => "Text on the pill while recording", "PillBreak" => "Pill on a break", "PillOvertime" => "Pill in overtime", "ShadowColor" => "Window shadow", _ => token
    };
    /// <summary>#RRGGBB for every colour, or #AARRGGBB for the shadow, which needs transparency.</summary>
    public static bool IsValid(string token, string? value)
    {
        if (value is null || !value.StartsWith('#')) return false;
        var digits = value[1..];
        var length = token == "ShadowColor" ? 8 : 6;
        return digits.Length == length && digits.All(Uri.IsHexDigit);
    }
}

public static class ThemeColours
{
    public static (int R, int G, int B) Rgb(string hex)
    {
        var d = hex.Length == 9 ? hex[3..] : hex[1..];
        return (Convert.ToInt32(d[..2], 16), Convert.ToInt32(d.Substring(2, 2), 16), Convert.ToInt32(d.Substring(4, 2), 16));
    }
    public static string Hex(int r, int g, int b) => "#" + Math.Clamp(r, 0, 255).ToString("X2") + Math.Clamp(g, 0, 255).ToString("X2") + Math.Clamp(b, 0, 255).ToString("X2");
    /// <summary>Blends two colours; <paramref name="amount"/> is how much of the second to take, from 0 to 1.</summary>
    public static string Mix(string from, string to, double amount)
    {
        var a = Rgb(from); var b = Rgb(to);
        return Hex((int)Math.Round(a.R + (b.R - a.R) * amount), (int)Math.Round(a.G + (b.G - a.G) * amount), (int)Math.Round(a.B + (b.B - a.B) * amount));
    }
    public static (double H, double S, double L) Hsl(string hex)
    {
        var (r, g, b) = Rgb(hex); double R = r / 255.0, G = g / 255.0, B = b / 255.0;
        var max = Math.Max(R, Math.Max(G, B)); var min = Math.Min(R, Math.Min(G, B)); var l = (max + min) / 2; double h = 0, s = 0;
        if (max != min)
        {
            var d = max - min; s = l > .5 ? d / (2 - max - min) : d / (max + min);
            h = max == R ? (G - B) / d + (G < B ? 6 : 0) : max == G ? (B - R) / d + 2 : (R - G) / d + 4; h *= 60;
        }
        return (h, s, l);
    }
    public static string FromHsl(double h, double s, double l)
    {
        double F(double n) { var k = (n + h / 30) % 12; var a = s * Math.Min(l, 1 - l); return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)); }
        return Hex((int)Math.Round(F(0) * 255), (int)Math.Round(F(8) * 255), (int)Math.Round(F(4) * 255));
    }
    public static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        double C(int v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(r) + 0.7152 * C(g) + 0.0722 * C(b);
    }
    /// <summary>The WCAG contrast ratio between two colours, from 1 (none) to 21. Text wants about 4.5 or more.</summary>
    /// <summary>Dark text or white text, whichever reads better on this colour.</summary>
    public static string ReadableOn(string hex)
    {
        double Channel(int at) { var v = Convert.ToInt32(hex.Substring(at, 2), 16) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5) > 0.3 ? "#24312B" : "#FFFFFF";
    }
    public static double Contrast(string a, string b)
    {
        var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}

public static class ThemeDerivation
{
    public const string Break = "Break", Overtime = "Overtime";
    public const string Recording = "Recording";
    /// <summary>
    /// The colours the pill wears in a state, so everything inside it reads naturally on its colour. Recording uses the theme's pill colour, a break its Break
    /// colour and overtime its Overtime colour. Text, lines and hovers are mixed from the colour and the text on it, and the colours that normally mean
    /// something (break, overtime) become plain text colour, because the whole pill already says it. Finish keeps its own red, since the
    /// Finish day button must stay red whatever the pill is doing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variant(ThemeDefinition theme, string state)
    {
        var surface = state switch { Break => theme.Colour("PillBreak"), Overtime => theme.Colour("PillOvertime"), _ => theme.Colour("PillSurface") };
        var ink = state == Recording ? theme.Colour("PillInk") : ThemeColours.ReadableOn(surface);
        var result = new Dictionary<string, string>(theme.Colours)
        {
            ["Surface"] = surface, ["Panel"] = ThemeColours.Mix(surface, ink, .10), ["Ink"] = ink, ["Muted"] = ThemeColours.Mix(surface, ink, .84),
            ["Line"] = ThemeColours.Mix(surface, ink, .30), ["Hover"] = ThemeColours.Mix(surface, ink, .16), ["Selected"] = ThemeColours.Mix(surface, ink, .22),
            ["Idle"] = ThemeColours.Mix(surface, ink, .84), ["Clock"] = ink, ["Overtime"] = ink, ["Break"] = ink,
            ["Live"] = state == Recording ? Lift(theme.Colour("Live"), surface, 9) : ink
        };
        return result;
    }
    /// <summary>A colour made lighter until it reads on a dark surface at the contrast asked for, so green tracking text holds up on the recording pill.</summary>
    private static string Lift(string colour, string surface, double contrast)
    {
        for (var step = 0; step < 8 && ThemeColours.Contrast(colour, surface) < contrast; step++) colour = ThemeColours.Mix(colour, "#FFFFFF", .2);
        return colour;
    }
    /// <summary>What a timeline block is drawn with: its fill, the colour of the edge down its left side, and the text on it.</summary>
    public readonly record struct BlockLook(string Fill, string Edge, string Text);
    /// <summary>Turns an account colour (already lifted if it was very dark) into a block for this theme, by the theme's own block rules.</summary>
    public static BlockLook Block(ThemeDefinition theme, string accountColour)
    {
        var ink = theme.Colour("Ink");
        switch (theme.BlockStyle)
        {
            case ThemeBlockStyle.Tinted:
            {
                var fill = ThemeColours.Mix(theme.Colour("Surface"), accountColour, Math.Clamp(theme.BlockTint, 0, 1));
                return new(fill, ThemeColours.Mix(accountColour, ink, Math.Clamp(theme.BlockEdge, 0, 1)), ink);
            }
            case ThemeBlockStyle.Softened:
            {
                var fill = Soften(theme, accountColour);
                return new(fill, ThemeColours.Mix(fill, ink, .18), ThemeColours.ReadableOn(fill));
            }
            default:
                return new(accountColour, ThemeColours.Mix(accountColour, ink, .18), ThemeColours.ReadableOn(accountColour));
        }
    }
    /// <summary>A block colour softened towards the theme's surface, for themes that ask for it. Dark colours are lifted first so they show.</summary>
    public static string Soften(ThemeDefinition theme, string colour)
        => theme.BlockStyle == ThemeBlockStyle.Softened ? ThemeColours.Mix(colour, theme.Colour("Surface"), theme.Dark ? .38 : .22) : colour;
}

public static class ThemeLibrary
{
    private static ThemeDefinition Make(string name, bool dark, string surface, string panel, string ink, string muted, string line, string hover, string selected,
        string live, string overtime, string warning, string breakColour, string finish, string accent, string accentInk, string clock, string shadow, string start = "#2E9E5B")
        => new(name, dark, new Dictionary<string, string>
        {
            ["Surface"] = surface, ["Panel"] = panel, ["Ink"] = ink, ["Muted"] = muted, ["Line"] = line, ["Hover"] = hover, ["Selected"] = selected, ["Live"] = live,
            ["Idle"] = muted, ["Overtime"] = overtime, ["BudgetWarning"] = warning, ["Break"] = breakColour, ["Finish"] = finish, ["Start"] = start, ["Accent"] = accent, ["AccentInk"] = accentInk,
            ["Clock"] = clock, ["ShadowColor"] = shadow,
            // The recording pill is the strongest object on screen. On a light theme it is dark, on a dark theme a lifted version of the panel, so it stands out either way.
            ["PillSurface"] = dark ? ThemeColours.Mix(panel, "#FFFFFF", .08) : ink, ["PillInk"] = dark ? ink : surface,
            // Overtime wants a deep, strong red under white text. A dark theme's overtime colour is a light one made for text, so it is deepened for the pill.
            ["PillBreak"] = Capsule(breakColour), ["PillOvertime"] = dark ? ThemeColours.FromHsl(ThemeColours.Hsl(overtime).H, .62, .36) : Capsule(overtime)
        }, true);
    // A colour that is fine for text on the theme's window can be too dull to carry text itself as a capsule, so it is brightened until text reads on it.
    private static string Capsule(string colour)
    {
        if (Math.Max(ThemeColours.Contrast(colour, "#FFFFFF"), ThemeColours.Contrast(colour, "#24312B")) >= 4.5) return colour;
        var (hue, saturation, _) = ThemeColours.Hsl(colour);
        return ThemeColours.FromHsl(hue, Math.Max(saturation, .8), .55);
    }
    private const string DarkShadow = "#B3000000", LightShadow = "#59000000";
    /// <summary>Light: cool and precise, a pale blue-grey surface with a strong blue accent (the palette that was the Studio study).</summary>
    public static readonly ThemeDefinition Light = Themed("Light", false, "#F3F6FA", "#E5EBF2", "#202C3D", "#4169C1", "#1F8F4E", "#E07A10", "#B92D43", .55);
    /// <summary>Dark: cool charcoal surfaces with a soft blue accent, apart from the green, orange and red that carry meaning. Blocks take a little more of the account colour than in the light version, so they hold against the dark.</summary>
    public static readonly ThemeDefinition Dark = Make("Dark", true, "#14161A", "#1C1F24", "#EEF0F3", "#9CA3AD", "#2E333A", "#262A31", "#1D2A44",
        "#4CD48A", "#FF7A8A", "#F2B84B", "#FF9A3C", "#FF6B6B", "#6EA8FF", "#0B1426", "#EEF0F3", DarkShadow, "#4CD48A") with { BlockStyle = ThemeBlockStyle.Tinted, BlockTint = .45, BlockEdge = 0 };
    // The colours a mock-up gives (surface, panel, text, accent, break and overtime) are the ones set by hand. The rest are worked out from them, so
    // every theme gets quiet text, lines, hovers and a selection tint that suit its own surface, and holds the same contrast the tests ask of all.
    private static ThemeDefinition Themed(string name, bool dark, string surface, string panel, string ink, string accent, string live, string breakColour, string overtime, double tint)
    {
        var muted = surface;
        for (var weight = .5; weight <= 1.0; weight += .05) { muted = ThemeColours.Mix(surface, ink, weight); if (ThemeColours.Contrast(muted, surface) >= 4.5) break; }
        // On a dark surface the overtime red is lightened until it reads as text, while the pill derives its deep red from the original.
        var overtimeText = overtime;
        for (var step = 0; dark && step < 6 && ThemeColours.Contrast(overtimeText, surface) < 4.5; step++) overtimeText = ThemeColours.Mix(overtimeText, "#FFFFFF", .2);
        var line = ThemeColours.Mix(panel, ink, .14); var hover = ThemeColours.Mix(panel, ink, .07); var selected = ThemeColours.Mix(surface, accent, dark ? .30 : .18);
        return Make(name, dark, surface, panel, ink, muted, line, hover, selected, live, overtimeText, dark ? "#F2B84B" : "#D9890B", breakColour, dark ? "#FF7A8A" : "#C0243F",
            accent, ThemeColours.ReadableOn(accent), ink, dark ? DarkShadow : LightShadow, live) with { BlockStyle = ThemeBlockStyle.Tinted, BlockTint = tint, BlockEdge = 0 };
    }
    /// <summary>After Hours: deep and electric, a navy surface with a soft violet accent.</summary>
    public static readonly ThemeDefinition AfterHours = Themed("After Hours", true, "#111827", "#1C2638", "#EDF2FA", "#A5A0FF", "#4CD48A", "#F28A24", "#B92D43", .42);
    /// <summary>Botanical: soft and grounded, a pale sage surface with a deep green accent.</summary>
    public static readonly ThemeDefinition Botanical = Themed("Botanical", false, "#F0F3EB", "#E2E9DA", "#24382D", "#38654B", "#2E7D4F", "#E07A10", "#B92D43", .55);
    /// <summary>Hawthorn: forest and luminous, deep green with a lime accent. It is the old default theme, back as a choice.</summary>
    public static readonly ThemeDefinition Hawthorn = Themed("Hawthorn", true, "#073D29", "#105237", "#F5F8ED", "#C5FF42", "#6FE39A", "#F28A24", "#B92D43", .38);
    // Light and Dark lead the list, then the studies. More can be added here later, and the editor and settings page list whatever is in this array.
    public static readonly IReadOnlyList<ThemeDefinition> BuiltIn = new[] { Light, Dark, AfterHours, Botanical, Hawthorn };
    /// <summary>"System" is not a theme of its own: it picks Light or Dark to match the computer.</summary>
    public const string System = "System";
    /// <summary>What a new installation starts on.</summary>
    public const string Default = System;
    /// <summary>A built-in theme that was renamed or removed keeps working under its old name in saved settings, by landing on the light or dark theme it was closest to.</summary>
    public static string Renamed(string name) => name switch
    {
        "Studio" or "Time Deck" or "Paper" or "Sandstone" or "Sunset" or "High contrast light" => "Light",
        "Time Deck Dark" or "Sage" or "Graphite" or "Midnight" or "Ocean" or "Aubergine" or "High contrast dark" => "Dark",
        _ => name
    };
}

/// <summary>Reads and writes a user's own themes as one JSON line each, which is how the settings file keeps lists.</summary>
public static class ThemeStorage
{
    private sealed record Saved(string Name, bool Dark, Dictionary<string, string> Colours, string? BlockStyle, double? BlockTint = null, double? BlockEdge = null);
    public static string ToLine(ThemeDefinition theme)
        => JsonSerializer.Serialize(new Saved(theme.Name, theme.Dark, new Dictionary<string, string>(theme.Colours), theme.BlockStyle, theme.BlockTint, theme.BlockEdge));
    /// <summary>Returns the themes in the lines that are sound. A damaged line is skipped, so one bad theme never costs the others.</summary>
    public static List<ThemeDefinition> FromLines(IEnumerable<string> lines)
    {
        var result = new List<ThemeDefinition>();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(line);
                if (saved is null || !IsUsableName(saved.Name)) continue;
                // Anything missing is taken from the built-in theme on the same base, so an old or hand-edited theme still works.
                var basis = saved.Dark ? ThemeLibrary.Dark : ThemeLibrary.Light;
                var colours = new Dictionary<string, string>();
                foreach (var token in ThemeTokens.Names)
                    colours[token] = saved.Colours is not null && saved.Colours.TryGetValue(token, out var value) && ThemeTokens.IsValid(token, value) ? value.ToUpperInvariant() : basis.Colour(token);
                if (result.Any(t => t.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)) || ThemeLibrary.BuiltIn.Any(t => t.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new ThemeDefinition(saved.Name.Trim(), saved.Dark, colours, false, ThemeBlockStyle.IsKnown(saved.BlockStyle) ? saved.BlockStyle! : ThemeBlockStyle.AsChosen,
                    Math.Clamp(saved.BlockTint ?? ThemeBlockStyle.DefaultTint, 0, 1), Math.Clamp(saved.BlockEdge ?? ThemeBlockStyle.DefaultEdge, 0, 1)));
            }
            catch (JsonException) { }
        }
        return result;
    }
    public static bool IsUsableName(string? name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name.Length <= 40
        && name.IndexOfAny(new[] { '\t', '\r', '\n' }) < 0 && !name.Equals(ThemeLibrary.System, StringComparison.OrdinalIgnoreCase);
}
