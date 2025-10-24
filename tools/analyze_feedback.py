#!/usr/bin/env python3
"""
ADQuery Feedback Analyzer
Analyzes user feedback and query patterns using Claude to generate actionable insights.

This is a STANDALONE tool - it reads feedback JSONL files from the web app's metrics directory
and uses its own API configuration to analyze patterns.

Usage:
    python analyze_feedback.py
    python analyze_feedback.py --since 2025-10-01
    python analyze_feedback.py --output report.md
"""

import json
import argparse
from datetime import datetime, timedelta
from pathlib import Path
from typing import List, Dict, Any
from collections import Counter, defaultdict
import anthropic
import os


class FeedbackAnalyzer:
    """Analyzes query feedback data and generates LLM-powered insights."""

    def __init__(self, feedback_dir: Path, config_file: Path = None):
        self.feedback_dir = feedback_dir

        # Load tool's own config
        if config_file is None:
            config_file = Path(__file__).parent / 'config.json'

        if not config_file.exists():
            raise FileNotFoundError(
                f"Config file not found: {config_file}\n"
                f"Copy config.example.json to config.json and add your API credentials."
            )

        with open(config_file, 'r') as f:
            config = json.load(f)

        claude_config = config.get('anthropic', {})

        # Initialize Anthropic client with Portkey/Vertex AI support
        base_url = claude_config.get('base_url')
        api_key = claude_config.get('api_key')

        if base_url and 'portkey' in base_url.lower():
            # Portkey gateway setup
            self.client = anthropic.Anthropic(
                base_url=base_url,
                api_key=api_key,  # This is the auth token for Portkey
                default_headers={
                    'x-portkey-api-key': api_key
                }
            )
        else:
            # Direct Anthropic API
            self.client = anthropic.Anthropic(api_key=api_key)

        self.model = claude_config.get('analysis_model', 'claude-opus-4-1')

    def load_feedback(self, since_date: datetime = None) -> List[Dict[str, Any]]:
        """Load all feedback entries from JSONL files."""
        feedback = []

        # Look for feedback files
        feedback_files = list(self.feedback_dir.glob("feedback-*.jsonl"))

        if not feedback_files:
            print(f"⚠️  No feedback files found in {self.feedback_dir}")
            return []

        for file_path in feedback_files:
            with open(file_path, 'r', encoding='utf-8') as f:
                for line in f:
                    if line.strip():
                        try:
                            entry = json.loads(line)
                            entry_date = datetime.fromisoformat(
                                entry['timestamp'].replace('Z', '+00:00')
                            )

                            if since_date is None or entry_date >= since_date:
                                feedback.append(entry)
                        except (json.JSONDecodeError, KeyError, ValueError) as e:
                            print(f"⚠️  Skipping invalid entry in {file_path}: {e}")
                            continue

        return sorted(feedback, key=lambda x: x['timestamp'])

    def compute_basic_stats(self, feedback: List[Dict]) -> Dict[str, Any]:
        """Compute basic statistics from feedback data."""
        total = len(feedback)

        if total == 0:
            return {"error": "No feedback data found"}

        # Sentiment breakdown
        sentiments = Counter(f.get('sentiment', 'unknown') for f in feedback)

        # Model performance
        model_stats = defaultdict(lambda: {'total': 0, 'positive': 0, 'negative': 0})
        for f in feedback:
            model = f.get('model_used', 'unknown')
            model_stats[model]['total'] += 1
            if f.get('sentiment') == 'positive':
                model_stats[model]['positive'] += 1
            elif f.get('sentiment') == 'negative':
                model_stats[model]['negative'] += 1

        # Retry analysis
        retries = [f for f in feedback if f.get('user_requested_retry', False)]
        retry_rate = len(retries) / total if total > 0 else 0

        # Response time analysis
        response_times = [f.get('response_time_ms', 0) for f in feedback if f.get('response_time_ms')]
        avg_response_time = sum(response_times) / len(response_times) if response_times else 0

        # Comments analysis
        negative_with_comments = [
            f for f in feedback
            if f.get('sentiment') == 'negative' and f.get('comment')
        ]

        return {
            'total_feedback': total,
            'date_range': {
                'start': feedback[0]['timestamp'],
                'end': feedback[-1]['timestamp']
            },
            'sentiment_breakdown': dict(sentiments),
            'satisfaction_rate': sentiments.get('positive', 0) / total,
            'model_performance': {
                model: {
                    'total': stats['total'],
                    'satisfaction_rate': stats['positive'] / stats['total'] if stats['total'] > 0 else 0,
                    'negative_count': stats['negative']
                }
                for model, stats in model_stats.items()
            },
            'retry_analysis': {
                'retry_rate': retry_rate,
                'retry_count': len(retries)
            },
            'avg_response_time_ms': avg_response_time,
            'negative_feedback_with_comments': len(negative_with_comments),
            'comments': [
                {
                    'query': f.get('query', 'N/A'),
                    'model': f.get('model_used', 'unknown'),
                    'comment': f.get('comment', '')
                }
                for f in negative_with_comments
            ]
        }

    def analyze_with_llm(self, stats: Dict[str, Any], feedback: List[Dict]) -> str:
        """Use Claude to analyze patterns and generate recommendations."""

        # Build analysis prompt
        prompt = self._build_analysis_prompt(stats, feedback)

        print(f"🤖 Calling {self.model} for analysis...")

        # Call Claude for analysis
        message = self.client.messages.create(
            model=self.model,
            max_tokens=4000,
            system="""You are an expert in analyzing user feedback for AI-powered query systems.
Your role is to:
1. Identify patterns in user satisfaction and dissatisfaction
2. Detect which types of queries benefit from more powerful models
3. Recommend specific improvements to prompts, validation, or UX
4. Provide actionable, specific recommendations backed by data

Be direct, specific, and focus on HIGH-IMPACT improvements.""",
            messages=[
                {"role": "user", "content": prompt}
            ]
        )

        return message.content[0].text

    def _build_analysis_prompt(self, stats: Dict[str, Any], feedback: List[Dict]) -> str:
        """Build the analysis prompt for Claude."""

        negative_feedback = [f for f in feedback if f.get('sentiment') == 'negative']
        positive_feedback = [f for f in feedback if f.get('sentiment') == 'positive']

        prompt = f"""Analyze this Active Directory query system feedback data and provide actionable recommendations.

## STATISTICS

Total Feedback: {stats['total_feedback']}
Date Range: {stats['date_range']['start']} to {stats['date_range']['end']}
Overall Satisfaction Rate: {stats['satisfaction_rate']:.1%}

Sentiment Breakdown:
{json.dumps(stats['sentiment_breakdown'], indent=2)}

## MODEL PERFORMANCE

{json.dumps(stats['model_performance'], indent=2)}

Retry Rate: {stats['retry_analysis']['retry_rate']:.1%}
Users Requested Opus Retry: {stats['retry_analysis']['retry_count']} times

## NEGATIVE FEEDBACK DETAILS

{len(negative_feedback)} negative feedback entries:

"""

        # Include sample negative queries with context
        for i, f in enumerate(negative_feedback[:10], 1):  # Max 10 samples
            prompt += f"\n{i}. Query: \"{f.get('query', 'N/A')}\"\n"
            prompt += f"   Model: {f.get('model_used', 'unknown')}\n"
            if f.get('comment'):
                prompt += f"   User Comment: \"{f['comment']}\"\n"
            if f.get('result_count') is not None:
                prompt += f"   Results Returned: {f['result_count']}\n"
            if f.get('user_requested_retry'):
                prompt += f"   User Retried with Opus: Yes\n"

        if len(negative_feedback) > 10:
            prompt += f"\n...and {len(negative_feedback) - 10} more negative feedback entries\n"

        prompt += f"""

## POSITIVE FEEDBACK QUERIES (Sample)

{len(positive_feedback)} queries received positive feedback. Sample successful queries:

"""

        for i, f in enumerate(positive_feedback[:5], 1):
            prompt += f"{i}. \"{f.get('query', 'N/A')}\" (Model: {f.get('model_used', 'unknown')})\n"

        prompt += """

## ANALYSIS REQUESTS

Please analyze this data and provide:

1. **Key Patterns**: What patterns do you see in negative vs positive feedback?
   - Which query types consistently fail or succeed?
   - Are certain models better for specific query patterns?
   - What themes emerge from user comments?

2. **Model Selection Insights**:
   - Does Opus actually perform better than Sonnet for certain queries?
   - Should we route certain query patterns directly to Opus?
   - What keywords/patterns should trigger "high accuracy mode" checkbox hint?

3. **Prompt Improvements**:
   - What specific prompt changes would address common failure patterns?
   - Are there misunderstandings in how the system interprets queries?

4. **Validation/Logic Issues**:
   - Are queries technically successful but semantically wrong?
   - What validation rules should be added?

5. **UX Recommendations**:
   - Should we adjust result limits, formatting, or presentation?
   - Are users expecting different output formats?

6. **Top 3 Action Items**:
   - Prioritized list of specific changes to implement
   - Each with expected impact and implementation effort

Be specific with examples. Quote query text and user comments to support recommendations.
"""

        return prompt

    def generate_report(self, stats: Dict[str, Any], analysis: str, output_file: Path = None) -> str:
        """Generate markdown report with statistics and LLM analysis."""

        report = f"""# ADQuery Feedback Analysis Report

Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}

## Executive Summary

- **Total Feedback**: {stats['total_feedback']} entries
- **Date Range**: {stats['date_range']['start']} to {stats['date_range']['end']}
- **Overall Satisfaction**: {stats['satisfaction_rate']:.1%}
- **Retry Rate**: {stats['retry_analysis']['retry_rate']:.1%}

## Model Performance Comparison

"""

        for model, perf in stats['model_performance'].items():
            report += f"### {model}\n"
            report += f"- Total Queries: {perf['total']}\n"
            report += f"- Satisfaction Rate: {perf['satisfaction_rate']:.1%}\n"
            report += f"- Negative Feedback: {perf['negative_count']}\n\n"

        report += f"""## Performance Metrics

- **Average Response Time**: {stats['avg_response_time_ms']:.0f}ms
- **Negative Feedback with Comments**: {stats['negative_feedback_with_comments']}

"""

        if stats['comments']:
            report += "## User Comments (Negative Feedback)\n\n"
            for comment in stats['comments'][:10]:
                report += f"- **Query**: \"{comment['query']}\"\n"
                report += f"  **Model**: {comment['model']}\n"
                report += f"  **Comment**: {comment['comment']}\n\n"

        report += f"""
---

## AI-Powered Analysis & Recommendations

{analysis}

---

## Data Source

Feedback data loaded from: `{self.feedback_dir}`

To update this report, run:
```bash
python analyze_feedback.py --output {output_file.name if output_file else 'report.md'}
```
"""

        if output_file:
            output_file.parent.mkdir(parents=True, exist_ok=True)
            output_file.write_text(report, encoding='utf-8')
            print(f"✅ Report saved to: {output_file}")

        return report


def main():
    parser = argparse.ArgumentParser(
        description="Analyze ADQuery user feedback with LLM-powered insights",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python analyze_feedback.py
  python analyze_feedback.py --since 2025-10-01
  python analyze_feedback.py --feedback-dir ../csharp/wwwroot/metrics
  python analyze_feedback.py --output reports/analysis.md
        """
    )
    parser.add_argument(
        '--feedback-dir',
        type=Path,
        default=Path('../csharp/wwwroot/metrics'),
        help='Directory containing feedback JSONL files (default: ../csharp/wwwroot/metrics)'
    )
    parser.add_argument(
        '--since',
        type=str,
        help='Only analyze feedback since this date (YYYY-MM-DD)'
    )
    parser.add_argument(
        '--output',
        type=Path,
        default=Path('./reports/feedback-analysis.md'),
        help='Output report file (default: ./reports/feedback-analysis.md)'
    )
    parser.add_argument(
        '--config',
        type=Path,
        help='Path to config.json (default: ./config.json)'
    )

    args = parser.parse_args()

    # Parse since date
    since_date = None
    if args.since:
        try:
            since_date = datetime.fromisoformat(args.since)
        except ValueError:
            print(f"❌ Invalid date format: {args.since}. Use YYYY-MM-DD")
            return 1

    try:
        # Initialize analyzer
        print(f"📊 Loading feedback from: {args.feedback_dir}")
        analyzer = FeedbackAnalyzer(args.feedback_dir, args.config)

        # Load and analyze feedback
        feedback = analyzer.load_feedback(since_date)
        print(f"✅ Loaded {len(feedback)} feedback entries")

        if len(feedback) == 0:
            print("❌ No feedback data found. Feedback files should be named 'feedback-*.jsonl'")
            return 1

        print("📈 Computing statistics...")
        stats = analyzer.compute_basic_stats(feedback)

        if 'error' in stats:
            print(f"❌ {stats['error']}")
            return 1

        print(f"🤖 Analyzing patterns with Claude...")
        analysis = analyzer.analyze_with_llm(stats, feedback)

        print("📝 Generating report...")
        report = analyzer.generate_report(stats, analysis, args.output)

        print(f"\n✅ Analysis complete!")
        print(f"📄 Full report: {args.output}")

        return 0

    except FileNotFoundError as e:
        print(f"❌ {e}")
        return 1
    except Exception as e:
        print(f"❌ Error: {e}")
        import traceback
        traceback.print_exc()
        return 1


if __name__ == '__main__':
    exit(main())
