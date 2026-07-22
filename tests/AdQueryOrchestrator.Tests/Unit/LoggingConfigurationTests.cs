using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class LoggingConfigurationTests
{
    [Fact]
    public void LoggingPackages_MatchDotNet10Family()
    {
        Assert.Equal(10, typeof(Serilog.AspNetCore.RequestLoggingOptions).Assembly.GetName().Version?.Major);
        Assert.Equal(7, typeof(FileLoggerConfigurationExtensions).Assembly.GetName().Version?.Major);
    }

    [Fact]
    public void CheckedInConfiguration_WritesStructuredExceptionAndFlushesRollingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"adquery-serilog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var logPath = Path.Combine(directory, "verification-.txt");
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "appsettings.json"))
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Serilog:WriteTo:1:Args:path"] = logPath
                    })
                .Build();

            using (var logger = new LoggerConfiguration()
                       .ReadFrom.Configuration(configuration)
                       .CreateLogger())
            {
                logger.Error(
                    new InvalidOperationException("verification exception"),
                    "Structured logging check for {Operation}",
                    "P03");
            }

            var writtenFile = Assert.Single(Directory.GetFiles(directory, "verification-*.txt"));
            var contents = File.ReadAllText(writtenFile);

            Assert.Contains("Structured logging check for P03", contents, StringComparison.Ordinal);
            Assert.Contains("Operation", contents, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", contents, StringComparison.Ordinal);
            Assert.Contains("verification exception", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
