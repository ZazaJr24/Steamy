using Steamy.Models;

namespace Steamy.UiTests;

public sealed partial class ModelNotificationMemoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void UnchangedModelsDoNotAllocateTheirOwnNotificationQueues()
    {
        const int count = 20_000;
        var models = new QuietModel[count];
        _ = new QuietModel(); // Warm type initialization outside the allocation interval.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < models.Length; i++) models[i] = new QuietModel();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{count:N0} unchanged models: {allocated:N0} allocated bytes ({allocated / count} per model).");
        Assert.InRange(allocated, 0, count * 64L);
        GC.KeepAlive(models);
    }
    private sealed class QuietModel : UiObservableObject { }
}
