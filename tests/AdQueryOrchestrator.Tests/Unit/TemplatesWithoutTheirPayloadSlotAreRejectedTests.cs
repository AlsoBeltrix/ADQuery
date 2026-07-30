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
/// slice6r2-or-1: both prompt templates substitute their payload with a blind
/// <c>string.Replace</c>, which does nothing at all when the token is absent. A template file
/// missing its placeholder loads, wins over the built-in fallback, and sends the model rules with
/// no reduction — or on the Translate path, plan guidance with no user query. The call succeeds
/// and nothing in the logs distinguishes it from a good one.
///
/// So a template is only usable if it carries the slot for what the model must be given. These
/// tests hold both directions: a template without its slot is rejected in favour of the working
/// fallback, and a template with it still wins.
/// </summary>
public sealed class TemplatesWithoutTheirPayloadSlotAreRejectedTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "adquery-template-slots-" + Guid.NewGuid().ToString("N"));

    public TemplatesWithoutTheirPayloadSlotAreRejectedTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    [Fact]
    public async Task AnAnswerTemplateWithoutItsPlaceholder_DoesNotSwallowTheReduction()
    {
        // The template reads like a plausible one — it even says REDUCTION: — but the slot that
        // carries the numbers is gone, which is what an editor deletes by accident.
        var path = WriteTemplate(
            "answer-no-slot.txt",
            "Answer from the reduction below.\nRULES:\n- Never invent a value.\n\nREDUCTION:\n");

        var handler = new RecordingHandler();
        var prompt = await NarrateAsync(handler, answerTemplate: path);

        Assert.Contains(Reduction, prompt, StringComparison.Ordinal);
        // The fallback ran, so its rules are what the model got.
        Assert.Contains("COMPLETENESS", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslateTemplateWithoutItsPlaceholder_DoesNotSwallowTheQuery()
    {
        var path = WriteTemplate(
            "translate-no-slot.txt",
            "Act as an expert Active Directory analyst.\n{{CONTEXT}}\nUSER QUERY:\n");

        var handler = new RecordingHandler();
        var prompt = await TranslateAsync(handler, promptTemplate: path);

        Assert.Contains(Query, prompt, StringComparison.Ordinal);
        Assert.Contains("JSON FORMAT:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerTemplateWithItsPlaceholder_StillWinsOverTheFallback()
    {
        // The over-removal sentinel: rejecting every template would satisfy the test above.
        var path = WriteTemplate("answer-slot.txt", "ONLY-FROM-THE-FILE\n{{REDUCTION}}");

        var handler = new RecordingHandler();
        var prompt = await NarrateAsync(handler, answerTemplate: path);

        Assert.Contains("ONLY-FROM-THE-FILE", prompt, StringComparison.Ordinal);
        Assert.Contains(Reduction, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("COMPLETENESS line is present", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslateTemplateWithItsPlaceholder_StillWinsOverTheFallback()
    {
        var path = WriteTemplate("translate-slot.txt", "ONLY-FROM-THE-FILE\n{{USER_QUERY}}");

        var handler = new RecordingHandler();
        var prompt = await TranslateAsync(handler, promptTemplate: path);

        Assert.Contains("ONLY-FROM-THE-FILE", prompt, StringComparison.Ordinal);
        Assert.Contains(Query, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON FORMAT:", prompt, StringComparison.Ordinal);
    }

    private const string Reduction =
        "QUESTION: how many contractors are there\nRESULT: count = 4271.";

    private const string Query = "how many contractors are there";

    private string WriteTemplate(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<string> NarrateAsync(RecordingHandler handler, string answerTemplate)
    {
        var service = CreateService(handler, answerTemplate: answerTemplate);
        await service.GenerateAnswerAsync(
            Reduction,
            cancellationToken: TestContext.Current.CancellationToken);

        return PromptOf(Assert.Single(handler.Bodies));
    }

    private static async Task<string> TranslateAsync(RecordingHandler handler, string promptTemplate)
    {
        var service = CreateService(handler, promptTemplate: promptTemplate);
        await service.GenerateExecutionPlanAsync(
            Query,
            cancellationToken: TestContext.Current.CancellationToken);

        return PromptOf(Assert.Single(handler.Bodies));
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

    private static ClaudeService CreateService(
        RecordingHandler handler,
        string? promptTemplate = null,
        string? answerTemplate = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = "test-api-key",
                ["Claude:BaseUrl"] = "https://provider.example",
                ["Claude:Endpoint"] = "/v1/messages",
                ["Claude:Model"] = "@integration/model",
                // Each test drives one path; the other points at nothing so the checked-in
                // templates cannot answer for it.
                ["Claude:PromptTemplate"] = promptTemplate ?? "missing-translate-template.txt",
                ["Claude:AnswerPromptTemplate"] = answerTemplate ?? "missing-answer-template.txt",
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
