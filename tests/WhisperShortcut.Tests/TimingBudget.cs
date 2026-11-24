using System.Diagnostics;

namespace WhisperShortcut.TestSupport;

/// <summary>
/// Time budgets for the timing tests. A budget is written for a normal PC; shared CI runners are slower and noisier
/// (WelcomeWindowTests took 4.5 s against 4 s, a 2 s Gemini body 187-516 ms against 100 ms), and a red main stops the
/// merge train and the nightly release. So on CI (GitHub sets <c>CI=true</c>) the budget is multiplied by
/// <see cref="CiFactor"/>. That still catches what these tests are for (every page relaying out, a copy per sample,
/// drawing at rest: several times slower, not 10 % slower).
/// <para>Strict anywhere: <c>WSW_TIMING_FACTOR=1</c> (or any factor, e.g. 2) overrides both.</para>
/// Linked into WhisperShortcut.UiTests as well.
/// </summary>
public static class TimingBudget
{
    public const double CiFactor = 3.0;

    /// <summary>Multiplier in effect: WSW_TIMING_FACTOR if set and valid, else <see cref="CiFactor"/> on CI, else 1.</summary>
    public static double Factor
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("WSW_TIMING_FACTOR");
            if (double.TryParse(env, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0)
                return f;
            return string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? CiFactor : 1.0;
        }
    }

    /// <summary>The budget for this machine, in milliseconds.</summary>
    public static long Ms(long budgetOnAPc) => (long)Math.Ceiling(budgetOnAPc * Factor);

    /// <summary>
    /// Fastest of <paramref name="runs"/> timings of <paramref name="action"/>, in milliseconds. The minimum is the code's
    /// own cost; a scheduler hiccup on the runner only makes single runs slower.
    /// </summary>
    public static double BestOf(int runs, Action action)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}
