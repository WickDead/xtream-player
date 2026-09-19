using XtreamPlayer.Infrastructure.Security;
using Xunit;

namespace XtreamPlayer.Tests;

/// <summary>
/// Cases are modelled on real URL shapes observed from a live provider,
/// including the two-hop redirect to a tokenised edge host.
/// </summary>
public sealed class CredentialRedactorTests
{
    [Theory]
    // The everyday case: credentials are path segments in every media URL.
    [InlineData(
        "http://example.pw/live/SomeUser1/Sekr3tPass/610.ts",
        "http://example.pw/live/***/***/610.ts")]
    [InlineData(
        "http://example.pw/movie/SomeUser1/Sekr3tPass/1359763.mkv",
        "http://example.pw/movie/***/***/1359763.mkv")]
    [InlineData(
        "http://example.pw/series/SomeUser1/Sekr3tPass/99.mp4",
        "http://example.pw/series/***/***/99.mp4")]
    // API calls put them in the query string instead.
    [InlineData(
        "http://example.pw/player_api.php?username=SomeUser1&password=Sekr3tPass&action=get_live_streams",
        "http://example.pw/player_api.php?username=***&password=***&action=get_live_streams")]
    // The edge host swaps credentials for an opaque token, which is just as
    // sensitive: it is a bearer credential for the stream.
    [InlineData(
        "http://abc.edge.pw:80/live/play/WWxKR1IxcExkbE4zVVU1V1RHSkJURFZVYXpWdw==/610",
        "http://abc.edge.pw:80/live/play/***/610")]
    public void RedactsCredentialsInUrls(string input, string expected) =>
        Assert.Equal(expected, CredentialRedactor.Redact(input));

    [Fact]
    public void RedactsCredentialsInsideLogLines()
    {
        const string line =
            "[Error] main: connection failed for "
          + "http://example.pw/live/SomeUser1/Sekr3tPass/610.ts (timeout)";

        var redacted = CredentialRedactor.Redact(line);

        Assert.DoesNotContain("Sekr3tPass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SomeUser1", redacted, StringComparison.Ordinal);
        Assert.Contains("timeout", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsKnownLiteralSecretsAnywhere()
    {
        const string text = "auth blob Sekr3tPass appeared in an unexpected shape";

        var redacted = CredentialRedactor.Redact(text, "SomeUser1", "Sekr3tPass");

        Assert.DoesNotContain("Sekr3tPass", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresLiteralsTooShortToBeDistinctive()
    {
        // Redacting a 2-character secret would shred unrelated text.
        const string text = "the cat sat on the mat";

        Assert.Equal(text, CredentialRedactor.Redact(text, "at"));
    }

    [Fact]
    public void LeavesInnocentTextAlone()
    {
        const string text = "Loaded 68595 VOD items in 4.2s from the catalog.";

        Assert.Equal(text, CredentialRedactor.Redact(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HandlesEmptyInput(string? input) =>
        Assert.Equal(string.Empty, CredentialRedactor.Redact(input));
}
