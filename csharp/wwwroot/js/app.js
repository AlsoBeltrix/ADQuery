(() => {
    // The floating chat is the sole query input (F02). If it is absent, there is
    // nothing to wire, so bail early (theme-only pages, tests of fragments).
    const chatPanel = document.getElementById('chat');
    if (!chatPanel) {
        return;
    }

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

    // F01 Slice C3 — floating chat surface. See initChat() for wiring.
    // chatPanel is resolved above (the sole-input guard).
    const chatLog = document.getElementById('chatLog');
    const chatForm = document.getElementById('chatForm');
    const chatInput = document.getElementById('chatInput');
    const chatSend = document.getElementById('chatSend');
    const chatReset = document.getElementById('chatReset');
    const chatMinimize = document.getElementById('chatMinimize');
    const chatResize = document.getElementById('chatResize');
    const chatRefine = chatForm ? chatForm.querySelector('.refine') : null;

    const THEMES = {
        DARK: 'dark',
        LIGHT: 'light'
    };
    const THEME_KEY = 'adquery-theme';

    const state = {
        isLoading: false,
        formLocked: false,
        currentRequestId: null,
        currentQuery: null,
        currentJobId: null,
        // F01 Slice C2 (FOLLOWUP-D2): the last completed job id, kept separate from the
        // in-flight currentJobId that hideResults/runQuery clear each run. A follow-up
        // sends only this id as previousJobId; the server resolves it (ownership-checked)
        // into the bounded last-turn context. No client-side context material is sent.
        lastCompletedJobId: null,
        pollInterval: null,
        recordCount: 0,
        summaryRowCount: 20,  // Default, will be loaded from config API
        defaultModelId: 'claude-sonnet-4',
        defaultModelDisplayName: 'claude-sonnet-4',
        alternateModelId: 'claude-opus-4',
        alternateModelDisplayName: 'claude-opus-4'
    };

    initTheme();

    downloadButtons.forEach(button => {
        button.addEventListener('click', () => {
            if (!state.currentJobId || state.isLoading) {
                return;
            }
            downloadResults(button);
        });
    });

    themeToggle?.addEventListener('click', handleThemeToggle);

    initChat();

    loadUserInfo();
    loadConfig();
    setLoading(false);

    /**
     * Derives a friendly display name from a model ID.
     * E.g., '@bedrock-global/us.anthropic.claude-sonnet-4-5-20250929-v1:0' -> 'claude-sonnet-4-5'
     */
    function deriveModelDisplayName(modelId) {
        if (!modelId || typeof modelId !== 'string') {
            return state.defaultModelDisplayName || 'claude-sonnet-4';
        }

        let trimmed = modelId.trim().replace(/^@/, '');

        // Remove version suffix (e.g., @20250805 or -v1:0)
        let withoutVersion = trimmed.replace(/@\d+$/, '').replace(/-v\d+:\d+$/, '');

        // Get the last path segment
        const lastSlash = withoutVersion.lastIndexOf('/');
        let baseName = lastSlash >= 0 ? withoutVersion.substring(lastSlash + 1) : withoutVersion;

        // Remove common prefixes
        const prefixes = ['anthropic.', 'us.anthropic.', 'eu.anthropic.'];
        for (const prefix of prefixes) {
            if (baseName.toLowerCase().startsWith(prefix)) {
                baseName = baseName.substring(prefix.length);
                break;
            }
        }

        // Remove date suffix (e.g., -20250929)
        baseName = baseName.replace(/-\d{8}$/, '');

        return baseName || state.defaultModelDisplayName || 'claude-sonnet-4';
    }

    async function loadConfig() {
        try {
            const response = await fetch('./api/query/config', { credentials: 'include' });
            if (response.ok) {
                const config = await response.json();
                if (config.summaryRowCount > 0) {
                    state.summaryRowCount = config.summaryRowCount;
                }
                if (config.defaultModelDisplayName) {
                    state.defaultModelDisplayName = config.defaultModelDisplayName;
                }
                if (config.defaultModelId) {
                    state.defaultModelId = config.defaultModelId;
                }
                if (config.alternateModelDisplayName) {
                    state.alternateModelDisplayName = config.alternateModelDisplayName;
                }
                if (config.alternateModelId) {
                    state.alternateModelId = config.alternateModelId;
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
        // The chat composer is the sole input; enable/disable it in lock-step
        // with query state so an in-flight or access-denied session can't submit.
        if (chatInput) {
            chatInput.disabled = !shouldEnable;
        }
        if (chatSend) {
            chatSend.disabled = !shouldEnable;
        }
    }

    function setLoading(isLoading) {
        state.isLoading = isLoading;

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
        const current = document.documentElement.getAttribute('data-theme') === THEMES.LIGHT
            ? THEMES.LIGHT
            : THEMES.DARK;
        const next = current === THEMES.DARK ? THEMES.LIGHT : THEMES.DARK;
        applyTheme(next);
    }

    function applyTheme(theme) {
        // F01 design contract: the theme lives on html[data-theme].
        const resolved = theme === THEMES.LIGHT ? THEMES.LIGHT : THEMES.DARK;
        document.documentElement.setAttribute('data-theme', resolved);
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

    async function runQuery(queryText) {
        const query = (queryText || '').trim();
        if (!query) {
            showError('Please enter a query.');
            return;
        }
        state.currentQuery = query;

        hideError();
        hideResults();
        setLoading(true);
        stopPolling();

        try {
            // FOLLOWUP-D2: send only a reference to the prior completed turn; the server
            // assembles and byte-bounds the last-turn context from it. No client-built
            // context (transcript, prior values) is transmitted.
            const payload = { query };
            if (state.lastCompletedJobId) {
                payload.previousJobId = state.lastCompletedJobId;
            }

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
        // A completed job becomes the last turn a follow-up can reference. Set after the
        // in-flight reset (hideResults clears currentJobId) so it survives the next run.
        state.lastCompletedJobId = job.jobId;
        state.recordCount = job.result.totalRows || 0;

        renderAnswer(job.result.answer);
        renderHeadline(job.result.headline);
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
        const modelDisplayName = job.modelUsed
            ? deriveModelDisplayName(job.modelUsed)
            : state.defaultModelDisplayName;

        showFeedback(
            job.jobId,
            job.query || '',
            modelDisplayName,
            job.result.totalRows || 0,
            job.responseTimeMs || 0
        );

        // F01 Slice C3: settle the chat's pending answer (if this query came from
        // the chat) and refresh the "refining last question" affordance now that a
        // completed turn exists to follow up on.
        resolveChatAnswer(job);
    }

    /**
     * F04 Slice 3: leads the main window with the model's answer (the Slice 2
     * Narrate string carried on the status DTO). Absent whenever Narrate failed,
     * was skipped, or the job predates F04 — the block then stays hidden and the
     * F01 headline leads the panel exactly as it did before.
     */
    function renderAnswer(answer) {
        const container = document.getElementById('answer');
        if (!container) {
            return;
        }

        container.innerHTML = '';
        container.hidden = true;

        const text = typeof answer === 'string' ? answer.trim() : '';
        if (text.length === 0) {
            return;
        }

        appendBlockLabel(container, 'Answer');

        const prose = document.createElement('div');
        prose.className = 'prose';
        prose.textContent = text;
        container.appendChild(prose);

        container.hidden = false;
    }

    /**
     * F02 Slice 3: renders the plain-language headline answer (the B1
     * server-side contract) as a mockup `.block` card leading the result panel,
     * per kind (artifacts/mockups/qa-ui.html). It never replaces the
     * authoritative download or the full data table beneath.
     *
     * Kinds (see Models/HeadlineResult.cs / HeadlineKind):
     *   - "count"   → .block.value  (.v big number + .ctx context line)
     *   - "record"  → .block.person (.who name + .kv grid)
     *   - "grouped" → .block        (.count total + table.data breakdown)
     *   - "none"/absent → the headline block stays hidden
     */
    function renderHeadline(headline) {
        const container = document.getElementById('headline');
        if (!container) {
            return;
        }

        container.innerHTML = '';
        container.hidden = true;
        // Reset to the base block; each kind adds its own modifier class.
        container.className = 'block';

        const kind = headline && typeof headline.kind === 'string' ? headline.kind : 'none';

        if (kind === 'count') {
            renderHeadlineCount(container, headline);
        } else if (kind === 'record') {
            renderHeadlineRecord(container, headline);
        } else if (kind === 'grouped') {
            renderHeadlineGrouped(container, headline);
        } else {
            // "none" or an unknown kind: no value payload to lead with.
            return;
        }

        container.hidden = false;
    }

    function appendBlockLabel(container, text, idx) {
        const label = document.createElement('div');
        label.className = 'block-label';
        const main = document.createElement('span');
        main.textContent = text;
        label.appendChild(main);
        if (idx) {
            const idxEl = document.createElement('span');
            idxEl.className = 'idx';
            idxEl.textContent = idx;
            label.appendChild(idxEl);
        }
        container.appendChild(label);
    }

    function renderHeadlineCount(container, headline) {
        container.classList.add('value');
        const count = typeof headline.count === 'number' ? headline.count : 0;
        appendBlockLabel(container, count === 1 ? 'Match found' : 'Matches found');

        const value = document.createElement('div');
        value.className = 'v';
        value.textContent = count.toLocaleString();
        container.appendChild(value);

        const ctx = document.createElement('span');
        ctx.className = 'ctx';
        const label = count === 1 ? 'record' : 'records';
        ctx.textContent = `${count.toLocaleString()} matching ${label}`;
        container.appendChild(ctx);
    }

    function renderHeadlineRecord(container, headline) {
        container.classList.add('person');
        const record = headline.record && typeof headline.record === 'object'
            ? headline.record
            : {};
        const entries = Object.entries(record);

        appendBlockLabel(container, 'Single match', '1 record');

        // The record's display name leads; the remaining fields form the grid.
        const nameKey = pickRecordNameKey(record);
        if (nameKey) {
            const name = document.createElement('div');
            name.className = 'who';
            name.textContent = formatCellValue(record[nameKey]);
            container.appendChild(name);
        }

        const grid = document.createElement('div');
        grid.className = 'kv';
        entries
            .filter(([key]) => key !== nameKey)
            .forEach(([key, value]) => {
                const k = document.createElement('div');
                k.className = 'k';
                k.textContent = formatColumnName(key);
                const v = document.createElement('div');
                v.textContent = formatCellValue(value);
                grid.appendChild(k);
                grid.appendChild(v);
            });
        container.appendChild(grid);
    }

    function pickRecordNameKey(record) {
        const preferred = ['displayName', 'name', 'cn', 'DisplayName', 'Name', 'CN'];
        for (const key of preferred) {
            if (key in record && record[key] !== null && record[key] !== undefined
                && String(record[key]).trim().length > 0) {
                return key;
            }
        }
        return null;
    }

    function renderHeadlineGrouped(container, headline) {
        const groups = Array.isArray(headline.groups) ? headline.groups : [];
        const total = typeof headline.count === 'number' ? headline.count : null;

        appendBlockLabel(container, 'Grouped matches', 'agg');

        const countEl = document.createElement('div');
        countEl.className = 'count';
        countEl.textContent = total !== null ? total.toLocaleString() : String(groups.length);
        const small = document.createElement('small');
        small.textContent = total === 1 ? 'matching record' : 'matching records';
        countEl.appendChild(small);
        container.appendChild(countEl);

        const table = document.createElement('table');
        table.className = 'data';
        const thead = document.createElement('thead');
        const headRow = document.createElement('tr');
        ['Group', 'Count'].forEach(text => {
            const th = document.createElement('th');
            th.textContent = text;
            headRow.appendChild(th);
        });
        thead.appendChild(headRow);
        table.appendChild(thead);

        const tbody = document.createElement('tbody');
        groups.forEach(group => {
            const tr = document.createElement('tr');
            const keyCell = document.createElement('td');
            const rawKey = group && typeof group.key === 'string' ? group.key : '';
            keyCell.textContent = rawKey.length > 0 ? rawKey : '(empty)';
            const countCell = document.createElement('td');
            countCell.className = 'mono';
            const rawCount = group && typeof group.count === 'number' ? group.count : 0;
            countCell.textContent = rawCount.toLocaleString();
            tr.appendChild(keyCell);
            tr.appendChild(countCell);
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        container.appendChild(table);
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
        // Case-folded buckets that merged more than one spelling; absent when there are
        // none, so the column only appears where it says something.
        const spellings = aggregation.grouped_spellings || null;
        const hasSpellings = spellings && Object.keys(spellings).length > 0;
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
        if (hasSpellings) {
            const spellingHeader = document.createElement('th');
            spellingHeader.textContent = 'Spellings';
            headerRow.appendChild(spellingHeader);
        }
        aggregationHead.appendChild(headerRow);

        // Determine if we have multi-field grouping
        const isMultiField = groupByFields.length > 1;

        entriesToShow.forEach(([key, count]) => {
            const row = document.createElement('tr');

            if (isMultiField) {
                // Split the composite key (escape-aware — see Services/GroupKey.cs)
                const keyParts = decomposeGroupKey(key, groupByFields.length);
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

            if (hasSpellings) {
                const spellingCell = document.createElement('td');
                spellingCell.textContent = (spellings[key] || 1).toLocaleString();
                spellingCell.style.textAlign = 'right';
                row.appendChild(spellingCell);
            }

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

    // Mirror of Services/GroupKey.Decompose. Composite group keys join their per-field
    // components with '|' and escape any '|' or '\' inside a value, so an unescaped split
    // would shift every column after a value that contains the delimiter. Keep in step
    // with the server-side encoding; single-field keys are never escaped.
    function decomposeGroupKey(key, fieldCount) {
        if (fieldCount <= 1) {
            return [key];
        }

        const components = [];
        let current = '';
        let escaped = false;

        for (const ch of key) {
            if (escaped) {
                current += ch;
                escaped = false;
            } else if (ch === '\\') {
                escaped = true;
            } else if (ch === '|' && components.length < fieldCount - 1) {
                components.push(current);
                current = '';
            } else {
                current += ch;
            }
        }

        if (escaped) {
            current += '\\';
        }

        components.push(current);

        while (components.length < fieldCount) {
            components.push('');
        }

        return components;
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
                return 'xlsx';
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
        // F01 Slice C3: settle any in-flight chat answer with the error text.
        failChatAnswer(message);
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

    // ==================== F01 SLICE C3 — FLOATING CHAT ====================
    /*
     * The chat drives initial queries and follow-ups through the SAME request
     * path as the main form (runQuery → execute-async). It keeps a display-only
     * log of past exchanges (FOLLOWUP-D2): that log lives only in the DOM, is
     * never transmitted, and is cleared on reload. The ONLY thing a follow-up
     * sends is C2's previousJobId (state.lastCompletedJobId), wired in runQuery.
     */
    const chatState = {
        // The current exchange's answer bubble, awaiting the job result. Non-null
        // only while a chat-originated query is in flight.
        pendingAnswer: null,
        // The most recently settled answer bubble. An alternate-model retry re-answers
        // the question that turn already asked, so the replacement job has to land
        // there — see reopenLastChatAnswerForRetry().
        lastAnswer: null
    };

    function initChat() {
        if (!chatPanel || !chatForm || !chatInput) {
            return;
        }

        chatForm.addEventListener('submit', event => {
            event.preventDefault();
            submitChatQuery();
        });
        chatReset?.addEventListener('click', resetChatConversation);
        chatMinimize?.addEventListener('click', toggleChatMinimized);
        initChatResize();
        updateChatRefineVisibility();
    }

    function submitChatQuery() {
        const query = chatInput.value.trim();
        if (!query || state.isLoading) {
            return;
        }

        // Drive the shared query path; the response resolves in the main panel.
        appendChatExchange(query);
        chatInput.value = '';
        runQuery(query);
    }

    function appendChatExchange(query) {
        if (!chatLog) {
            return;
        }

        // Demote prior exchanges to the dimmed "past" display state. This is a
        // visual distinction only; nothing about past turns is transmitted.
        chatLog.querySelectorAll('.exchange.current').forEach(el => {
            el.classList.remove('current');
            el.classList.add('past');
        });

        const exchange = document.createElement('div');
        exchange.className = 'exchange current';

        const you = document.createElement('div');
        you.className = 'turn you';
        you.textContent = query;

        const rule = document.createElement('hr');
        rule.className = 'qa-rule';

        const bot = document.createElement('div');
        bot.className = 'turn bot pending';
        bot.textContent = 'Searching…';

        exchange.appendChild(you);
        exchange.appendChild(rule);
        exchange.appendChild(bot);
        chatLog.appendChild(exchange);
        chatLog.scrollTop = chatLog.scrollHeight;

        chatState.pendingAnswer = bot;
        updateChatRefineVisibility();
    }

    function resolveChatAnswer(job) {
        if (!chatState.pendingAnswer) {
            return;
        }
        chatState.pendingAnswer.classList.remove('pending');
        chatState.pendingAnswer.textContent = summariseJobForChat(job);
        chatState.lastAnswer = chatState.pendingAnswer;
        chatState.pendingAnswer = null;
        updateChatRefineVisibility();
        if (chatLog) {
            chatLog.scrollTop = chatLog.scrollHeight;
        }
    }

    /**
     * F04 finding slice3-or-1: an alternate-model retry replaces the answer to the
     * question the last turn already asked, so it must land in that turn's bubble
     * rather than nowhere. Re-arms the settled bubble as pending; the replacement
     * job then settles it through the ordinary resolveChatAnswer path, keeping the
     * chat and the main panel on the same job.
     */
    function reopenLastChatAnswerForRetry() {
        if (chatState.pendingAnswer || !chatState.lastAnswer) {
            return;
        }
        chatState.pendingAnswer = chatState.lastAnswer;
        chatState.lastAnswer = null;
        chatState.pendingAnswer.classList.add('pending');
        chatState.pendingAnswer.textContent = 'Trying another model…';
        updateChatRefineVisibility();
    }

    function failChatAnswer(message) {
        if (!chatState.pendingAnswer) {
            return;
        }
        chatState.pendingAnswer.classList.remove('pending');
        chatState.pendingAnswer.textContent = message || 'Something went wrong. See the result panel.';
        chatState.pendingAnswer = null;
        // A failed turn is not a retry target: nothing answered it.
        chatState.lastAnswer = null;
        updateChatRefineVisibility();
    }

    // F04 Slice 3: the chat bubble carries the model's answer (Slice 2 Narrate).
    // The code-templated headline summary below survives only as the fallback for
    // a job that has no answer — Narrate failed, was skipped, or the job predates
    // F04. The result panel remains authoritative for the detail either way.
    function summariseJobForChat(job) {
        const result = job && job.result ? job.result : null;

        const answer = result && typeof result.answer === 'string' ? result.answer.trim() : '';
        if (answer.length > 0) {
            return answer;
        }

        const headline = result ? result.headline : null;
        const kind = headline && typeof headline.kind === 'string' ? headline.kind : 'none';

        if (kind === 'count') {
            const count = typeof headline.count === 'number' ? headline.count : 0;
            return count === 1
                ? '1 match. See the result panel.'
                : `${count.toLocaleString()} matches. See the result panel.`;
        }
        if (kind === 'record') {
            const record = headline.record && typeof headline.record === 'object' ? headline.record : {};
            const nameKey = pickRecordNameKey(record);
            const name = nameKey ? formatCellValue(record[nameKey]) : 'One record';
            return `${name} — details in the result panel.`;
        }
        if (kind === 'grouped') {
            const total = typeof headline.count === 'number' ? headline.count : null;
            return total !== null
                ? `${total.toLocaleString()} matches, grouped. See the result panel.`
                : 'Grouped matches. See the result panel.';
        }

        const total = result && typeof result.totalRows === 'number' ? result.totalRows : null;
        if (total !== null) {
            return total === 1
                ? '1 record. See the result panel.'
                : `${total.toLocaleString()} records. See the result panel.`;
        }
        return 'Done. See the result panel.';
    }

    function resetChatConversation() {
        // "Start over": end the follow-up chain so the next turn sends no
        // previousJobId, and clear the display-only history. Nothing here was ever
        // transmitted (FOLLOWUP-D2), so this is purely local.
        state.lastCompletedJobId = null;
        chatState.pendingAnswer = null;
        chatState.lastAnswer = null;
        if (chatLog) {
            chatLog.innerHTML = '';
        }
        updateChatRefineVisibility();
    }

    function updateChatRefineVisibility() {
        // "Refining last question" and the follow-up placeholder both only make
        // sense once a prior turn exists to refine (state.lastCompletedJobId is
        // what C2 would send as previousJobId). Before the first answer, the
        // placeholder invites an opening question instead.
        const hasPriorTurn = Boolean(state.lastCompletedJobId);
        if (chatInput) {
            const prompt = hasPriorTurn ? 'Ask a follow-up…' : 'Ask about the directory…';
            chatInput.placeholder = prompt;
            chatInput.setAttribute('aria-label', prompt);
        }
        if (chatRefine) {
            chatRefine.classList.toggle('hidden', !hasPriorTurn);
        }
    }

    function toggleChatMinimized() {
        chatPanel?.classList.toggle('minimized');
    }

    function initChatResize() {
        if (!chatResize || !chatPanel) {
            return;
        }

        let startX = 0;
        let startY = 0;
        let startW = 0;
        let startH = 0;
        let resizing = false;

        const onMove = event => {
            if (!resizing) {
                return;
            }
            // Handle is top-left; the panel is anchored bottom-right, so dragging
            // up/left grows it. Clamp to the Design contract: width ≤ 50vw,
            // height ≤ 100vh, floored at the min-width/min-height.
            const dx = startX - event.clientX;
            const dy = startY - event.clientY;
            const maxW = window.innerWidth * 0.5;
            const maxH = window.innerHeight;
            const w = Math.min(maxW, Math.max(300, startW + dx));
            const h = Math.min(maxH, Math.max(260, startH + dy));
            chatPanel.style.width = `${w}px`;
            chatPanel.style.height = `${h}px`;
        };

        const onUp = () => {
            resizing = false;
            window.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', onUp);
        };

        chatResize.addEventListener('pointerdown', event => {
            event.preventDefault();
            resizing = true;
            startX = event.clientX;
            startY = event.clientY;
            const rect = chatPanel.getBoundingClientRect();
            startW = rect.width;
            startH = rect.height;
            window.addEventListener('pointermove', onMove);
            window.addEventListener('pointerup', onUp);
        });
    }

    // ==================== END F01 SLICE C3 ====================

    // ==================== FEEDBACK SYSTEM ====================

    // Feedback state
    const feedbackState = {
        currentJobId: null,
        currentQuery: null,
        currentModel: null,
        originalJobId: null,
        resultCount: 0,
        responseTimeMs: 0,
        alternateModelDisplayName: null  // Will be loaded from config
    };

    // Make feedback functions global for onclick handlers
    window.submitFeedback = async function(sentiment) {
        const feedbackSection = document.getElementById('feedbackSection');
        const negativeOptions = document.getElementById('negativeOptions');

        try {
            if (sentiment === 'Positive') {
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
        const alternateModelLabel = state.alternateModelDisplayName || feedbackState.alternateModelDisplayName || 'alternate model';
        const progressMessage = `Regenerating results with ${alternateModelLabel}...`;

        setLoading(true);
        showProgress(progressMessage);
        hideError();

        try {
            // Log negative feedback first
            await saveFeedback({
                jobId: feedbackState.currentJobId || state.currentJobId,
                query: feedbackState.currentQuery,
                modelUsed: feedbackState.currentModel || 'claude-sonnet-4',
                sentiment: 'Negative',
                userRequestedRetry: true,
                resultCount: feedbackState.resultCount,
                responseTimeMs: feedbackState.responseTimeMs
            });

            if (negativeOptions) {
                negativeOptions.hidden = true;
            }
            if (retryStatus) {
                retryStatus.hidden = false;
            }

            const response = await fetch('./api/query/retry-with-alternate-model', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                credentials: 'include',
                cache: 'no-store',
                body: JSON.stringify({
                    originalJobId: feedbackState.currentJobId || state.currentJobId
                })
            });

            if (!response.ok) {
                const errorText = await response.text().catch(() => '<no body>');
                console.error('Retry endpoint returned non-OK status', response.status, errorText);
                throw new Error(`HTTP error ${response.status}`);
            }

            const result = await response.json();

            if (result.success && result.job_id) {
                // Store original job ID for tracking
                feedbackState.originalJobId = feedbackState.currentJobId || state.currentJobId;
                feedbackState.currentJobId = result.job_id;
                feedbackState.alternateModelDisplayName = alternateModelLabel;
                feedbackState.currentModel = alternateModelLabel;

                hideFeedback();
                hideResults();
                showProgress(progressMessage);

                // Update state to new job
                state.currentJobId = result.job_id;

                // The conversation must follow the replacement job too, or the chat
                // keeps presenting the answer the user just rejected.
                reopenLastChatAnswerForRetry();

                // Start polling the new job ID returned by the server
                startPolling(result.job_id);
            } else {
                console.error('Retry endpoint response did not indicate success', result);
                throw new Error(result.error || 'Failed to retry query');
            }
        } catch (error) {
            console.error('Failed to retry with alternate model:', error);
            setLoading(false);
            if (retryStatus) {
                retryStatus.hidden = true;
            }
            if (negativeOptions) {
                negativeOptions.hidden = false;
            }
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
                sentiment: 'Negative',
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
        const response = await fetch('./api/query/feedback', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            credentials: 'include',
            cache: 'no-store',
            body: JSON.stringify(feedbackData)
        });

        if (!response.ok) {
            const errorText = await response.text().catch(() => '<no body>');
            console.error('Feedback API returned non-OK status', response.status, errorText);
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
        feedbackState.alternateModelDisplayName = state.alternateModelDisplayName || feedbackState.alternateModelDisplayName;

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

