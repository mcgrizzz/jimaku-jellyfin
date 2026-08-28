const PLUGIN_ID = '9f1c2a3e-6d54-4b07-8e21-5c9a7d3b1f80';

const FIELDS = {
    checkbox: [
        'EnableScheduledTask', 'OverwriteExisting', 'AllowArchives',
        'EnableFramerateCorrection', 'AllowPiecewiseOnDemand', 'AllowPiecewiseScheduled',
        'EnableAudioFallback', 'DetectReferenceBias'
    ],
    number: [
        'MinCorrelation', 'MinOnsetCorrelation', 'MinPeakRatio', 'MaxOffsetSeconds', 'MaxCandidatesToTry',
        'MinCorrectionSeconds',
        'RetryDeclinedAfterDays', 'KaraokePolicy'
    ],
    text: ['ApiKey', 'LanguageTag', 'SileroModelPath']
};

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[c]);
}

function loadConfig(view) {
    return ApiClient.getPluginConfiguration(PLUGIN_ID).then(config => {
        FIELDS.text.forEach(id => { view.querySelector('#' + id).value = config[id] ?? ''; });
        FIELDS.number.forEach(id => { view.querySelector('#' + id).value = config[id] ?? 0; });
        FIELDS.checkbox.forEach(id => { view.querySelector('#' + id).checked = !!config[id]; });
        return config;
    });
}

function populateLibraries(view, selectedIds) {
    return ApiClient.getVirtualFolders().then(folders => {
        const select = view.querySelector('#LibraryIds');
        const selected = new Set(selectedIds || []);
        select.innerHTML = folders
            .map(f => `<option value="${escapeHtml(f.ItemId)}"${selected.has(f.ItemId) ? ' selected' : ''}>${escapeHtml(f.Name)}</option>`)
            .join('');
    });
}

function saveConfig(view) {
    Dashboard.showLoadingMsg();
    return ApiClient.getPluginConfiguration(PLUGIN_ID).then(config => {
        FIELDS.text.forEach(id => { config[id] = view.querySelector('#' + id).value.trim(); });
        FIELDS.number.forEach(id => { config[id] = Number(view.querySelector('#' + id).value); });
        FIELDS.checkbox.forEach(id => { config[id] = view.querySelector('#' + id).checked; });

        config.LibraryIds = Array.from(view.querySelector('#LibraryIds').selectedOptions).map(o => o.value);

        return ApiClient.updatePluginConfiguration(PLUGIN_ID, config)
            .then(result => Dashboard.processPluginConfigurationUpdateResult(result));
    }).catch(err => {
        Dashboard.hideLoadingMsg();
        Dashboard.alert({ message: 'Could not save settings: ' + err });
    });
}

function testKey(view) {
    const target = view.querySelector('#KeyResult');
    target.textContent = 'Checking…';

    // ApiClient.ajax injects the Jellyfin auth header; never build it by hand.
    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('Jellyfin.Plugin.Jimaku/ValidateApiKey'),
        data: JSON.stringify({ ApiKey: view.querySelector('#ApiKey').value.trim() }),
        contentType: 'application/json',
        dataType: 'json'
    }).then(valid => {
        target.textContent = valid
            ? 'The key works.'
            : 'Jimaku rejected that key. Check it on your account page.';
    }).catch(() => {
        target.textContent = 'Could not reach Jimaku to check the key.';
    });
}

// Searching Episodes by SearchTerm matches the episode's own title, not the series, so a series
// name finds only the handful of episodes that happen to repeat it in their titles. Find the
// series first, then list its episodes.
function searchSeries(view, term) {
    const container = view.querySelector('#EpisodeResults');
    if (!term || term.length < 2) {
        container.innerHTML = '';
        return;
    }

    ApiClient.getItems(ApiClient.getCurrentUserId(), {
        SearchTerm: term,
        IncludeItemTypes: 'Series',
        Recursive: true,
        Limit: 20,
        SortBy: 'SortName'
    }).then(result => {
        if (!result.Items.length) {
            container.innerHTML = '<p class="fieldDescription">No series matched.</p>';
            return;
        }

        container.innerHTML = '<div class="fieldDescription">Select a series:</div>' +
            result.Items.map(item => {
                const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                return `<div style="margin:0.35em 0;">
                    <button is="emby-button" type="button" class="raised block jimaku-series"
                            data-id="${item.Id}" data-name="${escapeHtml(item.Name)}">
                        <span>${escapeHtml(item.Name)}${year}</span>
                    </button></div>`;
            }).join('');
    });
}

function loadEpisodes(view, seriesId, seriesName) {
    const container = view.querySelector('#EpisodeResults');
    container.innerHTML = '<p class="fieldDescription">Loading episodes…</p>';

    ApiClient.getItems(ApiClient.getCurrentUserId(), {
        ParentId: seriesId,
        IncludeItemTypes: 'Episode',
        Recursive: true,
        Limit: 2000,
        Fields: 'MediaStreams',
        SortBy: 'ParentIndexNumber,IndexNumber',
        SortOrder: 'Ascending'
    }).then(result => {
        if (!result.Items.length) {
            container.innerHTML = '<p class="fieldDescription">That series has no episodes.</p>';
            return;
        }

        let html = `<div style="margin-bottom:0.5em;">
            <button is="emby-button" type="button" class="raised jimaku-back"><span>&larr; Back to search</span></button>
            <strong style="margin-left:0.75em;">${escapeHtml(seriesName)}</strong></div>`;

        let season = null;
        for (const item of result.Items) {
            if (item.ParentIndexNumber !== season) {
                season = item.ParentIndexNumber;
                html += `<div style="margin:0.75em 0 0.25em;font-weight:600;">Season ${season ?? '?'}</div>`;
            }

            // Flag episodes that already have Japanese subtitles, so it is obvious which ones
            // are worth acting on.
            const hasJapanese = (item.MediaStreams || []).some(st =>
                st.Type === 'Subtitle' && (st.Language === 'jpn' || st.Language === 'ja'));

            const code = `S${String(item.ParentIndexNumber ?? 0).padStart(2, '0')}E${String(item.IndexNumber ?? 0).padStart(2, '0')}`;
            const mark = hasJapanese
                ? '<span title="already has a Japanese subtitle track" style="opacity:0.7;">[JA]</span> '
                : '';

            html += `<div style="display:flex;gap:0.5em;align-items:center;margin:0.2em 0;flex-wrap:wrap;">
                <span style="flex:1 1 20em;">${mark}<code>${code}</code> ${escapeHtml(item.Name || '')}</span>
                <button is="emby-button" type="button" class="raised jimaku-auto" data-id="${item.Id}">
                    <span>Fetch best</span></button>
                <button is="emby-button" type="button" class="raised jimaku-list" data-id="${item.Id}">
                    <span>Show candidates</span></button>
            </div>`;
        }

        container.innerHTML = html;
    }).catch(err => {
        container.innerHTML = '<p class="fieldDescription">Could not load episodes: ' + escapeHtml(err) + '</p>';
    });
}

// ApiClient.ajax rejects with a fetch Response, which stringifies to "[object Response]" and
// tells the user nothing. Pull the server's actual message out of the body.
function describeError(err) {
    if (err && typeof err.text === 'function') {
        return err.text()
            .then(body => `${err.status || ''} ${body || err.statusText || 'request failed'}`.trim())
            .catch(() => `${err.status || ''} ${err.statusText || 'request failed'}`.trim());
    }
    return Promise.resolve(String(err && err.message ? err.message : err));
}

function showError(view, err) {
    describeError(err).then(text => {
        view.querySelector('#ActionStatus').innerHTML =
            '<strong>Failed</strong><div>' + escapeHtml(text) + '</div>';
    });
}

function renderResult(view, result) {
    const status = view.querySelector('#ActionStatus');
    const verdictText = {
        Exact: 'Attached unchanged — already in sync',
        ConstantOffset: 'Attached with a constant shift',
        FramerateDrift: 'Attached with a framerate correction',
        PiecewiseCut: 'Attached, matched to a different cut',
        Declined: 'Declined — nothing was written'
    }[result.Verdict] || result.Verdict;

    const detail = result.Applied
        ? `<div>${escapeHtml(result.FileName)}</div>
           <div>Correction: ${escapeHtml(result.Correction)} &middot; correlation ${result.Correlation.toFixed(2)} &middot; uniqueness ${result.PeakRatio.toFixed(2)}</div>
           <div>Reference: ${escapeHtml(result.ReferenceSource)}</div>
           <div>Written to ${escapeHtml(result.SidecarPath)}</div>`
        : '';

    status.innerHTML = `<strong>${escapeHtml(verdictText)}</strong>
        <div>${escapeHtml(result.Message)}</div>${detail}`;

    if (result.Candidates && result.Candidates.length) {
        renderCandidates(view, result.Candidates, null);
    }
}

function renderCandidates(view, candidates, itemId) {
    const container = view.querySelector('#CandidateResults');
    if (!candidates.length) {
        container.innerHTML = '<p class="fieldDescription">Jimaku has nothing for this episode.</p>';
        return;
    }

    const rows = candidates.map(c => {
        // Any candidate the filter did not reject can be applied by hand, including one that
        // failed verification: correlation compares cue structure, so a subtitle that is timed
        // correctly but segmented differently from the reference can score poorly and still be
        // the right file. The person watching it is the better judge.
        const declined = c.Verdict === 'Declined';
        const action = (itemId && c.Usable)
            ? `<button is="emby-button" type="button" class="raised jimaku-apply"
                 data-id="${itemId}" data-entry="${c.EntryId}"
                 data-url="${escapeHtml(c.Url)}" data-file="${escapeHtml(c.FileName)}"
                 data-force="${declined ? '1' : '0'}"
                 title="${declined ? 'Write this despite failing verification' : 'Verify and write this subtitle'}">
                 <span>${declined ? 'Use anyway' : 'Use this'}</span></button>`
            : escapeHtml(c.RejectedBecause || '');

        // Entry notes frequently say which release the subtitles were timed for.
        const notes = c.EntryNotes
            ? `<div style="opacity:0.75;font-size:0.9em;">${escapeHtml(c.EntryNotes)}</div>` : '';
        const unverified = c.EntryUnverified
            ? '<span title="this entry is flagged unverified on Jimaku" style="opacity:0.75;">[unverified] </span>' : '';

        // Measured numbers appear only for candidates that were actually downloaded and checked.
        const timing = c.Verdict
            ? `<div><strong>${escapeHtml(c.Verdict)}</strong>` +
              ` &middot; r ${Number(c.Correlation).toFixed(2)}` +
              ` &middot; uniqueness ${Number(c.PeakRatio).toFixed(2)}` +
              (c.Coverage != null ? ` &middot; covers ${(c.Coverage * 100).toFixed(0)}% of dialogue` : '') +
              (c.OnScreenRatio != null ? ` &middot; on screen ${(c.OnScreenRatio * 100).toFixed(0)}%` : '') +
              (c.Correction && c.Correction !== 'unchanged' ? ` &middot; ${escapeHtml(c.Correction)}` : '') +
              `</div><div style="opacity:0.75;font-size:0.9em;">${escapeHtml(c.TimingNotes || '')}</div>`
            : '<span style="opacity:0.6;">not measured</span>';

        return `<tr style="vertical-align:top;">
            <td style="padding:0.25em 0.75em 0.25em 0;">${unverified}${escapeHtml(c.FileName)}${notes}</td>
            <td style="padding:0.25em 0.75em;">${c.NameScore}</td>
            <td style="padding:0.25em 0.75em;">${timing}</td>
            <td style="padding:0.25em 0;">${action}</td>
        </tr>`;
    }).join('');

    container.innerHTML = `<table style="width:100%;border-collapse:collapse;">
        <thead><tr style="text-align:left;">
            <th style="padding-right:0.75em;">File</th><th style="padding:0 0.75em;">Name</th>
            <th style="padding:0 0.75em;">Timing</th><th></th>
        </tr></thead><tbody>${rows}</tbody></table>
        <p class="fieldDescription" style="margin-top:0.5em;">
            The name match is only a pre-filter. Timing is verified against your media before
            anything is written.
        </p>`;
}

function runAuto(view, itemId) {
    const status = view.querySelector('#ActionStatus');
    status.textContent = 'Searching, downloading and verifying… this can take a minute if the audio has to be analysed.';
    view.querySelector('#CandidateResults').innerHTML = '';

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Episodes/${itemId}/Auto`),
        dataType: 'json'
    }).then(result => renderResult(view, result))
      .catch(err => showError(view, err));
}

function listCandidates(view, itemId) {
    const status = view.querySelector('#ActionStatus');
    status.textContent = 'Looking up Jimaku…';

    ApiClient.ajax({
        type: 'GET',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Episodes/${itemId}/Candidates`),
        dataType: 'json'
    }).then(candidates => {
        status.textContent = `${candidates.length} file(s) found.`;
        renderCandidates(view, candidates, itemId);
    }).catch(err => showError(view, err));
}

function applyCandidate(view, button) {
    const status = view.querySelector('#ActionStatus');
    status.textContent = 'Downloading and verifying…';

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Episodes/${button.dataset.id}/Apply`),
        data: JSON.stringify({
            EntryId: Number(button.dataset.entry),
            FileName: button.dataset.file,
            Url: button.dataset.url,
            ApplyEvenIfUnverified: button.dataset.force === '1'
        }),
        contentType: 'application/json',
        dataType: 'json'
    }).then(result => renderResult(view, result))
      .catch(err => showError(view, err));
}

export default function (view) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        loadConfig(view)
            .then(config => populateLibraries(view, config.LibraryIds))
            .finally(() => Dashboard.hideLoadingMsg());
    });

    view.querySelector('#JimakuConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        saveConfig(view);
        return false;
    });

    view.querySelector('#TestKey').addEventListener('click', () => testKey(view));

    let searchTimer;
    view.querySelector('#EpisodeSearch').addEventListener('input', function (e) {
        clearTimeout(searchTimer);
        const term = e.target.value.trim();
        searchTimer = setTimeout(() => searchSeries(view, term), 350);
    });

    // Delegated, because the result rows are rebuilt on every search.
    view.addEventListener('click', function (e) {
        const series = e.target.closest('.jimaku-series');
        if (series) { loadEpisodes(view, series.dataset.id, series.dataset.name); return; }

        const back = e.target.closest('.jimaku-back');
        if (back) { searchSeries(view, view.querySelector('#EpisodeSearch').value.trim()); return; }

        const auto = e.target.closest('.jimaku-auto');
        if (auto) { runAuto(view, auto.dataset.id); return; }

        const list = e.target.closest('.jimaku-list');
        if (list) { listCandidates(view, list.dataset.id); return; }

        const apply = e.target.closest('.jimaku-apply');
        if (apply) { applyCandidate(view, apply); }
    });
}
