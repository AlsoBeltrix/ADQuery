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
        recordCount: 0
    };

    initTheme();

    form.addEventListener('submit', event => {
        event.preventDefault();
        runQuery();
    });

    downloadButtons.forEach(button => {
        button.addEventListener('click', () => {
            if (!state.currentRequestId || state.isLoading) {
                return;
            }
            downloadResults(button);
        });
    });

    themeToggle?.addEventListener('click', handleThemeToggle);

    loadUserInfo();
    setLoading(false);

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

        try {
            const payload = {
                query,
                context: buildContextHint(query)
            };

            const response = await fetch('./api/query/execute', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'include',
                body: JSON.stringify(payload)
            });

            const result = await response.json().catch(() => null);

            if (!response.ok) {
                const message = result?.error || result?.errorMessage || `Request failed with status ${response.status}.`;
                handleCriticalError(message);
                return;
            }

            if (!result?.success) {
                const message = result?.error || 'The query did not return any data.';
                handleCriticalError(message);
                return;
            }

            state.currentRequestId = result.requestId;
            state.recordCount = typeof result.recordCount === 'number' ? result.recordCount : 0;
            renderResults(result);
            showDownloadOptions();
        } catch (error) {
            handleCriticalError(error instanceof Error ? error.message : 'Network error - please try again.');
        } finally {
            setLoading(false);
        }
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

        showError(message || 'An unexpected error occurred.');
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
            const label = total === 1 ? 'record' : 'records';
            parts.push(`${total} ${label} returned`);
        } else if (previewCount > 0) {
            const label = previewCount === 1 ? 'record' : 'records';
            parts.push(`${previewCount} ${label} returned`);
        } else {
            parts.push('No records returned');
        }

        if (total !== undefined && previewCount < total) {
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
            const row = document.createElement('tr');
            const cell = document.createElement('td');
            cell.colSpan = 100;
            cell.textContent = 'No preview data available.';
            cell.style.textAlign = 'center';
            row.appendChild(cell);
            tableBody.appendChild(row);
            return;
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
        if (!format || !state.currentRequestId) {
            return;
        }

        const originalLabel = button.textContent;
        button.disabled = true;
        button.textContent = 'Downloading...';

        try {
            const response = await fetch(`./api/query/download/${encodeURIComponent(state.currentRequestId)}?format=${encodeURIComponent(format)}`, {
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
            showError(error instanceof Error ? error.message : 'Unable to download results.');
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
        const disable = state.isLoading || !state.currentRequestId;
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
        resultsSection.hidden = true;
        warningList.hidden = true;
        warningList.innerHTML = '';
        downloadSection.hidden = true;
        tableHead.innerHTML = '';
        tableBody.innerHTML = '';
        state.currentRequestId = null;
        state.recordCount = 0;
        updateDownloadButtons();
    }
})();
