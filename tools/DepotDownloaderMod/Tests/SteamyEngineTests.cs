// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using DepotDownloader;
using SteamKit2;

namespace DepotDownloaderMod.Tests;

public sealed class SteamyEngineTests
{
    [Fact]
    public async Task RetryBudgetStopsAtConfiguredAdditionalAttempts()
    {
        var delays = new List<TimeSpan>();
        var budget = new SteamyRetryBudget(2, (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, () => 0.5);

        await budget.BeforeAttemptAsync(CancellationToken.None);
        await budget.BeforeAttemptAsync(CancellationToken.None);
        await budget.BeforeAttemptAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SteamyDownloadException>(
            () => budget.BeforeAttemptAsync(CancellationToken.None));
        Assert.Equal("retry_exhausted", exception.Code);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task RetryWaitHonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        var budget = new SteamyRetryBudget(1, (_, token) =>
        {
            cancelled.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, () => 0.5);

        await budget.BeforeAttemptAsync(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.BeforeAttemptAsync(cancelled.Token));
    }

    [Fact]
    public void DepotKeyParsingIsAllOrNothingAndDoesNotEchoSecrets()
    {
        const string secret = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var exception = Assert.Throws<FormatException>(() => DepotKeyStore.AddAll(
            [$"1091500;{secret}", $"1091501;not-a-key-{secret}"]));

        Assert.False(DepotKeyStore.ContainsKey(1091500));
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DepotManifestMustMatchTheRequestedIdentity()
    {
        var manifest = new DepotManifest { DepotID = 8, ManifestGID = 90, Files = [] };
        SteamyManifestValidation.Validate(manifest, 8, 90, Path.GetTempPath());
        Assert.Throws<SteamyDownloadException>(() => SteamyManifestValidation.Validate(manifest, 8, 91, Path.GetTempPath()));
    }
}
