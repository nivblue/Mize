namespace Mize.Api.Stats;

/// <summary>Configuration of the statistics cache (section <c>Stats</c>).</summary>
public sealed class StatsOptions
{
    /// <summary>Name of the configuration section these options bind to.</summary>
    public const string SectionName = "Stats";

    /// <summary>How often the unflushed counters are written to the database, in seconds (fractions allowed). Default 5.</summary>
    public double FlushIntervalSeconds { get; set; } = 5;
}
