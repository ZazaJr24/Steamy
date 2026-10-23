using Xunit;

// These integration tests share WPF Application resources and native host fixtures.
// Serialize these process-wide integrations for deterministic verification.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestCollectionOrderer("Steamy.UiTests.WpfSmokeLastOrderer", "Steamy.UiTests")]

namespace Steamy.UiTests;

// Application.Shutdown is process-wide in WPF. Standalone STA-window tests must
// finish before the one integration collection creates and shuts down Application.
public sealed class WpfSmokeLastOrderer : ITestCollectionOrderer
{
    public IEnumerable<Xunit.Abstractions.ITestCollection> OrderTestCollections(IEnumerable<Xunit.Abstractions.ITestCollection> collections)
        => collections.OrderBy(collection => collection.DisplayName.Contains(nameof(PageSmokeTests), StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(collection => collection.DisplayName, StringComparer.Ordinal);
}
