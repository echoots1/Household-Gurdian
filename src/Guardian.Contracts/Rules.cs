namespace Guardian.Contracts;

public enum RuleMatch { Process, Domain, TitleContains }

/// <summary>A categorization rule. First match by ascending priority wins; unmatched falls to Other.</summary>
public sealed class Rule
{
    public long Id { get; set; }
    public RuleMatch MatchType { get; set; }
    /// <summary>Process name without extension (case-insensitive), registrable domain, or a title substring.</summary>
    public string Pattern { get; set; } = "";
    public string Category { get; set; } = Categories.Other;
    public int Priority { get; set; } = 100;
    public DateTimeOffset CreatedAt { get; set; }
}
