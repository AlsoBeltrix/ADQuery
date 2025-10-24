(() => {
    const form = document.getElementById('queryForm');
    if (!form) {
        return;
    }

    const queryInput = document.getElementById('queryText');
    const submitButton = document.getElementById('searchBtn');
    const resultsSection = document.getElementById('results');
    const resultInfo = document.getElementById('resultsInfo');
    const tableHead = document.getElementById('tableHead');
    const tableBody = document.getElementById('tableBody');
    const warningList = document.getElementById('warningList');
    const downloadSection = document.getElementById('downloadSection');
    const downloadButtons = Array.from(document.querySelectorAll('[data-download-format]'));
    const downloadStatus = document.getElementById('downloadStatus');
    const errorSection = document.getElementById('error');
    const errorMessage = document.getElementById('errorMessage');
    const welcomeMessage = document.getElementById('welcomeMessage');
    const themeToggle = document.getElementById('themeToggle');

    const THEMES = {
        DARK: 'dark',
        LIGHT: 'light'
    };
    const THEME_KEY = 'adquery-theme';

    const state = {
        isLoading: false,
        formLocked: false,
        currentRequestId: null,
        currentJobId: null,
        pollInterval: null,
        recordCount: 0,
        summaryRowCount: 20  // Default, will be loaded from config API
    };

    initTheme();

    form.addEventListener('submit', event => {
        event.preventDefault();
        runQuery();
    });

    downloadButtons.forEach(button => {
        button.addEventListener('click', () => {
            if (!state.currentJobId || state.isLoading) {
                return;
            }
            downloadResults(button);
        });
    });

    themeToggle?.addEventListener('click', handleThemeToggle);

    loadUserInfo();
    loadConfig();
    setLoading(false);

    async function loadConfig() {
        try {
            const response = await fetch('./api/query/config');
            if (response.ok) {
                const config = await response.json();
                if (config.summaryRowCount > 0) {
                    state.summaryRowCount = config.summaryRowCount;
                }
            }
        } catch (error) {
            console.warn('Failed to load config, using defaults:', error);
        }
    }

    async function loadUserInfo() {
        if (!welcomeMessage) {
            return;
        }

        try {
            const response = await fetch('./api/user/info', { credentials: 'include' });
            if (!response.ok) {
                throw new Error(`Failed to load user info (${response.status})`);
            }

            const info = await response.json();
            if (info && info.isAuthenticated) {
                const name = info.username && info.username.trim().length > 0 ? info.username : 'user';
                welcomeMessage.textContent = `Welcome, ${name}`;
                welcomeMessage.classList.remove('banner-warning', 'banner-danger');
                welcomeMessage.classList.add('banner-success');
            } else {
                welcomeMessage.textContent = 'Access denied - you are not authorized to run queries.';
                welcomeMessage.classList.remove('banner-success');
                welcomeMessage.classList.add('banner-warning');
                disableForm();
            }
        } catch (error) {
            welcomeMessage.textContent = 'Unable to verify access - refresh the page and try again.';
            welcomeMessage.classList.remove('banner-success');
            welcomeMessage.classList.add('banner-warning');
            console.warn('User info check failed:', error);
        }
    }

    function disableForm() {
        state.formLocked = true;
        toggleFormEnabled(false);
    }

    function toggleFormEnabled(enabled) {
        const shouldEnable = enabled && !state.formLocked;
        if (queryInput) {
            queryInput.disabled = !shouldEnable;
        }
        if (submitButton) {
            submitButton.disabled = !shouldEnable;
        }
    }

    function setLoading(isLoading) {
        state.isLoading = isLoading;

        const btnText = submitButton?.querySelector('.btn-text');
        const btnLoading = submitButton?.querySelector('.btn-loading');

        if (btnText) {
            btnText.hidden = isLoading;
        }
        if (btnLoading) {
            btnLoading.hidden = !isLoading;
        }

        toggleFormEnabled(!isLoading);
        updateDownloadButtons();
    }

    function initTheme() {
        const stored = getStoredTheme();
        const prefersLight = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches;
        const initial = stored === THEMES.LIGHT || stored === THEMES.DARK
            ? stored
            : prefersLight ? THEMES.LIGHT : THEMES.DARK;

        applyTheme(initial);
    }

    function handleThemeToggle() {
        const current = document.body.classList.contains(`theme-${THEMES.LIGHT}`) ? THEMES.LIGHT : THEMES.DARK;
        const next = current === THEMES.DARK ? THEMES.LIGHT : THEMES.DARK;
        applyTheme(next);
    }

    function applyTheme(theme) {
        const body = document.body;
        if (!body) {
            return;
        }

        const resolved = theme === THEMES.LIGHT ? THEMES.LIGHT : THEMES.DARK;
        body.classList.remove(`theme-${THEMES.DARK}`, `theme-${THEMES.LIGHT}`);
        body.classList.add(`theme-${resolved}`);
        setStoredTheme(resolved);
        updateThemeToggleLabel(resolved);
    }

    function updateThemeToggleLabel(currentTheme) {
        if (!themeToggle) {
            return;
        }

        const nextTheme = currentTheme === THEMES.LIGHT ? 'Dark' : 'Light';
        themeToggle.textContent = `${nextTheme} theme`;
        themeToggle.setAttribute('aria-label', `Switch to ${nextTheme.toLowerCase()} theme`);
        themeToggle.setAttribute('title', `Switch to ${nextTheme.toLowerCase()} theme`);
        themeToggle.setAttribute('aria-pressed', currentTheme === THEMES.DARK ? 'true' : 'false');
    }

    function getStoredTheme() {
        try {
            return localStorage.getItem(THEME_KEY);
        } catch (error) {
            console.warn('Unable to read stored theme preference:', error);
            return null;
        }
    }

    function setStoredTheme(theme) {
        try {
            localStorage.setItem(THEME_KEY, theme);
        } catch (error) {
            console.warn('Unable to persist theme preference:', error);
        }
    }

    async function runQuery() {
        if (!queryInput) {
            return;
        }

        const query = queryInput.value.trim();
        if (!query) {
            showError('Please enter a query.');
            return;
        }

        hideError();
        hideResults();
        setLoading(true);
        stopPolling();

        try {
            const payload = {
                query,
                context: buildContextHint(query)
            };

            const response = await fetch('./api/query/execute-async', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'include',
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                const result = await response.json().catch(() => null);
                const message = result?.error || result?.errorMessage || `Request failed with status ${response.status}.`;
                handleCriticalError(message);
                setLoading(false);
                return;
            }

            const result = await response.json();
            state.currentJobId = result.jobId;

            showProgress('Query submitted. Processing...');
            startPolling(result.jobId);
        } catch (error) {
            handleCriticalError(error instanceof Error ? error.message : 'Network error - please try again.');
            setLoading(false);
        }
    }

    function stopPolling() {
        if (state.pollInterval) {
            clearInterval(state.pollInterval);
            state.pollInterval = null;
        }
    }

    function startPolling(jobId) {
        stopPolling();

        state.pollInterval = setInterval(async () => {
            try {
                const response = await fetch(`./api/query/jobs/${encodeURIComponent(jobId)}`, {
                    method: 'GET',
                    credentials: 'include'
                });

                if (!response.ok) {
                    stopPolling();
                    setLoading(false);
                    showError(`Failed to check job status: ${response.status}`);
                    return;
                }

                const job = await response.json();

                switch (job.status) {
                    case 'queued':
                        showProgress('Query queued, waiting to start...');
                        break;

                    case 'running':
                        if (job.progress) {
                            const phase = job.progress.phase || '';
                            const pct = job.progress.percentComplete || 0;
                            const nodes = (job.progress.nodesProcessed || 0).toLocaleString();
                            const est = job.progress.estimatedTotal ? job.progress.estimatedTotal.toLocaleString() : '?';
                            const depth = job.progress.currentDepth || 0;

                            if (phase === 'generating-plan') {
                                showProgress('Generating query plan with AI...');
                            } else if (phase === 'validating') {
                                showProgress('Validating query plan...');
                            } else if (phase === 'executing' || phase === 'starting') {
                                showProgress('Starting query execution...');
                            } else if (phase && phase.startsWith('enumerating-level')) {
                                showProgress(`Processing level ${depth}... ${nodes} of ~${est} nodes (${pct}%)`);
                            } else if (phase === 'aggregation') {
                                showProgress(`Computing aggregation summaries...`);
                            } else if (phase === 'finalizing') {
                                showProgress(`Finalizing results...`);
                            } else if (depth > 0) {
                                showProgress(`Processing level ${depth}... ${nodes} of ~${est} nodes (${pct}%)`);
                            } else {
                                showProgress('Processing query...');
                            }
                        } else {
                            showProgress('Processing query...');
                        }
                        break;

                    case 'completed':
                        stopPolling();
                        setLoading(false);
                        hideProgress();
                        displayJobResults(job);
                        break;

                    case 'failed':
                        stopPolling();
                        setLoading(false);
                        hideProgress();
                        showError(job.error || 'Query failed');
                        break;

                    case 'cancelled':
                        stopPolling();
                        setLoading(false);
                        hideProgress();
                        showError('Query was cancelled');
                        break;
                }
            } catch (error) {
                stopPolling();
                setLoading(false);
                hideProgress();
                showError('Failed to check job status: ' + (error instanceof Error ? error.message : 'Unknown error'));
            }
        }, 2000);
    }

    function showProgress(message) {
        if (resultInfo) {
            resultInfo.textContent = message;
            resultInfo.style.fontWeight = 'bold';
        }
        if (resultsSection) {
            resultsSection.hidden = false;
        }
    }

    function hideProgress() {
        if (resultInfo) {
            resultInfo.style.fontWeight = 'normal';
        }
    }

    async function displayJobResults(job) {
        if (!job.result) {
            showError('No results available');
            return;
        }

        state.currentJobId = job.jobId;
        state.recordCount = job.result.totalRows || 0;

        renderWarnings(job.result.warnings);
        renderAggregation(job.result.aggregation);

        // Fetch preview rows
        try {
            const previewResponse = await fetch(`./api/query/jobs/${encodeURIComponent(job.jobId)}/preview`, {
                method: 'GET',
                credentials: 'include'
            });

            if (previewResponse.ok) {
                const preview = await previewResponse.json();
                const rows = normaliseRows(preview.rows);
                renderTable(rows);

                const mockResult = {
                    success: true,
                    data: preview.rows,
                    recordCount: job.result.totalRows || 0,
                    warnings: job.result.warnings || []
                };
                renderSummary(mockResult, rows.length);
            } else {
                renderSummary({ recordCount: job.result.totalRows || 0 }, 0);
            }
        } catch (error) {
            console.warn('Failed to fetch preview:', error);
            renderSummary({ recordCount: job.result.totalRows || 0 }, 0);
        }

        showDownloadOptions();

        resultsSection.hidden = false;
        resultsSection.scrollIntoView({ behavior: 'smooth', block: 'start' });

        // Show feedback UI after results are displayed
        showFeedback(
            job.jobId,
            job.query || '',
            job.modelUsed || 'claude-sonnet-4',
            job.result.totalRows || 0,
            job.responseTimeMs || 0
        );
    }

    function renderAggregation(aggregation) {
        if (!aggregation || !aggregation.grouped_counts) {
            return;
        }

        const aggregationSection = document.getElementById('aggregationSection');
        const aggregationHead = document.getElementById('aggregationHead');
        const aggregationBody = document.getElementById('aggregationBody');
        const aggregationMessage = document.getElementById('aggregationMessage');

        if (!aggregationSection || !aggregationHead || !aggregationBody) {
            return;
        }

        aggregationHead.innerHTML = '';
        aggregationBody.innerHTML = '';

        const counts = aggregation.grouped_counts;
        const entries = Object.entries(counts).sort((a, b) => b[1] - a[1]);
        const groupByFields = aggregation.group_by_fields || [];
        const totalEntries = entries.length;
        const displayLimit = state.summaryRowCount || 20;
        const entriesToShow = entries.slice(0, displayLimit);

        // Build dynamic table headers based on group_by fields
        const headerRow = document.createElement('tr');
        if (groupByFields.length > 1) {
            groupByFields.forEach(field => {
                const th = document.createElement('th');
                th.textContent = formatColumnName(field);
                headerRow.appendChild(th);
            });
        } else if (groupByFields.length === 1) {
            const th = document.createElement('th');
            th.textContent = formatColumnName(groupByFields[0]);
            headerRow.appendChild(th);
        } else {
            const th = document.createElement('th');
            th.textContent = 'Category';
            headerRow.appendChild(th);
        }
        const countHeader = document.createElement('th');
        countHeader.textContent = 'Count';
        headerRow.appendChild(countHeader);
        aggregationHead.appendChild(headerRow);

        // Determine if we have multi-field grouping
        const isMultiField = groupByFields.length > 1;

        entriesToShow.forEach(([key, count]) => {
            const row = document.createElement('tr');

            if (isMultiField) {
                // Split the composite key
                const keyParts = key.split('|');
                keyParts.forEach(part => {
                    const cell = document.createElement('td');
                    cell.textContent = part || '(empty)';
                    row.appendChild(cell);
                });
            } else {
                const keyCell = document.createElement('td');
                keyCell.textContent = key || '(empty)';
                row.appendChild(keyCell);
            }

            const countCell = document.createElement('td');
            countCell.textContent = count.toLocaleString();
            countCell.style.textAlign = 'right';
            row.appendChild(countCell);

            aggregationBody.appendChild(row);
        });

        // Update message about limited display
        if (aggregationMessage) {
            if (totalEntries > displayLimit) {
                aggregationMessage.textContent = `Showing top ${displayLimit} of ${totalEntries} categories. Download for full summary.`;
                aggregationMessage.hidden = false;
            } else {
                aggregationMessage.hidden = true;
            }
        }

        aggregationSection.hidden = false;
    }

    function handleCriticalError(message) {
        if (typeof message === 'string' && message.toLowerCase().includes('claude api key')) {
            if (welcomeMessage) {
                welcomeMessage.textContent = message;
                welcomeMessage.classList.remove('banner-success');
                welcomeMessage.classList.add('banner-danger');
            }
            disableForm();
        }

        showError(buildFriendlyError(message));
    }

    function buildFriendlyError(message) {
        if (typeof message !== 'string' || message.trim().length === 0) {
            return 'An unexpected error occurred.';
        }

        const lower = message.toLowerCase();

        if (lower.includes('not allow-listed')) {
            return 'This query uses directory attributes that are not exposed yet. Please focus the request on supported fields.';
        }

        if (lower.includes('fallback search matched') || lower.includes('too complex') || lower.includes('multi-level') || lower.includes('recursive')) {
            return 'This query is more complex than we currently support (for example, deep rollups or complex aggregations). Try narrowing the scope.';
        }

        if (lower.includes('filter value was missing')) {
            return 'The generated filter was incomplete. Try rephrasing with exact names or identifiers.';
        }

        if (lower.includes('cancelled') || lower.includes('timed out')) {
            return 'The query timed out or was cancelled. Please apply a smaller scope or limit.';
        }

        return message;
    }

    function buildContextHint(query) {
        const match = query.match(/\b(first|top)\s+(\d+)/i);
        if (!match) {
            return null;
        }

        const limit = Number.parseInt(match[2], 10);
        if (Number.isNaN(limit) || limit <= 0) {
            return null;
        }

        return `Limit results to approximately ${limit} entries.`;
    }

    function renderResults(result) {
        hideError();

        const rows = normaliseRows(result?.data);
        renderTable(rows);
        renderSummary(result, rows.length);
        renderWarnings(result?.warnings);

        resultsSection.hidden = false;
        resultsSection.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function renderSummary(result, previewCount) {
        if (!resultInfo) {
            return;
        }

        const parts = [];

        const total = typeof result?.recordCount === 'number' ? result.recordCount : undefined;
        if (typeof total === 'number') {
            if (total === 0) {
                parts.push('Aggregation summary only (no individual records)');
            } else {
                const label = total === 1 ? 'record' : 'records';
                parts.push(`${total} ${label} returned`);
            }
        } else if (previewCount > 0) {
            const label = previewCount === 1 ? 'record' : 'records';
            parts.push(`${previewCount} ${label} returned`);
        } else {
            parts.push('No records returned');
        }

        if (total !== undefined && previewCount < total && total > 0) {
            parts.push(`Previewing ${previewCount}`);
        }

        if (typeof result?.executionTimeMs === 'number' && result.executionTimeMs >= 0) {
            parts.push(`${result.executionTimeMs} ms`);
        }

        if (Array.isArray(result?.warnings) && result.warnings.length > 0) {
            parts.push(`Warnings: ${result.warnings.length}`);
        }

        resultInfo.textContent = parts.join(' | ');
    }

    function renderWarnings(warnings) {
        if (!warningList) {
            return;
        }

        warningList.innerHTML = '';

        if (!Array.isArray(warnings) || warnings.length === 0) {
            warningList.hidden = true;
            return;
        }

        warnings.forEach(warning => {
            const item = document.createElement('li');
            item.textContent = String(warning);
            warningList.appendChild(item);
        });

        warningList.hidden = false;
    }

    function renderTable(rows) {
        tableHead.innerHTML = '';
        tableBody.innerHTML = '';

        if (!rows.length) {
            // Check if there's an aggregation section visible
            const aggregationSection = document.getElementById('aggregationSection');
            if (aggregationSection && !aggregationSection.hidden) {
                // Hide the data table section entirely for aggregation-only queries
                const tableContainer = document.querySelector('.results-table-container');
                if (tableContainer) {
                    tableContainer.style.display = 'none';
                }
                return;
            }

            const row = document.createElement('tr');
            const cell = document.createElement('td');
            cell.colSpan = 100;
            cell.textContent = 'No preview data available.';
            cell.style.textAlign = 'center';
            row.appendChild(cell);
            tableBody.appendChild(row);
            return;
        }

        // Show table container if it was hidden
        const tableContainer = document.querySelector('.results-table-container');
        if (tableContainer) {
            tableContainer.style.display = 'block';
        }

        const headers = Array.from(
            new Set(
                rows.flatMap(row => (row && typeof row === 'object' ? Object.keys(row) : []))
            )
        );

        if (!headers.length) {
            const row = document.createElement('tr');
            const cell = document.createElement('td');
            cell.colSpan = 100;
            cell.textContent = typeof rows[0] === 'string' ? rows[0] : 'Results available.';
            row.appendChild(cell);
            tableBody.appendChild(row);
            return;
        }

        const headerRow = document.createElement('tr');
        headers.forEach(header => {
            const th = document.createElement('th');
            th.textContent = formatColumnName(header);
            headerRow.appendChild(th);
        });
        tableHead.appendChild(headerRow);

        rows.forEach(row => {
            const tr = document.createElement('tr');
            headers.forEach(header => {
                const td = document.createElement('td');
                const value = row?.[header];
                td.textContent = formatCellValue(value);
                tr.appendChild(td);
            });
            tableBody.appendChild(tr);
        });
    }

    function showDownloadOptions() {
        if (!downloadSection) {
            return;
        }

        if (downloadStatus) {
            const label = state.recordCount === 1 ? 'record' : 'records';
            downloadStatus.textContent = `Download full results (${state.recordCount} ${label}) as:`;
        }

        downloadSection.hidden = false;
        updateDownloadButtons();
    }

    async function downloadResults(button) {
        const format = button.dataset.downloadFormat;
        if (!format || !state.currentJobId) {
            return;
        }

        const originalLabel = button.textContent;
        button.disabled = true;
        button.textContent = 'Downloading...';

        try {
            const response = await fetch(`./api/query/download-async/${encodeURIComponent(state.currentJobId)}?format=${encodeURIComponent(format)}`, {
                method: 'GET',
                credentials: 'include'
            });

            if (!response.ok) {
                const errorText = await response.text();
                throw new Error(errorText || `Download failed with status ${response.status}.`);
            }

            const blob = await response.blob();
            const contentDisposition = response.headers.get('Content-Disposition') || '';
            const fileName = extractFileName(contentDisposition, format);

            const url = URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);
            URL.revokeObjectURL(url);
        } catch (error) {
            console.error('Download failed:', error);
            const friendly = error instanceof Error ? buildFriendlyError(error.message) : 'Unable to download results.';
            showError(friendly);
        } finally {
            button.textContent = originalLabel;
            updateDownloadButtons();
        }
    }

    function extractFileName(contentDisposition, format) {
        const match = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/.exec(contentDisposition);
        if (match && match[1]) {
            return match[1].replace(/['"]/g, '');
        }

        const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
        return `adquery-results-${format}-${timestamp}.${getExtension(format)}`;
    }

    function updateDownloadButtons() {
        const disable = state.isLoading || !state.currentJobId;
        downloadButtons.forEach(button => {
            button.disabled = disable;
        });
    }

    function normaliseRows(data) {
        if (!data) {
            return [];
        }

        if (Array.isArray(data)) {
            return data;
        }

        if (typeof data === 'object') {
            return [data];
        }

        return [];
    }

    function formatColumnName(name) {
        return name
            .replace(/([A-Z])/g, ' $1')
            .replace(/[_\-\s]+/g, ' ')
            .replace(/^./, str => str.toUpperCase())
            .trim();
    }

    function formatCellValue(value) {
        if (value === null || value === undefined) {
            return '';
        }

        if (Array.isArray(value)) {
            return value.map(item => formatCellValue(item)).join(', ');
        }

        if (value instanceof Date) {
            return value.toLocaleDateString();
        }

        if (typeof value === 'object') {
            try {
                return JSON.stringify(value);
            } catch {
                return String(value);
            }
        }

        return String(value);
    }

    function getExtension(format) {
        switch (format) {
            case 'csv':
                return 'csv';
            case 'excel':
                return 'xls';
            case 'html':
                return 'html';
            case 'text':
                return 'txt';
            default:
                return 'dat';
        }
    }

    function showError(message) {
        if (errorMessage) {
            errorMessage.textContent = message;
        }
        if (errorSection) {
            errorSection.hidden = false;
        }
        hideResults();
    }

    function hideError() {
        if (errorSection) {
            errorSection.hidden = true;
        }
    }

    function hideResults() {
        stopPolling();
        resultsSection.hidden = true;
        warningList.hidden = true;
        warningList.innerHTML = '';
        downloadSection.hidden = true;
        tableHead.innerHTML = '';
        tableBody.innerHTML = '';

        const aggregationSection = document.getElementById('aggregationSection');
        if (aggregationSection) {
            aggregationSection.hidden = true;
        }

        state.currentRequestId = null;
        state.currentJobId = null;
        state.recordCount = 0;
        updateDownloadButtons();
        hideFeedback();
    }

    // ==================== FEEDBACK SYSTEM ====================

    // Feedback state
    const feedbackState = {
        currentJobId: null,
        currentQuery: null,
        currentModel: null,
        originalJobId: null,
        resultCount: 0,
        responseTimeMs: 0
    };

    // Make feedback functions global for onclick handlers
    window.submitFeedback = async function(sentiment) {
        const feedbackSection = document.getElementById('feedbackSection');
        const negativeOptions = document.getElementById('negativeOptions');

        try {
            if (sentiment === 'positive') {
                await saveFeedback({
                    jobId: feedbackState.currentJobId || state.currentJobId,
                    query: feedbackState.currentQuery,
                    modelUsed: feedbackState.currentModel || 'claude-sonnet-4',
                    sentiment: sentiment,
                    resultCount: feedbackState.resultCount,
                    responseTimeMs: feedbackState.responseTimeMs
                });

                showMessage('✅ Thanks for your feedback!', 'success');
                hideFeedback();
            } else {
                // Show negative feedback options
                negativeOptions.hidden = false;
            }
        } catch (error) {
            console.error('Failed to submit feedback:', error);
            showMessage('Failed to save feedback. Please try again.', 'error');
        }
    };

    window.retryWithAlternateModel = async function() {
        const negativeOptions = document.getElementById('negativeOptions');
        const retryStatus = document.getElementById('retryStatus');

        try {
            // Log negative feedback first
            await saveFeedback({
                jobId: feedbackState.currentJobId || state.currentJobId,
                query: feedbackState.currentQuery,
                modelUsed: feedbackState.currentModel || 'claude-sonnet-4',
                sentiment: 'negative',
                userRequestedRetry: true,
                resultCount: feedbackState.resultCount,
                responseTimeMs: feedbackState.responseTimeMs
            });

            // Hide options, show retry status
            negativeOptions.hidden = true;
            retryStatus.hidden = false;

            // Call retry endpoint
            const response = await fetch('/api/query/retry-with-alternate-model', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    originalJobId: feedbackState.currentJobId || state.currentJobId
                })
            });

            if (!response.ok) {
                throw new Error(`HTTP error ${response.status}`);
            }

            const result = await response.json();

            if (result.success && result.job_id) {
                // Store original job ID for tracking
                feedbackState.originalJobId = feedbackState.currentJobId || state.currentJobId;
                feedbackState.currentJobId = result.job_id;
                feedbackState.currentModel = 'claude-opus-4-1';

                // Hide feedback section and start polling for new results
                hideFeedback();
                hideResults();
                hideError();

                // Update state to new job
                state.currentJobId = result.job_id;

                // Start polling
                showMessage('Query resubmitted with more powerful model...', 'info');
                startPolling();
            } else {
                throw new Error(result.error || 'Failed to retry query');
            }
        } catch (error) {
            console.error('Failed to retry with alternate model:', error);
            retryStatus.hidden = true;
            negativeOptions.hidden = false;
            showMessage('Failed to retry query. Please try again.', 'error');
        }
    };

    window.submitComment = async function() {
        const commentField = document.getElementById('feedbackComment');
        const comment = commentField.value.trim();

        try {
            await saveFeedback({
                jobId: feedbackState.currentJobId || state.currentJobId,
                query: feedbackState.currentQuery,
                modelUsed: feedbackState.currentModel || 'claude-sonnet-4',
                sentiment: 'negative',
                comment: comment || null,
                originalJobId: feedbackState.originalJobId,
                resultCount: feedbackState.resultCount,
                responseTimeMs: feedbackState.responseTimeMs
            });

            showMessage('✅ Thanks for your feedback!', 'success');
            hideFeedback();
            commentField.value = '';
        } catch (error) {
            console.error('Failed to submit comment:', error);
            showMessage('Failed to save feedback. Please try again.', 'error');
        }
    };

    window.closeFeedback = function() {
        const negativeOptions = document.getElementById('negativeOptions');
        const commentField = document.getElementById('feedbackComment');

        negativeOptions.hidden = true;
        commentField.value = '';
    };

    async function saveFeedback(feedbackData) {
        const response = await fetch('/api/query/feedback', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(feedbackData)
        });

        if (!response.ok) {
            throw new Error(`HTTP error ${response.status}`);
        }

        return await response.json();
    }

    function showFeedback(jobId, query, model, resultCount, responseTimeMs) {
        const feedbackSection = document.getElementById('feedbackSection');
        const negativeOptions = document.getElementById('negativeOptions');
        const retryStatus = document.getElementById('retryStatus');

        // Update feedback state
        feedbackState.currentJobId = jobId;
        feedbackState.currentQuery = query;
        feedbackState.currentModel = model || 'claude-sonnet-4';
        feedbackState.resultCount = resultCount || 0;
        feedbackState.responseTimeMs = responseTimeMs || 0;

        // Reset UI state
        negativeOptions.hidden = true;
        retryStatus.hidden = true;

        // Show feedback section
        feedbackSection.hidden = false;
    }

    function hideFeedback() {
        const feedbackSection = document.getElementById('feedbackSection');
        const negativeOptions = document.getElementById('negativeOptions');
        const retryStatus = document.getElementById('retryStatus');
        const commentField = document.getElementById('feedbackComment');

        feedbackSection.hidden = true;
        negativeOptions.hidden = true;
        retryStatus.hidden = true;
        if (commentField) {
            commentField.value = '';
        }
    }

    function showMessage(message, type = 'info') {
        // Simple message display - you can enhance this with a toast notification
        if (type === 'error') {
            console.error(message);
            showError(message);
        } else {
            console.log(message);
            // Could add a toast notification here
        }
    }

    // ==================== END FEEDBACK SYSTEM ====================

})();
