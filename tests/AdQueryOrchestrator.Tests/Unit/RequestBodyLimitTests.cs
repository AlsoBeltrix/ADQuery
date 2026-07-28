using AdQuery.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// REQBODY-D1 guard: the transport request-body cap is wired to the owner-approved
/// 2 MiB, independent of any feature. Under IIS in-process hosting
/// <see cref="IISServerOptions.MaxRequestBodySize"/> is authoritative; the Kestrel
/// limit is inert in-process but must carry the same value for a future direct host.
/// This fails if the wiring is dropped (the host default is 30,000,000 bytes for IIS
/// and 30 MiB for Kestrel — neither equals 2 MiB).
/// </summary>
public sealed class RequestBodyLimitTests
{
    private const long ExpectedCap = 2L * 1024 * 1024;

    [Fact]
    public void IisServerBodyCap_IsTwoMebibytes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var options = factory.Services.GetRequiredService<IOptions<IISServerOptions>>().Value;

        Assert.Equal(ExpectedCap, options.MaxRequestBodySize);
    }

    [Fact]
    public void KestrelBodyCap_IsTwoMebibytes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var options = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Equal(ExpectedCap, options.Limits.MaxRequestBodySize);
    }
}
