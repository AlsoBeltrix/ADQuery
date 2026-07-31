using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F05 Slice 1 guard: the Translate prompt tells the model that a bare "how many" question is
/// a pure count — an aggregation with an EMPTY group_by — and that it must never group on an
/// attribute a filter in the same plan has already pinned to a single value.
///
/// Earned by a live-AD job (`86b348a6`, "how many enabled users are there?"), whose executed
/// plan carried `filters: [Enabled equals true]` together with
/// `aggregation: {group_by: ["Enabled"]}`. That plan is grouped, so
/// <c>HeadlineClassifier</c> rung 2 fires and the user gets a two-bucket table instead of the
/// number. The classifier's pure-count rung is correct and already guarded; it never fires
/// because the plan it is handed does not describe the question.
///
/// Both prompt paths carry the rule. The external template is authoritative when present and
/// the built-in fallback is used when it is missing, so guidance in only one of them would go
/// silently absent depending on deployment.
/// </summary>
public sealed class BareCountPromptTests
{
    // The two rules, checked in both paths. Each phrase carries the RULE, not decoration: the
    // empty-group_by instruction, the one-number-versus-table test that makes it decidable,
    // and the pinned-attribute prohibition with the reason it holds.
    private static readonly string[] RequiredGuidance =
    [
        "bare 'how many' question is a PURE COUNT",
        "EMPTY group_by",
        "one number or a table of numbers",
        "NEVER group_by an attribute that a filter in the same plan has already pinned to a single value",
        "the count of it is the answer",
    ];

    // The positive case for group_by must survive alongside the new rule. Wording that pushed
    // only toward pure counts would suppress a legitimate breakdown, which is the
    // over-correction risk the plan names.
    private static readonly string[] RequiredBreakdownCase =
    [
        "how many users in each department",
        "count by employee type",
    ];

    // The reading that shipped: grouping on the very attribute the plan filtered. Asserting
    // its absence is what a presence check alone cannot do (the slice7r2-or-1 lesson) — a
    // future edit could satisfy every phrase above while restoring the worked-wrong example.
    private const string RetiredPinnedGrouping = "group_by on the attribute being filtered";

    [Fact]
    public void CheckedInTemplate_CarriesBareCountGuidance()
    {
        var template = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Configuration", "prompt_template.txt"));

        AssertCarriesTheRules(template);
    }

    [Fact]
    public async Task BuiltInFallback_CarriesTheSameGuidance()
    {
        // A missing template file degrades the wording, never the rule.
        var handler = new RecordingHandler();
        var service = CreateServiceWithoutTemplate(handler);

        await service.GenerateExecutionPlanAsync(
            "how many enabled users are there?",
            cancellationToken: TestContext.Current.CancellationToken);

        AssertCarriesTheRules(PromptOf(Assert.Single(handler.Bodies)));
    }

    private static void AssertCarriesTheRules(string prompt)
    {
        foreach (var phrase in RequiredGuidance)
        {
            Assert.Contains(phrase, prompt, StringComparison.Ordinal);
        }

        foreach (var phrase in RequiredBreakdownCase)
        {
            Assert.Contains(phrase, prompt, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(RetiredPinnedGrouping, prompt, StringComparison.Ordinal);
    }

    private static string PromptOf(string body)
    {
        using var document = JsonDocument.Parse(body);

        var prompt = new StringBuilder();
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            prompt.AppendLine(message.GetProperty("content").ToString());
        }

        return prompt.ToString();
    }

    private static ClaudeService CreateServiceWithoutTemplate(RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = "test-api-key",
                ["Claude:BaseUrl"] = "https://provider.example",
                ["Claude:Endpoint"] = "/v1/messages",
                ["Claude:Model"] = "@integration/model",
                // Absent on purpose: exercises the built-in fallback.
                ["Claude:PromptTemplate"] = "missing-bare-count-template.txt",
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"content\":[{\"text\":\"{}\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
