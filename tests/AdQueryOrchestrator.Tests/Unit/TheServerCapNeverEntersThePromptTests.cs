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
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// slice3r2-or-1 guard. The Translate call must not describe the server's configured
/// <c>QueryDefaults:MaxResults</c> ceiling to the model as a count the user asked for.
///
/// This is not a wording preference. ci-or-1 decides whether a truncated answer is incomplete
/// by asking whose limit applied, and the only signal it has is whether the returned plan
/// carries a <c>result_limit</c> — which the prompt contract reserves for a count the user
/// named. Telling the model the ceiling *is* such a count makes the server's own cap
/// indistinguishable from a user request, and the incompleteness caveat then never fires on
/// exactly the capped queries it exists for.
/// </summary>
public sealed class TheServerCapNeverEntersThePromptTests
{
    /// <summary>
    /// The retired injection, in the shape SubjectScopingPromptTests uses for a retired
    /// phrase: assert it cannot come back, in either prompt path.
    /// </summary>
    private const string RetiredCapInjection = "The user explicitly requested only";

    private const string RetiredPlaceholder = "{{RESULT_LIMIT_GUIDANCE}}";

    [Fact]
    public async Task TheBuiltInPrompt_NeverDescribesAServerCapAsAUserRequest()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler, withTemplate: false);

        await service.GenerateExecutionPlanAsync(
            "how many people report up through Sanjay?",
            context: null,
            cancellationToken: TestContext.Current.CancellationToken);

        var prompt = PromptOf(Assert.Single(handler.Bodies));

        Assert.DoesNotContain(RetiredCapInjection, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RetiredPlaceholder, prompt, StringComparison.Ordinal);

        // The guidance that survives is the one the contract rests on: result_limit is for a
        // count the *user* named.
        Assert.Contains("ONLY set result_limit when user explicitly specifies a count", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheckedInTemplatePath_DoesTheSame()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler, withTemplate: true);

        await service.GenerateExecutionPlanAsync(
            "how many people report up through Sanjay?",
            context: null,
            cancellationToken: TestContext.Current.CancellationToken);

        var prompt = PromptOf(Assert.Single(handler.Bodies));

        Assert.DoesNotContain(RetiredCapInjection, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RetiredPlaceholder, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeployedTemplateStillCarryingThePlaceholder_RendersNothingForIt()
    {
        // A template file deployed before this change still holds the token. Leaving it
        // unreplaced would ship the literal "{{RESULT_LIMIT_GUIDANCE}}" to the model.
        var template = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Configuration", "prompt_template.txt"));

        Assert.DoesNotContain(RetiredPlaceholder, template, StringComparison.Ordinal);
        Assert.DoesNotContain(RetiredCapInjection, template, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanCarryingTheCeilingAsItsOwnLimit_IsStillSystemImposed()
    {
        // What the defect produced: the model, told the ceiling was the user's count, returns
        // exactly it. EnsurePlanLimit must not read that back as a user request — and with the
        // injection gone, a plan can only reach this state by coincidence or by the model
        // ignoring the contract, so the safe reading is the one that caveats.
        const int Ceiling = 5000;
        var plan = SearchPlan();
        plan.ResultLimit = Ceiling;

        Preprocessor().EnsurePlanLimit(plan, Ceiling);

        // A user who names exactly the ceiling is answered completely at that count; the
        // classification is unchanged and this asserts the boundary is still where ci-or-1 put
        // it. The defect was never this comparison — it was the prompt manufacturing the input.
        Assert.False(plan.ResultLimitIsSystemImposed);
    }

    [Fact]
    public void WithNoLimitInThePlan_TheCeilingIsSystemImposed()
    {
        // The state the model now returns for an uncounted question, which is the whole point
        // of removing the injection: no result_limit, so the ceiling is visibly the server's.
        var plan = SearchPlan();

        Preprocessor().EnsurePlanLimit(plan, 5000);

        Assert.True(plan.ResultLimitIsSystemImposed);
        Assert.Equal(5000, plan.ResultLimit);
    }

    private static PlanPreprocessor Preprocessor() => new(new ConfigurationBuilder().Build());

    private static DirectoryQueryPlan SearchPlan() => new()
    {
        Description = "Everyone reporting up through Sanjay",
        Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition { RowStep = "s1" },
    };

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

    private static ClaudeService CreateService(RecordingHandler handler, bool withTemplate)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = "test-api-key",
                ["Claude:BaseUrl"] = "https://provider.example",
                ["Claude:Endpoint"] = "/v1/messages",
                ["Claude:Model"] = "@integration/model",
                ["Claude:PromptTemplate"] = withTemplate
                    ? Path.Combine(AppContext.BaseDirectory, "Configuration", "prompt_template.txt")
                    : "missing-server-cap-template.txt",
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
