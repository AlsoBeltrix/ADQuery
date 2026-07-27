using System.Reflection;
using AdQuery.Orchestrator.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// Guards F01 SYNC-D1: the unused synchronous <c>POST api/query/execute</c> endpoint
/// (<c>QueryController.ExecuteQuery</c>) is retired. The shipped browser drives every
/// query through the async path (<c>execute-async</c> + job polling); nothing calls the
/// sync endpoint. This guard fails if the sync <c>execute</c> route is reintroduced, and
/// separately confirms the async <c>execute-async</c> route is left intact (over-removal).
/// </summary>
public sealed class SyncExecuteEndpointRetiredTests
{
    [Fact]
    public void SyncExecuteRoute_IsNotMappedOnController()
    {
        var templates = PostTemplates();

        Assert.DoesNotContain("execute", templates);
    }

    [Fact]
    public void AsyncExecuteRoute_SurvivesRetirement()
    {
        var templates = PostTemplates();

        Assert.Contains("execute-async", templates);
    }

    private static IReadOnlyList<string> PostTemplates()
    {
        return typeof(QueryController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<HttpPostAttribute>())
            .Where(attribute => attribute?.Template is not null)
            .Select(attribute => attribute!.Template!)
            .ToList();
    }
}
