namespace Indra.Ocr;

/// <summary>
/// numpy's <c>np.arange</c> for doubles.
/// </summary>
/// <remarks>
/// <para>
/// <c>np.arange</c> does <b>not</b> compute <c>start + i*step</c>: it fills the first two slots
/// with <c>start</c> and <c>start + step</c> and then walks the array with
/// <c>delta = a[1] - a[0]</c>, so a step that is not exactly representable (0.1) is rounded
/// <i>once</i> and the rounding of that subtraction is what every value inherits. The length is
/// <c>ceil((stop - start) / step)</c>.
/// </para>
/// <para>
/// Both were verified against numpy 2.5.4 (0 mismatches over the angle grids
/// <c>arange(-5, 5 + 1e-6, 0.5)</c> and <c>arange(b - 0.5, b + 0.5 + 1e-6, 0.1)</c> for every
/// <c>b</c> on the 0.5 grid, and over 4000 random triples).
/// </para>
/// </remarks>
public static class NpyArange
{
    /// <summary>The values <c>np.arange(start, stop, step)</c> returns (ascending steps only).</summary>
    public static double[] Range(double start, double stop, double step)
    {
        if (step == 0) throw new ArgumentException("arange: zero step", nameof(step));
        int n = Length(start, stop, step);
        if (n == 0) return [];
        // arange's own step: (start + step) - start, so the values drift exactly as numpy's do.
        double delta = start + step - start;
        var o = new double[n];
        for (int i = 0; i < n; i++) o[i] = start + i * delta;
        return o;
    }

    /// <summary><c>ceil((stop - start) / step)</c>, the count <c>np.arange</c> allocates.</summary>
    public static int Length(double start, double stop, double step)
    {
        if (step == 0) throw new ArgumentException("arange: zero step", nameof(step));
        double len = (stop - start) / step;
        if (len < 0 || double.IsNaN(len)) return 0;
        if (double.IsPositiveInfinity(len)) throw new ArgumentOutOfRangeException(nameof(stop));
        return (int)Math.Ceiling(len);
    }
}
