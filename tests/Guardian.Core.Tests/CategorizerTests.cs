using Guardian.Contracts;
using Guardian.Core.Categorization;
using Xunit;

namespace Guardian.Core.Tests;

public class CategorizerTests
{
    private static Categorizer Defaults()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lists", "defaults.yaml"));
        var rules = DefaultRules.Parse(yaml);
        Assert.True(rules.Count > 40, "defaults.yaml should parse to many rules");
        long id = 1; foreach (var r in rules) r.Id = id++;
        return new Categorizer(rules, 1);
    }

    [Fact]
    public void Steam_is_gaming_and_google_docs_is_school()
    {
        var c = Defaults();
        Assert.Equal(Categories.Gaming, c.Categorize("steam.exe", null, "Steam"));
        Assert.Equal(Categories.Gaming, c.Categorize("Steam", null, null));
        Assert.Equal(Categories.School, c.Categorize("chrome", "docs.google.com", "Essay - Google Docs"));
        Assert.Equal(Categories.School, c.Categorize("msedge", "google.com", null));
        Assert.Equal(Categories.Other, c.Categorize("mspaint", null, "Untitled - Paint"));
        Assert.Equal(Categories.Other, c.Categorize("chrome", "youtube.com", "Cats"));
    }

    [Fact]
    public void Priority_wins_and_wildcards_work()
    {
        var rules = new List<Rule>
        {
            new() { Id = 1, MatchType = RuleMatch.TitleContains, Pattern = "homework", Category = Categories.School, Priority = 5 },
            new() { Id = 2, MatchType = RuleMatch.Process, Pattern = "minecraft*", Category = Categories.Gaming, Priority = 10 },
            new() { Id = 3, MatchType = RuleMatch.Domain, Pattern = "youtube.com", Category = Categories.Other, Priority = 10 },
        };
        var c = new Categorizer(rules, 1);
        Assert.Equal(Categories.School, c.Categorize("minecraft.windows", null, "Homework video"));
        Assert.Equal(Categories.Gaming, c.Categorize("Minecraft.Windows.exe", null, "Minecraft"));
        Assert.True(Categorizer.DomainMatches("www.youtube.com", "youtube.com"));
        Assert.False(Categorizer.DomainMatches("notyoutube.com", "youtube.com"));
    }

    [Fact]
    public void Browser_names_are_recognised()
    {
        Assert.True(BrowserNames.IsBrowser("chrome.exe"));
        Assert.True(BrowserNames.IsBrowser("msedge"));
        Assert.False(BrowserNames.IsBrowser("steam"));
    }
}
