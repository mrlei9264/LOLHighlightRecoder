using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LolHighlight.Desktop.Controls;

/// <summary>Native, compositor-animated artwork shared by previews and game notifications.</summary>
internal sealed class EventNotificationView : UserControl
{
    public const int LogicalWidth = 428;
    public const int LogicalHeight = 148;
    private readonly string _style;
    private readonly Canvas _surface;
    private readonly Border _progress;
    private readonly List<(FrameworkElement Element, string Effect)> _effects = new();
    private readonly List<Visual> _animated = new();
    private bool _animate = new UISettings().AnimationsEnabled;

    public EventNotificationView(string style, string title, string detail)
    {
        _style = style;
        RequestedTheme = ElementTheme.Dark;
        Background = new SolidColorBrush(Colors.Transparent);
        IsHitTestVisible = false;
        var stage = new Canvas { Width = LogicalWidth, Height = LogicalHeight, Background = new SolidColorBrush(Colors.Transparent) };
        var compact = style == "pulse";
        _surface = new Canvas { Width = compact ? 360 : 400, Height = compact ? 88 : 116 };
        Place(stage, _surface, compact ? 54 : 14, compact ? 28 : 14);
        Content = new Viewbox { Child = stage, Stretch = Stretch.Uniform };

        var accent = style switch
        {
            "neon" => 0x59ECEE,
            "ink" => 0xE35455,
            "crystal" => 0xC49AF5,
            "blade" => 0xB6DFF5,
            "comic" => 0xB83242,
            "forge" => 0xF0A459,
            "aurora" => 0xC4DDF4,
            _ => 0xA4EA95
        };
        switch (style)
        {
            case "neon": BuildNeon(); break;
            case "ink": BuildInk(); break;
            case "crystal": BuildCrystal(); break;
            case "blade": BuildBlade(); break;
            case "comic": BuildComic(); break;
            case "forge": BuildForge(); break;
            case "aurora": BuildAurora(); break;
            default: BuildPulse(); break;
        }

        var textX = compact ? 76 : style == "ink" ? 66 : style == "comic" ? 27 : 87;
        var textY = compact ? 17 : style == "blade" ? 22 : 18;
        var copy = new StackPanel { Spacing = compact ? 5 : 4, Width = compact ? 258 : style == "comic" ? 315 : 275 };
        if (!compact)
            copy.Children.Add(Text(style switch
            {
                "neon" => "HIGHLIGHT DETECTED",
                "ink" => "HIGHLIGHT / RIFT REPLAY",
                "crystal" => "R I F T · F R A C T U R E",
                "comic" => "B O O M ! · HIGHLIGHT",
                "forge" => "FORGED IN THE RIFT",
                "aurora" => "A MOMENT TO REMEMBER",
                _ => "HIGHLIGHT CAPTURE"
            }, 10, style == "comic" ? 0x47303B : style == "forge" ? 0xF0B682 : 0xBBCCD9));
        var label = Text(style == "comic" ? title + "!" : title,
            compact ? 24 : style is "ink" or "comic" ? 33 : 28,
            style == "comic" ? 0xB82D3C : style == "ink" ? 0xFFF0E8 : 0xF1F6FC);
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (style == "ink") label.FontFamily = new FontFamily("KaiTi");
        if (style is "ink" or "blade" or "comic") label.FontStyle = Windows.UI.Text.FontStyle.Italic;
        copy.Children.Add(label);
        var subtitle = Text(detail, 11, style == "comic" ? 0x47303B : 0xC4CFDB);
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.MaxLines = 2;
        copy.Children.Add(subtitle);
        Place(_surface, copy, textX, textY);

        _progress = new Border
        {
            Width = compact ? 278 : style == "blade" ? 325 : 346,
            Height = 2, Background = Brush(accent), Opacity = .9
        };
        Place(_surface, _progress, compact ? 41 : 27, compact ? 83 : style == "blade" ? 99 : 110);
        Loaded += OnLoaded;
    }

    private void BuildNeon()
    {
        Place(_surface, Poly(Gradient(0x102735, 0x1C1735),
            0,0, 375,0, 400,23, 400,116, 25,116, 0,93), 0, 0);
        Place(_surface, Poly(Brush(0x59ECEE), 0,0, 4,0, 4,91, 0,87), 0, 0);
        Place(_surface, Poly(Brush(0xC17AF3), 26,114, 400,114, 400,116, 26,116), 0, 0);
        Place(_surface, Text("///", 32, 0x59ECEE), 19, 34);
        var sweep = Poly(Gradient(0xC17AF3, 0x59ECEE, .24), 30,0, 62,0, 32,116, 0,116);
        Place(_surface, sweep, 296, 0);
        _effects.Add((sweep, "sweep"));
    }

    private void BuildInk()
    {
        Place(_surface, Poly(Brush(0x1D191F), 9,0, 396,5, 388,31, 400,49, 393,116, 0,109, 7,78, 0,43), 0, 0);
        var stroke = Poly(Gradient(0xB02B3D, 0xE44C50), 0,17, 325,0, 303,27, 330,34, 5,63);
        Place(_surface, stroke, 0, 26);
        _effects.Add((stroke, "stroke"));
        var seal = new Border { BorderBrush = Brush(0xEC8C7D), BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = Text("斩", 18, 0xFFCCBB) };
        Place(_surface, seal, 21, 41);
        for (var i = 0; i < 4; i++)
        {
            var fleck = Poly(Brush(0xCE3A48), 0,0, 10+i*2,2, 6,5, 1,4);
            Place(_surface, fleck, 332 + i * 12, 25 + i * 17);
            _effects.Add((fleck, "ink"));
        }
    }

    private void BuildCrystal()
    {
        Place(_surface, Poly(Gradient(0x38294F, 0x1B2035), 35,0, 385,5, 400,57, 367,116, 13,110, 0,40), 0, 0);
        var gem = Poly(Gradient(0xBF95ED, 0x493962), 26,0, 51,25, 39,61, 26,73, 0,28);
        Place(_surface, gem, 23, 20);
        _effects.Add((gem, "gem"));
        var face = Poly(Brush(0xE1C2FC), 26,0, 51,25, 26,36);
        face.Opacity = .45;
        Place(_surface, face, 23, 20);
        _effects.Add((face, "gem"));
        for (var i = 0; i < 3; i++)
        {
            var shard = Poly(Gradient(0xB88AEF, 0x4D3869, .5), 0,0, 60-i*10,24, 12,58-i*9);
            Place(_surface, shard, 325 + i * 13, 4 + i * 31);
            _effects.Add((shard, "shard"));
        }
    }

    private void BuildBlade()
    {
        Place(_surface, Poly(Gradient(0x3B5368, 0x121E2B), 29,10, 400,10, 371,104, 0,104), 0, 0);
        Place(_surface, Poly(Brush(0xB6DFF5), 29,10, 400,10, 399,12, 28,12), 0, 0);
        Place(_surface, Poly(Brush(0xD5EAF5), 0,25, 26,0, 23,15, 2,37), 29, 33);
        Place(_surface, Poly(Brush(0x87B7D3), 0,23, 24,0, 21,14, 2,35), 40, 46);
        var streaks = new Canvas { Width = 90, Height = 38 };
        for (var i = 0; i < 4; i++)
            Place(streaks, new Border { Width = 90 - i * 14, Height = 1, Background = Brush(0xA6D9F4), Opacity = .4 }, i * 4, i * 10);
        Place(_surface, streaks, 280, 33);
        _effects.Add((streaks, "speed"));
    }

    private void BuildPulse()
    {
        Place(_surface, new Border
        {
            Width = 360, Height = 88, CornerRadius = new CornerRadius(44),
            Background = Gradient(0x23382A, 0x17251E), BorderBrush = Brush(0x526C5C), BorderThickness = new Thickness(1)
        }, 0, 0);
        var ring = new Ellipse { Width = 48, Height = 48, Stroke = Brush(0xA4EA95), StrokeThickness = 1, Opacity = .5 };
        Place(_surface, ring, 17, 20);
        _effects.Add((ring, "pulse"));
        Place(_surface, new Ellipse { Width = 36, Height = 36, Fill = Brush(0xA4EA95) }, 23, 26);
        Place(_surface, new FontIcon { Glyph = "\uE945", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 19, Foreground = Brush(0x24412B) }, 32, 34);
    }

    private void BuildComic()
    {
        var burst = Poly(Brush(0xEFC04C),
            22,24, 0,0, 96,12, 146,0, 194,12, 284,0, 308,18, 412,6,
            389,45, 412,63, 382,82, 412,121, 305,112, 264,133, 195,117,
            87,130, 92,111, 0,120, 32,80, 0,60);
        Place(_surface, burst, -6, -7);
        Place(_surface, Poly(Brush(0x352339), 0,0, 400,0, 392,104, 349,104, 367,120, 290,106, 0,106), 0, 0);
        Place(_surface, Poly(Brush(0xFFE789), 3,3, 396,3, 389,101, 345,101, 354,110, 290,103, 3,103), 0, 0);
        for (var row = 0; row < 8; row++)
            for (var column = 0; column < 28; column++)
                Place(_surface, new Ellipse { Width = 2, Height = 2, Fill = Brush(0x6E4451), Opacity = .16 },
                    10 + column * 14, 7 + row * 13);
        var star = Text("✦", 34, 0xBB2C3C);
        Place(_surface, star, 348, 27);
        _effects.Add((star, "comic-star"));
    }

    private void BuildForge()
    {
        Place(_surface, Poly(Gradient(0x474849, 0x1B2229), 0,0, 377,0, 400,19, 400,116, 22,116, 0,95), 0, 0);
        Place(_surface, Poly(Brush(0xBA6136), 22,113, 400,113, 400,116, 22,116), 0, 0);
        foreach (var (x, y) in new[] { (10, 8), (384, 101) })
            Place(_surface, new Ellipse { Width = 5, Height = 5, Fill = Brush(0xB5A99A) }, x, y);
        var core = Poly(Gradient(0xFBC971, 0xD65C2A), 25,0, 50,13, 50,52, 25,65, 0,52, 0,13);
        Place(_surface, core, 24, 25);
        _effects.Add((core, "forge-core"));
        Place(_surface, Poly(Brush(0x5A2D22), 14,0, 11,16, 20,10, 27,24, 25,34, 15,40, 4,34, 0,24, 7,14, 7,25, 13,20), 35, 37);
        for (var i = 0; i < 4; i++)
        {
            Place(_surface, new Border { Width = 14, Height = 4, Background = Brush(0xF3A35C), Opacity = .55 },
                376, 25 + i * 10);
            var spark = new Border
            {
                Width = 3, Height = 8, Background = Brush(0xFFCF83), Opacity = 0,
                RenderTransform = new RotateTransform { Angle = 25 + i * 20 }
            };
            Place(_surface, spark, 36 + i * 8, 36 + i * 3);
            _effects.Add((spark, "spark"));
        }
    }

    private void BuildAurora()
    {
        var ribbonBrush = Gradient(0x90D9DB, 0xE9B0CE);
        ribbonBrush.GradientStops.Add(new GradientStop { Color = Color(0xA49DE0), Offset = .5 });
        var ribbon = new Border
        {
            Width = 376, Height = 108, CornerRadius = new CornerRadius(22, 40, 20, 36),
            Background = ribbonBrush, Opacity = .88,
            RenderTransform = new RotateTransform { Angle = -4, CenterX = 188, CenterY = 54 }
        };
        Place(_surface, ribbon, 12, -3);
        _effects.Add((ribbon, "ribbon"));
        var front = new Border
        {
            Width = 392, Height = 116, CornerRadius = new CornerRadius(28, 16, 32, 18),
            Background = Gradient(0x264454, 0x50405E), BorderBrush = Brush(0x82A4B8), BorderThickness = new Thickness(1)
        };
        Place(_surface, front, 0, 0);
        Place(_surface, Text("✦", 37, 0xCAE8F0), 26, 31);
        Place(_surface, Text("✧", 15, 0xE8C5E6), 54, 30);
        var shine = Poly(Gradient(0xDDEFFC, 0xE8B8E6, .16), 28,0, 62,0, 34,114, 0,114);
        Place(_surface, shine, 292, 1);
        _effects.Add((shine, "aurora-shine"));
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        if (!_animate) return;
        try { PlayEntrance(); }
        catch (Exception error)
        {
            // Decorative animation failure must never take down recording or previews.
            _animate = false;
            StopAnimations();
            foreach (var element in _surface.Children.OfType<FrameworkElement>().Prepend(_surface))
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.Opacity = 1;
                visual.Scale = Vector3.One;
                visual.RotationAngleInDegrees = 0;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            LolHighlight.Recording.Infrastructure.RuntimeDiagnostics.Write(
                new LolHighlight.Recording.Infrastructure.AppPaths().ResolveRuntimeDirectory(".runtime"),
                $"事件提示动画不可用，已使用静态样式：{error}");
        }
    }

    private void PlayEntrance()
    {
        var visual = VisualFor(_surface);
        visual.CenterPoint = new Vector3((float)_surface.Width / 2, (float)_surface.Height / 2, 0);
        var start = _style switch
        {
            "neon" => new Vector3(130, 0, 0),
            "ink" => new Vector3(42, -16, 0),
            "blade" => new Vector3(190, 0, 0),
            "comic" => new Vector3(20, -25, 0),
            "forge" => new Vector3(0, -40, 0),
            "aurora" => new Vector3(75, 20, 0),
            _ => Vector3.Zero
        };
        Vector(visual, "Translation", 460, (0, start),
            (.8f, _style is "comic" or "forge" ? new Vector3(0, 5, 0) : new Vector3(-4, 0, 0)), (1, Vector3.Zero));
        Scalar(visual, "Opacity", 200, (0, 0), (1, 1));
        if (_style is "crystal" or "pulse" or "comic")
        {
            visual.CenterPoint = _style == "pulse" ? new Vector3(340, 44, 0) : new Vector3(200, 58, 0);
            Vector(visual, "Scale", 480, (0, new Vector3(_style == "pulse" ? .15f : .8f, .8f, 1)),
                (.8f, new Vector3(1.035f, 1.035f, 1)), (1, Vector3.One));
        }
        if (_style == "crystal") Scalar(visual, "RotationAngleInDegrees", 480, (0, 5), (1, 0));
        if (_style == "comic") Scalar(visual, "RotationAngleInDegrees", 480, (0, 8), (.75f, -4), (1, -2));
        if (_style == "aurora") Scalar(visual, "RotationAngleInDegrees", 550, (0, 5), (1, 0));

        Vector(VisualFor(_progress), "Scale", 3640, (0, Vector3.One), (1, new Vector3(0, 1, 1)));
        foreach (var (element, effect) in _effects)
        {
            var v = VisualFor(element);
            v.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            switch (effect)
            {
                case "sweep": Vector(v, "Translation", 850, (0, new Vector3(-280,0,0)), (1, new Vector3(80,0,0))); break;
                case "stroke": Vector(v, "Scale", 400, (0, new Vector3(.05f,1,1)), (1, Vector3.One)); break;
                case "shard":
                case "gem": Vector(v, "Translation", 750, (0, new Vector3(effect == "shard" ? 45 : -25, -18, 0)), (1, Vector3.Zero)); break;
                case "speed": Vector(v, "Translation", 650, (0, new Vector3(85,0,0)), (1, new Vector3(-170,0,0))); break;
                case "pulse": Vector(v, "Scale", 1000, (0, new Vector3(.6f)), (1, new Vector3(1.7f,1.7f,1))); break;
                case "comic-star":
                    Vector(v, "Scale", 650, (0, new Vector3(.4f,.4f,1)), (.65f, new Vector3(1.3f,1.3f,1)), (1, Vector3.One));
                    Scalar(v, "RotationAngleInDegrees", 650, (0, -45), (.65f, 10), (1, 0));
                    break;
                case "forge-core": Scalar(v, "Opacity", 850, (0, .6f), (.35f, 1), (1, .8f)); break;
                case "spark":
                    Vector(v, "Translation", 750, (0, Vector3.Zero), (1, new Vector3(80 + (float)Canvas.GetLeft(element), -65, 0)));
                    Scalar(v, "Opacity", 750, (0, 0), (.15f, 1), (1, 0));
                    break;
                case "ribbon":
                    Vector(v, "Translation", 900, (0, new Vector3(50, 0, 0)), (.8f, new Vector3(-5, 0, 0)), (1, Vector3.Zero));
                    break;
                case "aurora-shine": Vector(v, "Translation", 1300, (0, new Vector3(-280,0,0)), (1, new Vector3(90,0,0))); break;
            }
            if (effect is "speed" or "pulse") Scalar(v, "Opacity", 1000, (0, 1), (1, 0));
        }
    }

    public void PlayExit()
    {
        if (!_animate) return;
        var v = VisualFor(_surface);
        Scalar(v, "Opacity", 360, (0, 1), (1, 0));
        var end = _style switch
        {
            "neon" => new Vector3(140,0,0),
            "ink" => new Vector3(-28,12,0),
            "blade" => new Vector3(-220,0,0),
            "comic" => new Vector3(80,-15,0),
            "forge" => new Vector3(0,25,0),
            "aurora" => new Vector3(130,0,0),
            _ => Vector3.Zero
        };
        Vector(v, "Translation", 360, (0, Vector3.Zero), (1, end));
        if (_style is "pulse" or "crystal")
            Vector(v, "Scale", 360, (0, Vector3.One),
                (1, _style == "pulse" ? new Vector3(.08f,.6f,1) : new Vector3(1.12f,1.12f,1)));
        if (_style == "comic")
        {
            Vector(v, "Scale", 360, (0, Vector3.One), (1, new Vector3(.25f,.25f,1)));
            Scalar(v, "RotationAngleInDegrees", 360, (0, -2), (1, 10));
        }
        if (_style == "aurora")
            Vector(v, "Scale", 360, (0, Vector3.One), (1, new Vector3(.5f,1,1)));
        foreach (var (element, effect) in _effects)
        {
            if (effect is "shard" or "ink")
                Vector(VisualFor(element), "Translation", 360, (0, Vector3.Zero), (1, new Vector3(35,-18,0)));
        }
    }

    public void StopAnimations()
    {
        foreach (var visual in _animated)
            foreach (var property in new[] { "Opacity", "Translation", "Scale", "RotationAngleInDegrees" })
                visual.StopAnimation(property);
        _animated.Clear();
        Loaded -= OnLoaded;
    }

    private Visual VisualFor(FrameworkElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!_animated.Contains(visual)) _animated.Add(visual);
        return visual;
    }

    private static void Scalar(Visual visual, string property, int milliseconds, params (float Time, float Value)[] frames)
    {
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        foreach (var (time, value) in frames) animation.InsertKeyFrame(time, value);
        visual.StartAnimation(property, animation);
    }

    private static void Vector(Visual visual, string property, int milliseconds, params (float Time, Vector3 Value)[] frames)
    {
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        foreach (var (time, value) in frames) animation.InsertKeyFrame(time, value);
        visual.StartAnimation(property, animation);
    }

    private static void Place(Canvas parent, FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        parent.Children.Add(element);
    }

    private static TextBlock Text(string value, double size, int color) => new()
    {
        Text = value, FontSize = size, Foreground = Brush(color), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI")
    };

    private static Polygon Poly(Brush fill, params double[] coordinates)
    {
        var polygon = new Polygon { Fill = fill };
        for (var i = 0; i < coordinates.Length; i += 2)
            polygon.Points.Add(new Point(coordinates[i], coordinates[i + 1]));
        return polygon;
    }

    private static Color Color(int value) => Windows.UI.Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    private static SolidColorBrush Brush(int color) => new(Color(color));
    private static LinearGradientBrush Gradient(int from, int to, double opacity = 1)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), Opacity = opacity };
        brush.GradientStops.Add(new GradientStop { Color = Color(from), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color(to), Offset = 1 });
        return brush;
    }
}
