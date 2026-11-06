using NAudio.Wave;
using System.Runtime.InteropServices;

namespace LolHighlight.Recording.Services;

internal sealed class AudioPcmConverter
{
    private static readonly Guid IeeeFloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    private double _position;
    private float _previousLeft;
    private float _previousRight;
    private int _sampleRate;

    public static byte[] ToFloat32Stereo(byte[] source, int byteCount, WaveFormat format)
        => new AudioPcmConverter().Convert(source, byteCount, format);

    public byte[] Convert(byte[] source, int byteCount, WaveFormat format)
    {
        if (byteCount <= 0 || format.SampleRate <= 0 || format.Channels <= 0 || format.BlockAlign <= 0)
            return Array.Empty<byte>();

        if (format.BlockAlign < format.Channels * SampleBytes(format))
            throw new ArgumentException("Invalid PCM frame alignment");
        if (_sampleRate != format.SampleRate)
        {
            _position = 0;
            _sampleRate = format.SampleRate;
        }
        var frameCount = Math.Min(byteCount, source.Length) / format.BlockAlign;
        if (frameCount <= 0)
            return Array.Empty<byte>();

        var inputSamples = new float[frameCount * 2];
        var inputOffset = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * format.BlockAlign;
            var left = ReadSample(source, offset, format);
            var right = format.Channels == 1
                ? left
                : ReadSample(source, offset, format, channelOffset: 1);

            inputSamples[inputOffset++] = left;
            inputSamples[inputOffset++] = right;
        }

        if (format.SampleRate == 48000)
            return MemoryMarshal.AsBytes(inputSamples.AsSpan()).ToArray();

        // Preserve fractional position and the preceding frame across callback boundaries.
        // Rounding each 44.1 kHz callback separately otherwise accumulates audible clock drift.
        var capacity = (int)Math.Ceiling((frameCount + 2) * 48000d / format.SampleRate) * 2;
        var output = new float[capacity];
        var written = 0;
        var step = format.SampleRate / 48000d;
        while (_position < frameCount - 1)
        {
            var first = (int)Math.Floor(_position);
            var fraction = (float)(_position - first);
            var left = first < 0 ? _previousLeft : inputSamples[first * 2];
            var right = first < 0 ? _previousRight : inputSamples[first * 2 + 1];
            output[written++] = Lerp(left, inputSamples[(first + 1) * 2], fraction);
            output[written++] = Lerp(right, inputSamples[(first + 1) * 2 + 1], fraction);
            _position += step;
        }
        _position -= frameCount;
        _previousLeft = inputSamples[^2];
        _previousRight = inputSamples[^1];
        return MemoryMarshal.AsBytes(output.AsSpan(0, written)).ToArray();
    }

    private static float ReadSample(byte[] source, int frameOffset, WaveFormat format, int channelOffset = 0)
    {
        var bytesPerSample = SampleBytes(format);
        var offset = frameOffset + channelOffset * bytesPerSample;

        var isIeeeFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                          format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubType;
        if (isIeeeFloat && bytesPerSample == 4)
            return BitConverter.ToSingle(source, offset);

        return format.BitsPerSample switch
        {
            8 => (source[offset] - 128) / 128f,
            16 => BitConverter.ToInt16(source, offset) / 32768f,
            24 => Read24(source, offset) / 8388608f,
            32 => BitConverter.ToInt32(source, offset) / 2147483648f,
            _ => 0f
        };
    }

    private static float Lerp(float first, float second, float amount)
        => first + (second - first) * amount;

    private static int SampleBytes(WaveFormat format) => Math.Max(1, format.BitsPerSample / 8);

    private static int Read24(byte[] source, int offset)
    {
        var value = source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}
