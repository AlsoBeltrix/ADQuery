# User Feedback System - Implementation Status

## ✅ Completed

### Backend API (C#)

#### Models
- **QueryFeedback.cs** - Feedback data model with sentiment tracking
- **FeedbackSentiment** enum - Positive/Negative/Neutral
- **SubmitFeedbackRequest** - API request model
- **RetryWithOpusRequest** - Model retry request model

#### Services
- **IFeedbackStore** - Interface for feedback persistence
- **JsonLinesFeedbackStore** - Thread-safe JSONL file storage
  - Monthly file rotation (feedback-YYYY-MM.jsonl)
  - Stored in `wwwroot/metrics/`
  - Append-only for data integrity

#### API Endpoints
- **POST /api/query/feedback** - Submit user feedback
- **POST /api/query/retry-with-opus** - Retry query with Opus model
- **IQueryJobManager.EnqueueJobAsync()** - Support for model override

#### Configuration
- Registered JsonLinesFeedbackStore as singleton
- Feedback automatically written to metrics directory

### Analysis Tool (Python)

#### Standalone Tool (`tools/`)
- **analyze_feedback.py** - Main analysis script
- **config.example.json** - Template for Portkey/Vertex AI credentials
- **requirements.txt** - Python dependencies (anthropic SDK)
- **README.md** - Complete usage documentation
- **.gitignore** - Protects API keys from git

#### Features
- Loads feedback from JSONL files
- Computes statistics (satisfaction rates, model performance, retry rates)
- Uses Claude Opus for pattern recognition and recommendations
- Generates markdown reports with actionable insights
- Supports date filtering and custom output paths
- Cost: ~$0.30 per analysis run

## 🚧 Remaining: Frontend UI

### What Needs to Be Implemented

#### 1. Feedback UI Component
Location: `wwwroot/js/app.js` and `wwwroot/index.html`

**After Results Display**:
```html
<div id="feedback-section" style="display:none;">
    <h3>Were these results helpful?</h3>
    <button class="btn-positive" onclick="submitFeedback('positive')">
        👍 Yes, this is helpful
    </button>
    <button class="btn-negative" onclick="submitFeedback('negative')">
        👎 No, not helpful
    </button>
</div>
```

#### 2. Negative Feedback Options
**Show after thumbs down**:
```html
<div id="negative-options" style="display:none;">
    <button onclick="retryWithOpus()">
        ♦️ Try again with more powerful model (Opus)
    </button>
    <textarea id="feedback-comment"
              placeholder="What was wrong? (optional)"></textarea>
    <button onclick="submitComment()">Submit Feedback</button>
</div>
```

#### 3. JavaScript Functions

```javascript
// Track current query metadata
let currentJobId = null;
let currentModel = null;
let currentQuery = null;
let originalJobId = null;

// Submit feedback
async function submitFeedback(sentiment) {
    await fetch('/api/query/feedback', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            jobId: currentJobId,
            query: currentQuery,
            modelUsed: currentModel,
            sentiment: sentiment,
            resultCount: currentResultCount,
            responseTimeMs: currentResponseTime
        })
    });

    if (sentiment === 'positive') {
        showMessage('✅ Thanks for your feedback!');
        hideFeedback();
    } else {
        showNegativeOptions();
    }
}

// Retry with Opus
async function retryWithOpus() {
    showLoading('♦️ Regenerating with Opus...');

    // Log negative feedback first
    await fetch('/api/query/feedback', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            jobId: currentJobId,
            query: currentQuery,
            modelUsed: currentModel,
            sentiment: 'negative',
            userRequestedRetry: true
        })
    });

    // Retry with Opus
    const response = await fetch('/api/query/retry-with-opus', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            originalJobId: currentJobId
        })
    });

    const result = await response.json();

    if (result.success) {
        originalJobId = currentJobId;
        currentJobId = result.job_id;
        currentModel = 'claude-opus-4-1';

        // Poll for new results
        pollJobStatus(result.job_id);
    }
}

// Submit comment
async function submitComment() {
    const comment = document.getElementById('feedback-comment').value;

    await fetch('/api/query/feedback', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            jobId: currentJobId,
            query: currentQuery,
            modelUsed: currentModel,
            sentiment: 'negative',
            comment: comment,
            originalJobId: originalJobId
        })
    });

    showMessage('✅ Thanks for your feedback!');
    hideFeedback();
}
```

#### 4. CSS Styling
Location: `wwwroot/css/styles.css`

```css
.feedback-section {
    margin-top: 30px;
    padding: 20px;
    background: #f8f9fa;
    border-radius: 8px;
}

.btn-feedback {
    padding: 12px 24px;
    margin: 0 10px;
    border: none;
    border-radius: 6px;
    font-size: 16px;
    cursor: pointer;
    transition: all 0.2s;
}

.btn-positive {
    background: #28a745;
    color: white;
}

.btn-positive:hover {
    background: #218838;
    transform: translateY(-2px);
}

.btn-negative {
    background: #dc3545;
    color: white;
}

.btn-negative:hover {
    background: #c82333;
    transform: translateY(-2px);
}

.btn-retry {
    display: block;
    width: 100%;
    padding: 15px;
    margin: 15px 0;
    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    color: white;
    border: none;
    border-radius: 8px;
    font-size: 16px;
    cursor: pointer;
}

.btn-retry:hover {
    transform: translateY(-2px);
    box-shadow: 0 4px 12px rgba(102, 126, 234, 0.4);
}

#feedback-comment {
    width: 100%;
    padding: 12px;
    margin: 15px 0;
    border: 1px solid #ced4da;
    border-radius: 6px;
    font-family: inherit;
    resize: vertical;
}
```

#### 5. Integration Points

**When Query Completes Successfully**:
1. Show results
2. Show feedback section
3. Set metadata (jobId, model, query, resultCount, responseTime)

**When User Downloads Results**:
- Optional: Auto-submit positive feedback (or just track download as implicit positive signal)

## 📊 Data Flow

```
User submits query
    ↓
Results displayed
    ↓
Feedback UI shown
    ↓
User clicks 👍 or 👎
    ↓
If 👍: Log positive feedback → Hide UI → Done
    ↓
If 👎: Show options (Retry with Opus / Comment)
    ↓
If Retry: Log negative → Call retry endpoint → Poll for new results → Show feedback UI again
    ↓
If Comment: Log negative with comment → Hide UI → Done
```

## 🔧 Testing Checklist

### Backend (Already Works)
- ✅ Build succeeds
- ✅ Feedback endpoints registered
- ✅ JSONL storage configured
- ✅ Model override support added

### Frontend (To Test)
- ⬜ Feedback UI appears after query results
- ⬜ Thumbs up/down buttons work
- ⬜ Positive feedback saves and hides UI
- ⬜ Negative feedback shows retry options
- ⬜ "Try with Opus" creates new job
- ⬜ Opus retry polls and shows new results
- ⬜ Comment submission saves feedback
- ⬜ Feedback files created in wwwroot/metrics/

### Analysis Tool (To Test)
- ⬜ Create tools/config.json with API key
- ⬜ Run `python analyze_feedback.py`
- ⬜ Verify report generation
- ⬜ Check Claude analysis quality

## 📁 Files Modified/Created

### C# Backend
- ✅ `Models/QueryFeedback.cs` - NEW
- ✅ `Services/IFeedbackStore.cs` - NEW
- ✅ `Services/JsonLinesFeedbackStore.cs` - NEW
- ✅ `Services/IQueryJobManager.cs` - MODIFIED (added EnqueueJobAsync)
- ✅ `Services/QueryJobManager.cs` - MODIFIED (implemented EnqueueJobAsync)
- ✅ `Controllers/QueryController.cs` - MODIFIED (added 2 endpoints)
- ✅ `Program.cs` - MODIFIED (registered FeedbackStore)

### Python Analysis Tool
- ✅ `tools/analyze_feedback.py` - NEW
- ✅ `tools/requirements.txt` - NEW
- ✅ `tools/config.example.json` - NEW
- ✅ `tools/.gitignore` - NEW
- ✅ `tools/README.md` - NEW

### Frontend (TODO)
- ⬜ `wwwroot/index.html` - ADD feedback UI
- ⬜ `wwwroot/js/app.js` - ADD feedback functions
- ⬜ `wwwroot/css/styles.css` - ADD feedback styles

## 🎯 Next Steps

1. **Implement Frontend UI**
   - Add feedback HTML to results display
   - Implement JavaScript functions
   - Add CSS styling
   - Test complete flow

2. **Configure Analysis Tool**
   - Copy config.example.json to config.json
   - Add Portkey API credentials
   - Test with sample feedback data

3. **Production Deployment**
   - Test feedback workflow end-to-end
   - Run first analysis after 50-100 queries
   - Review recommendations and implement improvements
   - Schedule monthly analysis runs

## 💡 Usage After Implementation

### For End Users
1. Submit query
2. Review results
3. Click thumbs up if good, thumbs down if not
4. If not satisfied, try Opus model
5. Optionally provide comment explaining issue

### For Developers
1. Let feedback accumulate (~50-100 queries minimum)
2. Run analysis tool: `python tools/analyze_feedback.py`
3. Review generated report in `tools/reports/`
4. Implement top recommended improvements
5. Re-run analysis monthly to track improvements

## 📈 Expected Insights from Analysis

The Claude-powered analysis tool will identify:

- **Query Patterns**: Which types of queries consistently fail/succeed
- **Model Performance**: Does Opus actually improve results for certain queries?
- **Prompt Issues**: Misunderstandings in how the system interprets queries
- **UX Problems**: Common user frustrations (too much data, wrong format, etc.)
- **Action Items**: Prioritized list of specific improvements to implement

Example recommendation:
> "Recursive queries with 'entire org' fail 78% of time with Sonnet but only 12% with Opus.
> Recommend: Auto-suggest high accuracy mode checkbox when detecting keywords:
> ['entire org', 'complete hierarchy', 'everyone under']"
