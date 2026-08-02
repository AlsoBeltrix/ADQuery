using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Options;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F06 Slice 2 guard: an allow-listed attribute is permitted under both its LDAP name and its
/// PowerShell display name.
///
/// Earned by the retry of live job `5c1a4abb`, which failed validation with
/// "Step 1 requests attribute 'l' which is not allow-listed for User." The list holds `City`
/// but not `l` — the same attribute under the other convention — so a plan was refused for
/// its choice of synonym rather than for what it asked to read.
///
/// This is an inconsistency rather than a policy: `user_allow_attr.txt` already carries both
/// conventions in places, listing `physicalDeliveryOfficeName` beside `Office` and `sn` beside
/// `Surname`.
///
/// The security property, asserted below and load-bearing: <b>a synonym admits exactly the
/// attributes the file already allows.</b> It resolves one name to another; it never widens
/// the set.
/// </summary>
public sealed class AttributeSynonymsTests
{
    /// <summary>
    /// Drives the <b>shipped</b> allow-list files rather than the hardcoded fallback, because
    /// the defect is in what those files contain. `Configuration/*.txt` is copied beside the
    /// test binary by the csproj's content glob.
    /// </summary>
    private static DirectorySecurityPolicy Policy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AttributeFiles:User"] = "Configuration/user_allow_attr.txt",
                ["Security:AttributeFiles:Group"] = "Configuration/group_allow_attr.txt",
                ["Security:AttributeFiles:Computer"] = "Configuration/comp_allow_attr.txt",
                ["Security:AttributeFiles:OrganizationalUnit"] = "Configuration/ou_allow_attr.txt",
            })
            .Build();

        return new DirectorySecurityPolicy(
            configuration,
            new StubEnvironment(),
            NullLogger<PlanValidator>.Instance);
    }

    [Theory]
    // The pair that broke the live query.
    [InlineData("l", "City")]
    // The rest of the address/location cluster, which fails the same way.
    [InlineData("st", "State")]
    [InlineData("street", "StreetAddress")]
    [InlineData("streetAddress", "StreetAddress")]
    [InlineData("c", "Country")]
    [InlineData("co", "Country")]
    [InlineData("postOfficeBox", "POBox")]
    [InlineData("facsimileTelephoneNumber", "Fax")]
    public void AnLdapName_IsAllowedWhereItsDisplayNameIs(string ldapName, string displayName)
    {
        var policy = Policy();

        Assert.True(
            policy.IsAttributeAllowed(DirectoryObjectType.User, displayName),
            $"precondition: '{displayName}' should be on the User allow-list.");
        Assert.True(
            policy.IsAttributeAllowed(DirectoryObjectType.User, ldapName),
            $"'{ldapName}' is the LDAP name of the allowed attribute '{displayName}' and must be accepted too.");
    }

    [Fact]
    public void ASynonymNeverWidensTheAllowList()
    {
        // The security property. `employeeType` is allowed, but a synonym must not smuggle in
        // an attribute the file does not contain, and a name with no canonical entry stays
        // refused however it is spelled.
        var policy = Policy();

        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "unicodePwd"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "ntSecurityDescriptor"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "msDS-ManagedPassword"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "thumbnailPhoto"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "not-an-attribute"));
    }

    [Fact]
    public void ASynonymOfADisallowedAttribute_StaysDisallowedForOtherObjectTypes()
    {
        // Synonym resolution must respect the per-object-type lists, not collapse them.
        // `City` is a User attribute; a Computer plan asking for `l` must not gain it.
        var policy = Policy();

        var cityOnComputer = policy.IsAttributeAllowed(DirectoryObjectType.Computer, "City");
        var ldapOnComputer = policy.IsAttributeAllowed(DirectoryObjectType.Computer, "l");

        Assert.Equal(cityOnComputer, ldapOnComputer);
    }

    [Fact]
    public void TheCanonicalNamesStillWork()
    {
        // Over-correction guard: adding synonyms must not disturb direct hits.
        var policy = Policy();

        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "displayName"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "physicalDeliveryOfficeName"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "Office"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "sn"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "Surname"));
    }

    [Fact]
    public void EveryPairThePromptAdvertises_ActuallyResolves()
    {
        // The prompt tells the model both conventions work. If the map and the prose drift,
        // the model is being lied to and a plan fails validation for obeying instructions —
        // which is exactly how the live defect surfaced. This holds the claim to the code.
        var template = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Configuration", "prompt_template.txt"));
        var policy = Policy();

        foreach (var (ldapName, displayName) in AdvertisedPairs)
        {
            Assert.True(
                template.Contains(ldapName, StringComparison.Ordinal),
                $"the prompt should advertise '{ldapName}'.");
            Assert.True(
                policy.IsAttributeAllowed(DirectoryObjectType.User, ldapName),
                $"the prompt advertises '{ldapName}' but the policy refuses it.");
            Assert.True(
                policy.IsAttributeAllowed(DirectoryObjectType.User, displayName),
                $"the prompt advertises '{displayName}' but the policy refuses it.");
        }

        Assert.Contains("EITHER the LDAP name or the PowerShell name", template, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBuiltInFallback_AdvertisesTheSamePairs()
    {
        // The two-path contract: a missing template file must degrade wording, never a rule.
        // Without this, the fallback could stop telling the model both conventions work and
        // nothing would notice — the deployment with no template file would reproduce the
        // original defect exactly.
        var handler = new PromptCapturingHandler();
        var service = CreateServiceWithoutTemplate(handler);

        await service.GenerateExecutionPlanAsync(
            "how many users are in Chelmsford",
            cancellationToken: TestContext.Current.CancellationToken);

        var prompt = Assert.Single(handler.Bodies);

        Assert.Contains("EITHER the LDAP name or the PowerShell name", prompt, StringComparison.Ordinal);
        foreach (var (ldapName, displayName) in AdvertisedPairs)
        {
            Assert.Contains(ldapName, prompt, StringComparison.Ordinal);
            Assert.Contains(displayName, prompt, StringComparison.Ordinal);
        }
    }

    private static ClaudeService CreateServiceWithoutTemplate(PromptCapturingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = "test-api-key",
                ["Claude:BaseUrl"] = "https://provider.example",
                ["Claude:Endpoint"] = "/v1/messages",
                ["Claude:Model"] = "@integration/model",
                // Absent on purpose: exercises the built-in fallback.
                ["Claude:PromptTemplate"] = "missing-synonym-template.txt",
            })
            .Build();
        var providerOptions = Options.Create(
            configuration.GetSection(LlmProviderOptions.SectionName).Get<LlmProviderOptions>()
            ?? new LlmProviderOptions());

        return new ClaudeService(
            new HttpClient(handler),
            NullLogger<ClaudeService>.Instance,
            configuration,
            providerOptions,
            new LlmMessagesRequestBuilder(providerOptions));
    }

    private sealed class PromptCapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            using var document = System.Text.Json.JsonDocument.Parse(body);
            var prompt = new System.Text.StringBuilder();
            foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
            {
                prompt.AppendLine(message.GetProperty("content").ToString());
            }

            Bodies.Add(prompt.ToString());

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"content\":[{\"text\":\"{}\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private static readonly (string Ldap, string Display)[] AdvertisedPairs =
    [
        ("l", "City"),
        ("st", "State"),
        ("c", "Country"),
        ("street", "StreetAddress"),
        ("physicalDeliveryOfficeName", "Office"),
        ("telephoneNumber", "OfficePhone"),
        ("sn", "Surname"),
    ];

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = System.AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = System.AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
