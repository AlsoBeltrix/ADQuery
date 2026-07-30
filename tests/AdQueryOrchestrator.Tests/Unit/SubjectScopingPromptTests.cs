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
/// F04 Slice 6c guard: the Translate prompt tells the model to read a follow-up within the
/// conversation's established subject rather than escaping it to a directory-wide set, and
/// to state the interpretation it used in the plan description.
///
/// Both prompt paths carry it. The external template is authoritative when present and the
/// built-in fallback is used when it is missing, so guidance in only one of them would go
/// silently absent depending on deployment.
/// </summary>
public sealed class SubjectScopingPromptTests
{
    // Phrases the guidance turns on, checked in both paths: the scoping rule itself, the
    // explicit-exit condition, the worked example, and the interpretation statement.
    private static readonly string[] RequiredGuidance =
    [
        "established subject",
        "forget Sanjay",
        "add the users in China",
        "does NOT mean every user in China",
        "states the interpretation",
        // slice6-or-2: the exit condition turns on what is being replaced, not on a phrase,
        // and carries the constraint-replacement counter-example that distinguishes the two.
        "replaces THE SUBJECT ITSELF",
        "Replacing a CONSTRAINT is not replacing the subject",
        "instead of titles, only people in China",
    ];

    // slice6-or-2: bare "instead of..." once sat in the exit list beside two genuine resets,
    // licensing a directory-wide escape for an ordinary constraint correction. It must not
    // come back — in either path.
    private const string RetiredResetTrigger = "\"instead of...\"";

    [Fact]
    public void CheckedInTemplate_CarriesSubjectScopingGuidance()
    {
        var template = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Configuration", "prompt_template.txt"));

        foreach (var phrase in RequiredGuidance)
        {
            Assert.Contains(phrase, template, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(RetiredResetTrigger, template, StringComparison.Ordinal);

        // The guidance is only reachable when a follow-up actually supplies context.
        Assert.Contains("{{CONTEXT}}", template, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltInFallback_CarriesTheSameGuidance()
    {
        // A missing template file degrades the wording, never the scoping rule.
        var handler = new RecordingHandler();
        var service = CreateServiceWithoutTemplate(handler);

        await service.GenerateExecutionPlanAsync(
            "add the users in China",
            context: "Previous questions, oldest first:\n- everyone under Sanjay\n- only with titles",
            cancellationToken: TestContext.Current.CancellationToken);

        var prompt = PromptOf(Assert.Single(handler.Bodies));

        foreach (var phrase in RequiredGuidance)
        {
            Assert.Contains(phrase, prompt, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(RetiredResetTrigger, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreadContext_ReachesTheModelIntact()
    {
        // The accumulated thread must arrive in the prompt; scoping guidance is inert
        // without the earlier questions it tells the model to scope to.
        var handler = new RecordingHandler();
        var service = CreateServiceWithoutTemplate(handler);

        await service.GenerateExecutionPlanAsync(
            "add the users in China",
            context: "Previous questions, oldest first:\n- everyone under Sanjay\n- only with titles",
            cancellationToken: TestContext.Current.CancellationToken);

        var prompt = PromptOf(Assert.Single(handler.Bodies));

        Assert.Contains("CONTEXT:", prompt, StringComparison.Ordinal);
        Assert.Contains("everyone under Sanjay", prompt, StringComparison.Ordinal);
        Assert.Contains("only with titles", prompt, StringComparison.Ordinal);
        Assert.Contains("add the users in China", prompt, StringComparison.Ordinal);
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
                ["Claude:PromptTemplate"] = "missing-subject-scoping-template.txt",
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
