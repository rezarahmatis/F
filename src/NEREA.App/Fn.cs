using Microsoft.UI.Xaml;

namespace NEREA.App;

/// <summary>Static helpers for function bindings in XAML ({x:Bind local:Fn.Vis(...)}).</summary>
public static class Fn
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisInv(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisIf(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    public static double Op(bool value) => value ? 1.0 : 0.0;
    public static string PlayGlyph(bool playing) => playing ? "\uE769" : "\uE768";
    public static string MuteGlyph(bool muted, double volume) => muted || volume <= 0.001 ? "\uE74F" : "\uE767";
    public static string ShuffleOpacityText(bool on) => on ? "1" : "0.45";
    public static double Dim(bool on) => on ? 1.0 : 0.45;
    public static string StarGlyph(bool favorite) => favorite ? "\uE735" : "\uE734";
}
