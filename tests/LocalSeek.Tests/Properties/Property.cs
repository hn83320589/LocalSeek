using System.Runtime.CompilerServices;

namespace LocalSeek.Tests.Properties;

/// <summary>
/// Minimal seeded property runner. Every iteration gets its own case seed derived from the
/// fixed property seed; a failure reports that case seed so it can be replayed alone with
/// the environment variable LOCALSEEK_SEED.
/// </summary>
public static class Property
{
    public const string SeedVariable = "LOCALSEEK_SEED";

    public static void ForAll(int seed, int iterations, Action<Random> body, [CallerMemberName] string name = "")
    {
        if (ParseReplaySeed(Environment.GetEnvironmentVariable(SeedVariable)) is { } replaySeed)
        {
            Run(name, iteration: 0, replaySeed, body);
            return;
        }

        var seeds = new Random(seed);
        for (var i = 0; i < iterations; i++)
        {
            Run(name, i, seeds.Next(), body);
        }
    }

    /// <summary>
    /// Asserts a minimum number of checked cases. Skipped when replaying a single case, where no
    /// minimum can hold; the property itself still fails inside <see cref="ForAll"/>.
    /// </summary>
    public static void AssertCoverage(bool condition, string message) =>
        AssertCoverage(condition, message, replay: ParseReplaySeed(Environment.GetEnvironmentVariable(SeedVariable)) is not null);

    internal static void AssertCoverage(bool condition, string message, bool replay)
    {
        if (!replay)
        {
            Assert.True(condition, message);
        }
    }

    internal static int? ParseReplaySeed(string? value) => int.TryParse(value, out var seed) ? seed : null;

    private static void Run(string name, int iteration, int caseSeed, Action<Random> body)
    {
        try
        {
            body(new Random(caseSeed));
        }
        catch (Exception ex)
        {
            throw new PropertyFailedException(
                $"Property '{name}' failed at iteration {iteration}. Reproduce with {SeedVariable}={caseSeed}. {ex.Message}",
                ex);
        }
    }
}

public sealed class PropertyFailedException(string message, Exception inner) : Exception(message, inner);
