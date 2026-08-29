const PLUGIN_ID = '9f1c2a3e-6d54-4b07-8e21-5c9a7d3b1f80';

const FIELDS = {
    checkbox: [
        'EnableScheduledTask', 'OverwriteExisting', 'AllowArchives',
        'EnableFramerateCorrection', 'AllowPiecewiseOnDemand', 'AllowPiecewiseScheduled',
        'EnableAudioFallback', 'DetectReferenceBias',
        'ShowClientNotifications', 'WriteActivityLog', 'UseSeriesPreference',
        'RemoveSupersededSidecars', 'StampProvenance'
    ],
    number: [
        'MinCorrelation', 'MinOnsetCorrelation', 'MinPeakRatio', 'MaxOffsetSeconds', 'MaxCandidatesToTry',
        'MinCorrectionSeconds',
        'RetryDeclinedAfterDays', 'KaraokePolicy',
        'MaxEpisodesPerRun', 'OnlySweepEpisodesAddedWithinDays',
        'SeriesPreferenceMinConfirmations', 'SeriesEntryCacheHours'
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
        const host = view.querySelector('#LibraryIds');
        const selected = new Set(selectedIds || []);
        host.innerHTML = folders
            .map(f => `<label class="checkboxContainer">`
                + `<input is="emby-checkbox" type="checkbox" class="libraryCheck" `
                + `data-id="${escapeHtml(f.ItemId)}"${selected.has(f.ItemId) ? ' checked' : ''} />`
                + `<span>${escapeHtml(f.Name)}</span></label>`)
            .join('');
    });
}

function saveConfig(view) {
    Dashboard.showLoadingMsg();
    return ApiClient.getPluginConfiguration(PLUGIN_ID).then(config => {
        FIELDS.text.forEach(id => { config[id] = view.querySelector('#' + id).value.trim(); });
        FIELDS.number.forEach(id => { config[id] = Number(view.querySelector('#' + id).value); });
        FIELDS.checkbox.forEach(id => { config[id] = view.querySelector('#' + id).checked; });

        config.LibraryIds = Array.from(view.querySelectorAll('#LibraryIds .libraryCheck'))
            .filter(c => c.checked)
            .map(c => c.dataset.id);

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
            <strong style="margin-left:0.75em;">${escapeHtml(seriesName)}</strong>
            <button is="emby-button" type="button" class="raised jimaku-sweep-parent"
                    data-id="${seriesId}" data-label="${escapeHtml(seriesName)}" style="margin-left:0.75em;">
                <span>Fetch for the whole series</span></button>
            </div>
            <div id="SeriesPreference" data-series="${seriesId}" class="fieldDescription"
                 style="margin-bottom:0.5em;"></div>
            <label class="checkboxContainer" style="margin-bottom:0.5em;">
                <input is="emby-checkbox" type="checkbox" id="SweepReplaceExisting" />
                <span>Replace subtitles that are already there</span>
            </label>`;

        let season = null;
        for (const item of result.Items) {
            if (item.ParentIndexNumber !== season) {
                season = item.ParentIndexNumber;
                const label = `${seriesName} season ${season ?? '?'}`;
                html += `<div style="margin:0.75em 0 0.25em;font-weight:600;">Season ${season ?? '?'}
                    ${item.SeasonId ? `<button is="emby-button" type="button" class="raised jimaku-sweep-parent"
                        data-id="${item.SeasonId}" data-label="${escapeHtml(label)}" style="margin-left:0.5em;font-weight:400;">
                        <span>Fetch this season</span></button>` : ''}</div>`;
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
                <button is="emby-button" type="button" class="raised jimaku-history" data-id="${item.Id}">
                    <span>What's attached?</span></button>
                <button is="emby-button" type="button" class="raised jimaku-reference" data-id="${item.Id}">
                    <span>What's it comparing to?</span></button>
            </div>`;
        }

        container.innerHTML = html;
        loadPreference(view, seriesId);
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
        const rejectedMark = c.PreviouslyRejected
            ? '<span title="you rejected this file for this episode; it is skipped automatically but can still be picked" '
              + 'style="opacity:0.75;">[rejected] </span>' : '';

        const notes = c.EntryNotes
            ? `<div style="opacity:0.75;font-size:0.9em;">${escapeHtml(c.EntryNotes)}</div>` : '';
        const unverified = c.EntryUnverified
            ? '<span title="this entry is flagged unverified on Jimaku" style="opacity:0.75;">[unverified] </span>' : '';

        // Measured numbers appear only for candidates that were actually downloaded and checked.
        const timing = c.Verdict
            ? `<div><strong>${escapeHtml(c.Verdict)}</strong>` +
              ` &middot; r ${Number(c.Correlation).toFixed(2)}` +
              ` &middot; uniqueness ${Number(c.PeakRatio).toFixed(2)}` +
              (c.Coverage != null
                  ? ` &middot; covers ${(c.Coverage * 100).toFixed(0)}% of dialogue`
                  : ' &middot; coverage not measurable against this reference') +
              (c.OnScreenRatio != null ? ` &middot; on screen ${(c.OnScreenRatio * 100).toFixed(0)}%` : '') +
              (c.Correction && c.Correction !== 'unchanged' ? ` &middot; ${escapeHtml(c.Correction)}` : '') +
              `</div><div style="opacity:0.75;font-size:0.9em;">${escapeHtml(c.TimingNotes || '')}</div>`
            : '<span style="opacity:0.6;">not measured</span>';

        return `<tr style="vertical-align:top;">
            <td style="padding:0.25em 0.75em 0.25em 0;">${rejectedMark}${unverified}${escapeHtml(c.FileName)}${notes}</td>
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

let sweepTimer = null;

function renderPreference(view, pref) {
    const host = view.querySelector('#SeriesPreference');
    if (!host) { return; }

    if (!pref.ReleaseGroup) {
        host.innerHTML = 'No preferred release group yet for this series. Pick the same group by hand '
            + `${pref.Required} time(s) and later episodes will lean towards it.`;
        return;
    }

    host.innerHTML = `Preferred release group: <strong>${escapeHtml(pref.ReleaseGroup)}</strong> `
        + `(${pref.Confirmations} of ${pref.Required} needed &mdash; ${pref.InUse ? 'in use' : 'not used yet'}). `
        + `<button is="emby-button" type="button" class="raised jimaku-reset-preference"
                  data-id="${host.dataset.series}" style="margin-left:0.5em;">
             <span>Forget it</span></button>`;
}

function loadPreference(view, seriesId) {
    return ApiClient.ajax({
        type: 'GET',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Series/${seriesId}/Preference`),
        dataType: 'json'
    }).then(pref => renderPreference(view, pref)).catch(() => {});
}

function renderSweep(view, status) {
    const host = view.querySelector('#SweepStatus');

    if (!status.IsRunning && !status.Total) {
        host.innerHTML = '<p class="fieldDescription">No sweep has run since the server started.</p>';
        return;
    }

    const pct = status.Total ? Math.round(100 * status.Completed / status.Total) : 0;

    let html = `<div style="margin-bottom:0.5em;">
        <strong>${status.IsRunning ? 'Running' : 'Finished'}</strong>
        &middot; ${escapeHtml(status.Scope)}
        &middot; ${status.Completed} of ${status.Total} (${pct}%)
        <div style="background:rgba(255,255,255,0.15);height:0.5em;border-radius:0.25em;margin:0.4em 0;">
            <div style="background:#00a4dc;height:100%;border-radius:0.25em;width:${pct}%;"></div>
        </div>
        <div>${status.Applied} attached &middot; ${status.Declined} declined &middot; ${status.Skipped} skipped</div>`;

    if (status.IsRunning) {
        html += `<div style="margin-top:0.3em;">Working on: <strong>${escapeHtml(status.CurrentEpisode || '…')}</strong></div>
            <button is="emby-button" type="button" class="raised jimaku-sweep-cancel" style="margin-top:0.5em;">
                <span>Stop the sweep</span></button>`;
    } else if (status.Conclusion) {
        html += `<div style="margin-top:0.3em;">${escapeHtml(status.Conclusion)}</div>`;
    }

    html += '</div>';

    const outcomes = status.Outcomes || [];
    if (outcomes.length) {
        html += '<table style="width:100%;border-collapse:collapse;"><tbody>'
             + outcomes.map(o => `<tr style="vertical-align:top;${o.Applied ? '' : 'opacity:0.7;'}">
                    <td style="padding:0.2em 0.75em 0.2em 0;">${o.Applied ? '&#10003;' : '&ndash;'}</td>
                    <td style="padding:0.2em 0.75em 0.2em 0;">${escapeHtml(o.Name)}</td>
                    <td style="padding:0.2em 0;">${escapeHtml(o.FileName || o.Message)}</td>
                  </tr>`).join('')
             + '</tbody></table>';
    }

    host.innerHTML = html;
}

function pollSweep(view, force) {
    return ApiClient.ajax({
        type: 'GET',
        url: ApiClient.getUrl('Jellyfin.Plugin.Jimaku/Sweep/Status'),
        dataType: 'json'
    }).then(status => {
        renderSweep(view, status);

        clearTimeout(sweepTimer);
        if (status.IsRunning || force) {
            sweepTimer = setTimeout(() => pollSweep(view, false), 2000);
        }
        return status;
    }).catch(() => { clearTimeout(sweepTimer); });
}

function startSweep(view, body, label) {
    const host = view.querySelector('#SweepStatus');
    host.innerHTML = '<p class="fieldDescription">Starting ' + escapeHtml(label) + '…</p>';

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('Jellyfin.Plugin.Jimaku/Sweep'),
        data: JSON.stringify(body),
        contentType: 'application/json',
        dataType: 'json'
    }).then(status => {
        renderSweep(view, status);
        clearTimeout(sweepTimer);
        sweepTimer = setTimeout(() => pollSweep(view, true), 1000);
    }).catch(err => describeError(err).then(text => {
        host.innerHTML = '<strong>Could not start</strong><div>' + escapeHtml(text) + '</div>';
    }));
}

const STATUS_TEXT = {
    Applied: 'attached now',
    Superseded: 'replaced by a later one',
    Rejected: 'rejected',
    Declined: 'not written'
};

function renderHistory(view, itemId, history) {
    const container = view.querySelector('#HistoryResults');

    const attempts = history.Attempts || [];
    if (!attempts.length && !(history.SidecarsOnDisk || []).length) {
        container.innerHTML = '<p class="fieldDescription">Nothing has been attached to this episode yet.</p>';
        return;
    }

    let html = '';

    if (history.Current) {
        const c = history.Current;
        html += `<div style="margin-bottom:0.5em;">
            <strong>Currently attached:</strong> ${escapeHtml(c.FileName)}
            ${c.ReleaseGroup ? ' <span style="opacity:0.75;">(' + escapeHtml(c.ReleaseGroup) + ')</span>' : ''}
            <div style="opacity:0.8;">${escapeHtml(c.Verdict)} &middot; ${escapeHtml(c.Correction)} &middot; entry ${c.EntryId}</div>
            <button is="emby-button" type="button" class="raised jimaku-reject" data-id="${itemId}"
                    style="margin-top:0.4em;"
                    title="Delete this subtitle, stop offering it for this episode, and take back the credit it gave this series' preferred group">
                <span>This one is bad &mdash; reject it</span></button>
        </div>`;
    }

    if (attempts.length) {
        html += '<table style="width:100%;border-collapse:collapse;"><thead><tr style="text-align:left;">'
             + '<th style="padding-right:0.75em;">Tried</th><th style="padding:0 0.75em;">File</th>'
             + '<th style="padding:0 0.75em;">Outcome</th></tr></thead><tbody>'
             + attempts.map(a => `<tr style="vertical-align:top;${a.Status === 'Rejected' ? 'opacity:0.6;' : ''}">
                    <td style="padding:0.25em 0.75em 0.25em 0;white-space:nowrap;">${escapeHtml((a.AttemptedUtc || '').slice(0, 10))}</td>
                    <td style="padding:0.25em 0.75em;">${escapeHtml(a.FileName || '—')}</td>
                    <td style="padding:0.25em 0.75em;">${escapeHtml(STATUS_TEXT[a.Status] || a.Status)}
                        <div style="opacity:0.75;font-size:0.9em;">${escapeHtml(a.Reason || '')}</div></td>
                  </tr>`).join('')
             + '</tbody></table>';
    }

    const disk = history.SidecarsOnDisk || [];
    if (disk.length) {
        html += '<div class="fieldDescription" style="margin-top:0.5em;">On disk: '
             + disk.map(d => '<code>' + escapeHtml(d.split(/[\\/]/).pop()) + '</code>').join(', ') + '</div>';
    }

    if ((history.RejectedFileNames || []).length) {
        html += `<div class="fieldDescription" style="margin-top:0.5em;">
            Skipping ${history.RejectedFileNames.length} rejected file(s) when choosing automatically.
            <button is="emby-button" type="button" class="raised jimaku-clear-rejections" data-id="${itemId}"
                    style="margin-left:0.5em;"><span>Consider them again</span></button></div>`;
    }

    container.innerHTML = html;
}

function renderReference(view, report) {
    const host = view.querySelector('#ReferenceResults');

    let html = `<div style="margin-bottom:0.5em;"><strong>Compared against:</strong> `
        + `${escapeHtml(report.Chosen || 'nothing usable')}`
        + (report.FromSubtitles ? '' : ' <span style="opacity:0.75;">(weaker than an embedded subtitle track)</span>')
        + '</div>';

    if (report.Note) {
        html += `<div class="fieldDescription" style="margin-bottom:0.5em;">${escapeHtml(report.Note)}</div>`;
    }

    const streams = report.Streams || [];
    if (streams.length) {
        html += '<table style="width:100%;border-collapse:collapse;"><thead><tr style="text-align:left;">'
             + '<th style="padding-right:0.75em;">Track</th><th style="padding:0 0.75em;">Codec</th>'
             + '<th style="padding:0 0.75em;">Cues</th><th style="padding:0 0.75em;">Used for timing</th>'
             + '</tr></thead><tbody>'
             + streams.map(t => `<tr style="vertical-align:top;${t.Used ? 'font-weight:600;' : 'opacity:0.75;'}">
                    <td style="padding:0.2em 0.75em 0.2em 0;">#${t.Index} ${escapeHtml(t.Language)}
                        ${t.Title ? '<span style="opacity:0.8;"> &middot; ' + escapeHtml(t.Title) + '</span>' : ''}
                        ${t.IsForced ? ' <span style="opacity:0.8;">[forced]</span>' : ''}</td>
                    <td style="padding:0.2em 0.75em;">${escapeHtml(t.Codec)}</td>
                    <td style="padding:0.2em 0.75em;">${t.CueCount || '—'}</td>
                    <td style="padding:0.2em 0.75em;">${escapeHtml(t.Status)}</td>
                  </tr>`).join('')
             + '</tbody></table>';
    } else {
        html += '<p class="fieldDescription">This file carries no embedded subtitle tracks.</p>';
    }

    host.innerHTML = html;
}

function loadReference(view, itemId) {
    const host = view.querySelector('#ReferenceResults');
    host.innerHTML = '<p class="fieldDescription">Reading the media\u2019s tracks\u2026</p>';

    return ApiClient.ajax({
        type: 'GET',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Episodes/${itemId}/Reference`),
        dataType: 'json'
    }).then(report => renderReference(view, report))
      .catch(err => showError(view, err));
}

function loadHistory(view, itemId) {
    return ApiClient.ajax({
        type: 'GET',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Episodes/${itemId}/History`),
        dataType: 'json'
    }).then(history => renderHistory(view, itemId, history))
      .catch(() => { view.querySelector('#HistoryResults').innerHTML = ''; });
}

function rejectCurrent(view, itemId, url, label) {
    const status = view.querySelector('#ActionStatus');
    status.textContent = label;

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl(url),
        dataType: 'json'
    }).then(history => {
        status.textContent = 'Done.';
        renderHistory(view, itemId, history);
        view.querySelector('#CandidateResults').innerHTML = '';
    }).catch(err => showError(view, err));
}

function runAuto(view, itemId) {
    const status = view.querySelector('#ActionStatus');
    status.textContent = 'Searching, downloading and verifying… this can take a minute if the audio has to be analysed.';
    view.querySelector('#CandidateResults').innerHTML = '';

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Episodes/${itemId}/Auto`),
        dataType: 'json'
    }).then(result => {
        renderResult(view, result);
        if (!result.Applied) { loadReference(view, itemId); }
        return loadHistory(view, itemId);
    })
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
        return loadHistory(view, itemId);
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
    }).then(result => { renderResult(view, result); return loadHistory(view, button.dataset.id); })
      .catch(err => showError(view, err));
}

export default function (view) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        loadConfig(view)
            .then(config => populateLibraries(view, config.LibraryIds))
            .finally(() => Dashboard.hideLoadingMsg());

        // A sweep started from Dashboard - Scheduled Tasks reports here too, so pick it up on open.
        pollSweep(view, false);
    });

    view.addEventListener('viewhide', function () {
        clearTimeout(sweepTimer);
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

        const resetPreference = e.target.closest('.jimaku-reset-preference');
        if (resetPreference) {
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl(`Jellyfin.Plugin.Jimaku/Series/${resetPreference.dataset.id}/ResetPreference`),
                dataType: 'json'
            }).then(pref => renderPreference(view, pref));
            return;
        }

        const sweepParent = e.target.closest('.jimaku-sweep-parent');
        if (sweepParent) {
            const replace = view.querySelector('#SweepReplaceExisting');
            startSweep(
                view,
                {
                    ParentId: sweepParent.dataset.id,
                    OnlyMissingSubtitles: !(replace && replace.checked),
                    RespectHistory: false
                },
                sweepParent.dataset.label);
            return;
        }

        const sweepCancel = e.target.closest('.jimaku-sweep-cancel');
        if (sweepCancel) {
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Jellyfin.Plugin.Jimaku/Sweep/Cancel'),
                dataType: 'json'
            }).then(status => renderSweep(view, status));
            return;
        }

        const auto = e.target.closest('.jimaku-auto');
        if (auto) { runAuto(view, auto.dataset.id); return; }

        const reject = e.target.closest('.jimaku-reject');
        if (reject) {
            rejectCurrent(
                view,
                reject.dataset.id,
                `Jellyfin.Plugin.Jimaku/Episodes/${reject.dataset.id}/Reject`,
                'Removing it…');
            return;
        }

        const clear = e.target.closest('.jimaku-clear-rejections');
        if (clear) {
            rejectCurrent(
                view,
                clear.dataset.id,
                `Jellyfin.Plugin.Jimaku/Episodes/${clear.dataset.id}/ClearRejections`,
                'Clearing…');
            return;
        }

        const showReference = e.target.closest('.jimaku-reference');
        if (showReference) {
            view.querySelector('#CandidateResults').innerHTML = '';
            loadReference(view, showReference.dataset.id);
            return;
        }

        const showHistory = e.target.closest('.jimaku-history');
        if (showHistory) {
            view.querySelector('#CandidateResults').innerHTML = '';
            loadHistory(view, showHistory.dataset.id);
            return;
        }

        const list = e.target.closest('.jimaku-list');
        if (list) { listCandidates(view, list.dataset.id); return; }

        const apply = e.target.closest('.jimaku-apply');
        if (apply) { applyCandidate(view, apply); }
    });
}
