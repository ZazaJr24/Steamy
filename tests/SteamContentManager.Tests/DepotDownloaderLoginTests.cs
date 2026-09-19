using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// The app may pass an account name, but it must never pass, read or store a secret.
/// These tests pin that behaviour down.
/// </summary>
public sealed class DepotDownloaderLoginTests
{
    private static DepotDownloaderRequest Request(string username = "", bool interactive = false) => new()
    {
        ExecutablePath = @"C:\Tools\DepotDownloader.exe",
        AppId = 730,
        DepotId = 731,
        TargetFolder = @"D:\SteamLibrary\Counter-Strike 2",
        WorkingDirectory = @"C:\Tools",
        AuthorizationConfirmed = true,
        SteamUsername = username,
        InteractiveConsole = interactive
    };

    [Fact]
    public void Build_WithoutAccountUsesAnonymousMode()
    {
        var command = DepotDownloaderArgumentBuilder.Build(Request());

        Assert.DoesNotContain("-username", command.Arguments);
        Assert.DoesNotContain("-remember-password", command.Arguments);
        Assert.Equal(
            new[] { "-app", "730", "-dir", @"D:\SteamLibrary\Counter-Strike 2", "-depot", "731" },
            command.Arguments);
        Assert.False(command.Interactive);
    }

    [Fact]
    public void Build_WithAccountAddsUsernameAndRemembersTheSession()
    {
        var command = DepotDownloaderArgumentBuilder.Build(Request("gamer_42"));

        Assert.Contains("-username", command.Arguments);
        Assert.Contains("gamer_42", command.Arguments);
        Assert.Contains("-remember-password", command.Arguments);
        var arguments = command.Arguments.ToList();
        Assert.Equal("gamer_42", arguments[arguments.IndexOf("-username") + 1]);
    }

    [Fact]
    public void Build_NeverPassesAPasswordOrSteamGuardCode()
    {
        var command = DepotDownloaderArgumentBuilder.Build(Request("gamer_42"));

        Assert.DoesNotContain("-password", command.Arguments);
        Assert.DoesNotContain("-qr", command.Arguments);
        Assert.DoesNotContain("-no-mobile", command.Arguments);

        // Only the session flag is allowed to mention a password; no value is ever passed.
        Assert.Single(command.Arguments, argument => argument.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-remember-password", command.Arguments);
    }

    [Fact]
    public void RequestType_HasNoFieldForASecret()
    {
        var names = typeof(DepotDownloaderRequest).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Guard", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("gamer 42")]
    [InlineData("gamer\"42")]
    [InlineData("a")]
    [InlineData("gamer;42")]
    public void Validate_RejectsAccountNamesThatCouldBreakTheCommandLine(string username)
    {
        Assert.False(DepotDownloaderArgumentBuilder.Validate(Request(username)).IsValid);
    }

    [Fact]
    public void Build_CarriesTheInteractiveFlagIntoTheCommand()
    {
        Assert.True(DepotDownloaderArgumentBuilder.Build(Request("gamer_42", interactive: true)).Interactive);
    }

    [Fact]
    public void ProcessFactory_AnonymousModeCapturesOutputForProgressParsing()
    {
        var command = DepotDownloaderArgumentBuilder.Build(Request());

        var startInfo = DepotDownloaderProcessFactory.Create(command, command.Interactive);

        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void ProcessFactory_InteractiveModeKeepsTheToolsOwnConsoleForPrompts()
    {
        var command = DepotDownloaderArgumentBuilder.Build(Request("gamer_42", interactive: true));

        var startInfo = DepotDownloaderProcessFactory.Create(command, command.Interactive);

        // A visible console is the only place a console application can ask for a password:
        // measured on this machine, only ShellExecute gives the tool a console of its own.
        Assert.True(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(@"C:\Tools\DepotDownloader.exe", startInfo.FileName);
        Assert.Contains("-username gamer_42", startInfo.Arguments);
        Assert.Contains("-remember-password", startInfo.Arguments);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"D:\Steam Library\Counter-Strike 2", "\"D:\\Steam Library\\Counter-Strike 2\"")]
    [InlineData(@"D:\Games\", @"D:\Games\")] // nothing to quote, so it stays untouched
    [InlineData(@"D:\Games Folder\", @"""D:\Games Folder\\""")] // quoted: the trailing backslash is doubled
    [InlineData("quote\"inside", @"""quote\""inside""")] // an inner quote must be escaped
    public void QuoteArgument_FollowsWindowsCommandLineRules(string input, string expected)
    {
        Assert.Equal(expected, DepotDownloaderProcessFactory.QuoteArgument(input));
    }

    [Fact]
    public void OutputParser_StillParsesRealProgressLines()
    {
        var progress = DepotDownloaderOutputParser.Parse("  45,50% depot 731 - downloading chunk 9 of 20 (12.3 MB / 27.1 MB)");

        Assert.NotNull(progress);
        Assert.Equal(45.5, progress!.Percent);
    }
}
