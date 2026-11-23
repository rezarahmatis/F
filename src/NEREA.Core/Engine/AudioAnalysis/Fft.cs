namespace NEREA.Core.Engine.AudioAnalysis;

/// <summary>Allocation-free in-place radix-2 FFT.</summary>
public sealed class Fft
{
    private readonly int size;
    private readonly float[] cos;
    private readonly float[] sin;
    private readonly int[] reverse;

    public int Size => size;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.", nameof(size));
        this.size = size;
        cos = new float[size / 2];
        sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            double a = -2.0 * Math.PI * i / size;
            cos[i] = (float)Math.Cos(a);
            sin[i] = (float)Math.Sin(a);
        }

        reverse = new int[size];
        int bits = (int)Math.Log2(size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            reverse[i] = r;
        }
    }

    public void Transform(float[] re, float[] im)
    {
        for (int i = 0; i < size; i++)
        {
            int j = reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= size; len <<= 1)
        {
            int half = len >> 1;
            int step = size / len;
            for (int start = 0; start < size; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = cos[k * step];
                    float wi = sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float tr = re[b] * wr - im[b] * wi;
                    float ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
