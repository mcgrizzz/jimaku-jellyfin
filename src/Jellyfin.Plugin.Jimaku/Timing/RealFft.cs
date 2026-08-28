using System;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Minimal in-place iterative radix-2 Cooley-Tukey FFT over separate real and imaginary arrays.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a package: the plugin needs exactly two operations on
/// power-of-two sizes, and a numerics dependency is not worth carrying into a Jellyfin plugin zip.
/// </remarks>
internal static class RealFft
{
    /// <summary>Returns the smallest power of two greater than or equal to <paramref name="n"/>.</summary>
    /// <param name="n">The minimum length.</param>
    /// <returns>A power of two.</returns>
    public static int NextPowerOfTwo(int n)
    {
        if (n <= 1)
        {
            return 1;
        }

        var power = 1;
        while (power < n)
        {
            power <<= 1;
        }

        return power;
    }

    /// <summary>Forward transform, in place.</summary>
    /// <param name="re">Real components.</param>
    /// <param name="im">Imaginary components.</param>
    public static void Forward(double[] re, double[] im) => Transform(re, im, inverse: false);

    /// <summary>Inverse transform, in place, scaled by 1/N.</summary>
    /// <param name="re">Real components.</param>
    /// <param name="im">Imaginary components.</param>
    public static void Inverse(double[] re, double[] im)
    {
        Transform(re, im, inverse: true);
        var scale = 1.0 / re.Length;
        for (var i = 0; i < re.Length; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    private static void Transform(double[] re, double[] im, bool inverse)
    {
        ArgumentNullException.ThrowIfNull(re);
        ArgumentNullException.ThrowIfNull(im);
        if (re.Length != im.Length)
        {
            throw new ArgumentException("Real and imaginary arrays must be the same length.", nameof(im));
        }

        var n = re.Length;
        if (n <= 1)
        {
            return;
        }

        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException("Length must be a power of two.", nameof(re));
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Butterflies.
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);

            for (var i = 0; i < n; i += len)
            {
                var curRe = 1.0;
                var curIm = 0.0;
                var half = len >> 1;
                for (var j = 0; j < half; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = (re[i + j + half] * curRe) - (im[i + j + half] * curIm);
                    var vIm = (re[i + j + half] * curIm) + (im[i + j + half] * curRe);

                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + half] = uRe - vRe;
                    im[i + j + half] = uIm - vIm;

                    var nextRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nextRe;
                }
            }
        }
    }
}
