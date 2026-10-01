using Steamy.Services;

namespace Steamy.Tests;

public sealed class GameSearchIndexTests
{
    private sealed record App(int Id, string Name, bool Visible = true);
    private static GameSearchIndex<App> Index(params App[] apps) => new(apps, app => app.Id, app => app.Name);

    [Theory]
    [InlineData("offlien library")]
    [InlineData("offine library")]
    [InlineData("offline librarry")]
    [InlineData("offlina library")]
    public void CorrectsOneEditWithoutChangingTitle(string query)
    {
        var index = Index(new App(10, "An offline library game"), new(20, "Other game"));
        var result = index.Find(query);
        Assert.True(result.IsTypoMatch);
        Assert.Equal("An offline library game", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void ExactMatchesWinOverApproximateMatches()
    {
        var result = Index(new App(10, "Librarry"), new(20, "Library")).Find("Librarry");
        Assert.False(result.IsTypoMatch);
        Assert.Equal(10, Assert.Single(result.Items).Id);
    }

    [Fact]
    public void FirstAndLastLetterBucketFindsMiddleTransposition()
    {
        var result = Index(new App(10, "Elden Ring")).Find("elden rnig");
        Assert.True(result.IsTypoMatch);
        Assert.Equal(10, Assert.Single(result.Items).Id);
    }

    [Fact]
    public void AllTermsAndAccentInsensitiveNamesStillMatch()
    {
        var result = Index(new App(10, "Pokémon Legend"), new(20, "Legend of something")).Find("legend pokemon");
        Assert.False(result.IsTypoMatch);
        Assert.Equal(10, Assert.Single(result.Items).Id);
    }

    [Theory]
    [InlineData("Assassin’s Creed", "assassins creed")]
    [InlineData("Marvel's Spider-Man", "spiderman marvels")]
    [InlineData("Counter-Strike 2", "counterstrike 2")]
    [InlineData("Counter-Strike 2", "counter strike 2")]
    public void PunctuationDoesNotRequireAnExactSpelling(string title, string query)
    {
        var result = Index(new App(10, title)).Find(query);
        Assert.False(result.IsTypoMatch);
        Assert.Equal(title, Assert.Single(result.Items).Name);
    }

    [Fact]
    public void EligibilityIsAppliedBeforeFallback()
    {
        var index = Index(new App(10, "Librarry", false), new(20, "Library"));
        var result = index.Find("librarry", eligible: app => app.Visible);
        Assert.True(result.IsTypoMatch);
        Assert.Equal(20, Assert.Single(result.Items).Id);
    }

    [Fact]
    public void NumericAppIdNeverGetsTypoCorrected()
    {
        var index = Index(new App(1245620, "Elden Ring"), new(25, "2048"));
        Assert.Equal(1245620, Assert.Single(index.Find("4562").Items).Id);
        Assert.Equal(25, Assert.Single(index.Find("2048").Items).Id);
        Assert.Empty(index.Find("1245621").Items);
        Assert.False(index.Find("1245621").IsTypoMatch);
    }

    [Fact]
    public void ShortAmbiguousWordsDoNotTriggerFuzzyMatching()
    {
        Assert.Empty(Index(new App(10, "God of War")).Find("gdo").Items);
        Assert.Empty(Index(new App(10, "Library")).Find("lxxrary").Items);
    }

    [Fact]
    public void CancellationStopsReusableIndexQueries()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Index(new App(10, "Library")).Find("library", cancellation.Token));
    }

    [Fact]
    public void LargeSharedPrefixVocabularyIsBoundedAndDeterministic()
    {
        var apps = Enumerable.Range(1, 20_000).Select(id => new App(id, $"prefix{id:D5}")).ToArray();
        var index = new GameSearchIndex<App>(apps, app => app.Id, app => app.Name);
        var first = index.Find("prefiz99999");
        var second = index.Find("prefiz99999");
        Assert.Equal(first.Items.Select(app => app.Id), second.Items.Select(app => app.Id));
        Assert.Empty(first.Items);
    }
}
