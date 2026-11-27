#nullable enable
using System;

namespace Glome.Core;

// A chart-authored ellipse lifts to normalize(a + b cos(theta) + c sin(theta)).
// In each half-circle t = tan(theta/2) is bounded by one. The unnormalized
// point is quadratic in t, so extrema of p dot normalize(v) solve a quartic.
internal readonly struct SphericalEllipse(D4 center, D4 cosine, D4 sine)
{
    internal D4 Nearest(D4 p)
    {
        D4 best = (center + cosine).Unit();
        double distance = (best - p).Length;
        Span<double> coefficients = stackalloc double[5];
        Span<double> roots = stackalloc double[4];
        for (int sign = -1; sign <= 1; sign += 2)
        {
            D4 a = center + cosine * sign, b = sine * (2 * sign), c = center - cosine * sign;
            double l = a.Dot(a), m = a.Dot(b), n = b.Dot(b) + 2*a.Dot(c), o = b.Dot(c), k = c.Dot(c);
            double x = p.Dot(a), y = p.Dot(b), z = p.Dot(c);
            coefficients[0] = y*l - x*m;
            coefficients[1] = y*m + 2*z*l - x*n;
            coefficients[2] = 3*(z*m - x*o);
            coefficients[3] = z*n - y*o - 2*x*k;
            coefficients[4] = z*o - y*k;
            int count = Roots(coefficients, roots);
            for (int i = -2; i < count; i++)
            {
                double t = i == -2 ? -1 : i == -1 ? 1 : roots[i];
                D4 q = (a + (b + c*t)*t).Unit();
                double d = (q - p).Length;
                if (d < distance) { best = q; distance = d; }
            }
        }
        return best;
    }

    // Derivative roots partition the interval into monotone pieces. Even-order
    // roots of the stationarity equation do not change the extremum.
    private static int Roots(ReadOnlySpan<double> coeff, Span<double> roots)
    {
        int degree = coeff.Length - 1;
        while (degree > 0 && coeff[degree] == 0) degree--;
        if (degree == 0) return 0;
        if (degree == 1)
        {
            double t = -coeff[0] / coeff[1];
            if (Math.Abs(t) >= 1) return 0;
            roots[0] = t;
            return 1;
        }
        Span<double> derivative = stackalloc double[degree];
        Span<double> extrema = stackalloc double[degree];
        for (int i = 1; i <= degree; i++) derivative[i-1] = i*coeff[i];
        int n = Roots(derivative, extrema);
        extrema[n] = 1;
        int count = 0;
        double left = -1, f = Evaluate(coeff, left);
        for (int i = 0; i <= n; i++)
        {
            double right = extrema[i], g = Evaluate(coeff, right);
            if (g == 0 && right < 1) roots[count++] = right;
            if (f*g < 0)
            {
                double lo = left, hi = right;
                for (int j = 0; j < 50; j++)
                {
                    double mid = (lo + hi)*0.5;
                    if (Math.Sign(Evaluate(coeff, mid)) == Math.Sign(f)) lo = mid;
                    else hi = mid;
                }
                roots[count++] = (lo + hi)*0.5;
            }
            left = right;
            f = g;
        }
        return count;
    }

    private static double Evaluate(ReadOnlySpan<double> coeff, double t)
    {
        double value = 0;
        for (int i = coeff.Length-1; i >= 0; i--) value = value*t + coeff[i];
        return value;
    }
}
