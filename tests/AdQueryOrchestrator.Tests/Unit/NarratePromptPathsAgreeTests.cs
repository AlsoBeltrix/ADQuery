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
/// slice5r2-or-1: the Narrate prompt exists twice — the external
/// <c>Configuration/answer_prompt_template.txt</c> when it loads, the built-in
/// <c>StringBuilder</c> block otherwise — and <c>BuildAnswerPrompt</c>'s contract is that a
/// missing file degrades the wording, never a rule. A rule reaching one path only turns a
/// missing file into a behaviour change, which is the opposite of the contract.
///
/// It has happened: <c>ci-or-1</c> taught the template that a COMPLETENESS line means every
/// figure is a floor, and never taught the fallback, so a deployment without the file would
/// narrate a truncated count as the count. The reduction emits that line on both paths.
///
/// The list below is the whole check. A rule Narrate depends on is added there once and both
/// paths are held to it.
/// </summary>
public sealed class NarratePromptPathsAgreeTests
{
    /// <summary>
    /// One phrase per rule Narrate's contract names, chosen to be the load-bearing words rather
    /// than a whole sentence: the two wordings are deliberately different lengths, and a guard
    /// that pinned full sentences would force them identical and make the external file
    /// pointless.
    /// </summary>
    private static readonly (string Rule, string Template, string Fallback)[] SharedRules =
    [
        // ci-or-1 / slice5r2-or-1: the rule whose absence from the fallback is this test's reason
        // to exist. Both paths must say a capped figure is a floor.
        ("completeness", "COMPLETENESS", "COMPLETENESS"),
        ("a floor, not the count", "floor", "floor"),
        ("say \"at least\"", "at least", "at least"),
        // The no-invention constraint the doc comment names explicitly.
        ("no invention", "Never invent", "Never invent"),
        // The reduction is bounded; the largest buckets are not the whole distribution.
        ("bounded buckets", "LARGEST VALUES", "LARGEST VALUES"),
        ("not the complete list", "complete list", "complete list"),
        // slice1r2-or-1: a distribution of singletons has no meaningful most-common value.
        ("read the distribution", "DISTRIBUTION", "DISTRIBUTION"),
        ("empty result", "empty", "empty"),
        // F04 Slice 3: the answer states the interpretation so a misreading is correctable.
        ("state the interpretation", "QUERY RUN", "QUERY RUN"),
        // The table is rendered separately; the model writes the sentence above it.
        ("prose only", "no code fences", "no code fences"),
    ];

    [Fact]
    public void TheCheckedInTemplate_CarriesTheRules()
    {
        var template = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Configuration", "answer_prompt_template.txt"));

        foreach (var (rule, phrase, _) in SharedRules)
        {
            Assert.True(
                template.Contains(phrase, StringComparison.Ordinal),
                $"the external answer template must carry the '{rule}' rule (looked for '{phrase}')");
        }

        Assert.Contains("{{REDUCTION}}", template, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBuiltInFallback_CarriesTheSameRules()
    {
        var handler = new RecordingHandler();
        var service = CreateServiceWithoutAnswerTemplate(handler);

        await service.GenerateAnswerAsync(
            Reduction,
            cancellationToken: TestContext.Current.CancellationToken);

        var prompt = PromptOf(Assert.Single(handler.Bodies));

        foreach (var (rule, _, phrase) in SharedRules)
        {
            Assert.True(
                prompt.Contains(phrase, StringComparison.Ordinal),
                $"the built-in Narrate fallback must carry the '{rule}' rule (looked for '{phrase}')");
        }
    }

    [Fact]
    public async Task TheFallback_StillCarriesTheReductionItself()
    {
        // The over-removal sentinel: a fallback that satisfied every rule check above while
        // dropping the reduction would send the model instructions and no facts.
        var handler = new RecordingHandler();
        var service = CreateServiceWithoutAnswerTemplate(handler);

        await service.GenerateAnswerAsync(
            Reduction,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(Reduction, PromptOf(Assert.Single(handler.Bodies)), StringComparison.Ordinal);
    }

    private const string Reduction =
        "QUESTION: how many contractors are there\n"
        + "COMPLETENESS: partial — the query stopped at a system limit.\n"
        + "RESULT: count = 5000.";

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

    private static ClaudeService CreateServiceWithoutAnswerTemplate(RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = "test-api-key",
                ["Claude:BaseUrl"] = "https://provider.example",
                ["Claude:Endpoint"] = "/v1/messages",
                ["Claude:Model"] = "@integration/model",
                // Absent on purpose: this is the deployment the fallback exists for.
                ["Claude:AnswerPromptTemplate"] = "missing-answer-template.txt",
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
                    "{\"content\":[{\"text\":\"An answer.\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
