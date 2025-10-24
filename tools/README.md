# ADQuery Feedback Analyzer

Standalone Python tool that analyzes user feedback from the ADQuery web application using Claude AI to generate actionable insights and recommendations.

## Features

- Reads feedback data from JSONL files written by the web app
- Computes statistical analysis (satisfaction rates, model performance, etc.)
- Uses Claude Opus to identify patterns and generate recommendations
- Outputs detailed markdown reports with prioritized action items

## Setup

### 1. Install Dependencies

```bash
cd tools
python -m pip install -r requirements.txt
```

### 2. Configure API Access

Copy the example config and add your credentials:

```bash
cp config.example.json config.json
```

Edit `config.json` and add your Portkey API key:

```json
{
  "anthropic": {
    "base_url": "https://api.portkey.ai",
    "api_key": "YOUR_PORTKEY_API_KEY",
    "analysis_model": "@vertexai-global/anthropic.claude-opus-4-1@20250805"
  }
}
```

**Note**: This tool uses its OWN API credentials, completely separate from the web app's `appsettings.json`.

### 3. Run Analysis

```bash
python analyze_feedback.py
```

## Usage

### Basic Analysis

```bash
# Analyze all feedback and generate report
python analyze_feedback.py

# Output: ./reports/feedback-analysis.md
```

### Filter by Date

```bash
# Only analyze feedback since October 1st
python analyze_feedback.py --since 2025-10-01
```

### Custom Paths

```bash
# Specify feedback directory and output file
python analyze_feedback.py \
  --feedback-dir /path/to/metrics \
  --output my-report.md
```

### Full Options

```bash
python analyze_feedback.py --help
```

## How It Works

1. **Load Feedback**: Reads `feedback-*.jsonl` files from the web app's metrics directory
2. **Compute Stats**: Calculates satisfaction rates, model performance, retry rates
3. **LLM Analysis**: Sends data to Claude Opus for pattern recognition and recommendations
4. **Generate Report**: Creates detailed markdown report with actionable insights

## Output Report Structure

The generated report includes:

- **Executive Summary**: Total feedback, satisfaction rate, retry rate
- **Model Performance**: Sonnet vs Opus success rates
- **User Comments**: Negative feedback with user explanations
- **AI Analysis**: Claude's pattern recognition and insights
- **Action Items**: Prioritized list of specific improvements to implement

## Example Report Sections

### Model Performance Comparison
```
### claude-sonnet-4
- Total Queries: 112
- Satisfaction Rate: 87.5%
- Negative Feedback: 14

### claude-opus-4-1
- Total Queries: 15
- Satisfaction Rate: 86.7%
- Negative Feedback: 2
```

### AI-Powered Recommendations
```
1. **Key Patterns**:
   - Recursive queries with "entire org" trigger 78% of Sonnet failures
   - Recommend: Auto-suggest Opus checkbox for these patterns

2. **Prompt Improvements**:
   - Users confused by "show my team" (ambiguous "my")
   - Recommend: Add clarification prompt for possessive queries

3. **Top 3 Action Items**:
   1. Add keyword detection for "entire org" → suggest checkbox
   2. Improve prompt clarity around org hierarchy queries
   3. Add validation for unbounded recursion patterns
```

## Integration with Web App

### Data Flow

```
Web App (C#)
    ↓
Writes feedback-*.jsonl files
    ↓
tools/analyze_feedback.py reads files
    ↓
Calls Claude Opus for analysis
    ↓
Generates markdown report
```

### Feedback File Format

The web app writes JSONL files like:

```json
{"timestamp":"2025-10-22T14:30:00Z","job_id":"abc123","user":"jsmith","query":"show all contractors","model_used":"claude-sonnet-4","sentiment":"positive","result_count":147,"response_time_ms":2341}
{"timestamp":"2025-10-22T14:35:00Z","job_id":"def456","user":"jdoe","query":"entire org under CEO","model_used":"claude-sonnet-4","sentiment":"negative","comment":"Too slow, incomplete data","user_requested_retry":true,"result_count":0}
```

## Cost Considerations

At typical usage (10-25 queries/day, monthly analysis):

- **Feedback to analyze**: ~450 entries
- **Analysis tokens**: ~10K input, ~3K output
- **Estimated cost**: ~$0.30 per analysis run

Run monthly to track trends over time.

## Troubleshooting

### "Config file not found"
- Make sure `config.json` exists in the `tools/` directory
- Copy from `config.example.json` and add your API key

### "No feedback data found"
- Check that the web app is writing to `../csharp/wwwroot/metrics/`
- Feedback files must be named `feedback-*.jsonl`
- Use `--feedback-dir` to specify a different location

### API Authentication Errors
- Verify your Portkey API key in `config.json`
- Ensure the `analysis_model` name matches your Vertex AI model catalog

## Development

### Running Tests

```bash
# TODO: Add tests
pytest tests/
```

### Adding New Analysis Features

Edit `analyze_feedback.py` and modify:

- `compute_basic_stats()` - Add new statistical calculations
- `_build_analysis_prompt()` - Modify what Claude analyzes
- `generate_report()` - Change report format

## License

Part of the ADQuery project.
