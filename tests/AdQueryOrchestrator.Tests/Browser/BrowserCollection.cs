using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Shares one <see cref="StaticSiteFixture"/> (static site + Chromium) across the
/// browser tests so the server and browser are launched once, not per test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<StaticSiteFixture>
{
    public const string Name = "browser";
}
