using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Interface for storing and retrieving user feedback.
/// </summary>
public interface IFeedbackStore
{
    /// <summary>
    /// Save feedback to persistent storage (JSONL file).
    /// </summary>
    Task SaveFeedbackAsync(QueryFeedback feedback);
}
