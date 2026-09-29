// Test-only stand-ins for the app types the linked sharing sources reference. Steamy.Models.LogLevel
// mirrors the app's enum so Steamy.Services.OwnedGames compiles unchanged in both projects.

namespace Steamy.Models
{
    public enum LogLevel { Debug, Info, Warning, Error }
}

namespace Steamy.Services
{
    using System.Collections.Concurrent;
    using Steamy.Models;

    public interface ILoggingService
    {
        void Add(LogLevel level, string source, string message, int? appId = null, Guid? jobId = null);
    }

    public sealed class InMemoryLoggingService : ILoggingService
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public void Add(LogLevel level, string source, string message, int? appId = null, Guid? jobId = null) =>
            _lines.Enqueue($"{level} {source}: {message}");
    }

    public static class StableDnsHandler
    {
        public static HttpMessageHandler Create() => new HttpClientHandler();
    }
}
