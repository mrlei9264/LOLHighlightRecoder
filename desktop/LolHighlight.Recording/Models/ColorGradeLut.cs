using System.Globalization;
using System.Text;

namespace LolHighlight.Recording.Models;

/// <summary>The same RGB lookup table drives the GPU preview and the exported video.</summary>
public static class ColorGradeLut
{
    public const int Size = 33;

    public static (double R, double G, double B) Transform(ColorTone tone, double strength, double r, double g, double b)
    {
        if (!double.IsFinite(strength) || strength is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(strength));
        var contrast = 1 + (tone.Contrast - 1) * strength;
        var exponent = 1 / (1 + (tone.Gamma - 1) * strength);
        double Level(double v) => Math.Pow(Math.Clamp((v - .5) * contrast + .5, 0, 1), exponent);
        r = Level(r); g = Level(g); b = Level(b);
        var luma = .2126 * r + .7152 * g + .0722 * b;
        var saturation = (1 + (tone.Saturation - 1) * strength) * (tone.Monochrome ? 1 - strength : 1);
        r = luma + (r - luma) * saturation;
        g = luma + (g - luma) * saturation;
        b = luma + (b - luma) * saturation;
        var shadows = Math.Pow(1 - luma, 2);
        var highlights = luma * luma;
        r += strength * (tone.RedShadows * shadows + tone.RedHighlights * highlights);
        b += strength * (tone.BlueShadows * shadows + tone.BlueHighlights * highlights);
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    // B varies fastest, as required by Win2D's EffectTransferTable3D.
    public static byte[] CreateRgb(ColorTone tone, double strength)
    {
        if (!double.IsFinite(strength) || strength is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(strength));
        var result = new byte[Size * Size * Size * 3];
        var i = 0;
        for (var r = 0; r < Size; r++)
        for (var g = 0; g < Size; g++)
        for (var b = 0; b < Size; b++)
        {
            var color = Transform(tone, strength, r / (double)(Size - 1), g / (double)(Size - 1), b / (double)(Size - 1));
            result[i++] = (byte)Math.Round(color.R * 255);
            result[i++] = (byte)Math.Round(color.G * 255);
            result[i++] = (byte)Math.Round(color.B * 255);
        }
        return result;
    }

    public static string ToCube(byte[] rgb)
    {
        if (rgb.Length != Size * Size * Size * 3) throw new ArgumentException("Invalid LUT size.");
        var text = new StringBuilder("TITLE \"Rift Replay\"\nLUT_3D_SIZE 33\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n");
        // .cube uses R as its fastest axis, unlike Win2D.
        for (var b = 0; b < Size; b++)
        for (var g = 0; g < Size; g++)
        for (var r = 0; r < Size; r++)
        {
            var i = ((r * Size + g) * Size + b) * 3;
            text.Append((rgb[i] / 255d).ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                .Append((rgb[i + 1] / 255d).ToString("0.########", CultureInfo.InvariantCulture)).Append(' ')
                .Append((rgb[i + 2] / 255d).ToString("0.########", CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }
}
