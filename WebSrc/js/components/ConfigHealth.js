function healthIcon(status) {
    if (status === 'pass') return 'check';
    if (status === 'warn') return 'alert-triangle';
    return 'x';
}

function healthClass(status) {
    if (status === 'pass') return 'health-pass';
    if (status === 'warn') return 'health-warn';
    return 'health-fail';
}

function renderHealthLine(labelKey, item, extra = '') {
    const status = item?.status || 'warn';
    const icon = healthIcon(status);
    const cls = healthClass(status);
    return `<li class="${cls}"><i data-lucide="${icon}"></i> <span>${window._t(labelKey)}${extra}</span></li>`;
}

function missingModNames(mods) {
    if (!Array.isArray(mods?.missingMods)) return [];
    return mods.missingMods.map(name => String(name || '').trim()).filter(Boolean);
}

function formatBytes(n) {
    const num = Number(n) || 0;
    if (num < 1024) return `${num} B`;
    if (num < 1024 * 1024) return `${(num / 1024).toFixed(1)} KB`;
    return `${(num / (1024 * 1024)).toFixed(1)} MB`;
}

function healthSummary(health, conflictCount) {
    const parts = [];
    const mods = health.modFilesPresent || {};
    if (mods.status === 'fail') {
        const names = missingModNames(mods);
        const count = mods.missingCount || names.length || 1;
        const shown = names.slice(0, 3).join(', ');
        const key = count === 1 ? 'health_missing_summary' : 'health_missing_summary_plural';
        parts.push(shown
            ? window._t(key, count, shown)
            : window._t('health_missing_summary_count', count));
    }
    if (health.profileSynced?.status === 'warn') parts.push(window._t('health_profile_out_of_sync'));
    if (health.iniVerified?.status && health.iniVerified.status !== 'pass') parts.push(window._t('health_ini_issue'));
    if (health.deployState?.state === 'stale') parts.push(window._t('health_deploy_stale'));
    if (conflictCount > 0) parts.push(window._t('widget_conflict_count', conflictCount));
    return parts.join(' · ') || window._t('health_status_needs_attention');
}

export class ConfigHealth {
    render(data) {
        const health = data?.configHealth || {};
        const conflictCount = typeof health.conflictCount === 'number'
            ? health.conflictCount
            : ((data && typeof data.conflictsCount !== 'undefined') ? data.conflictsCount : 0);

        const isReady = !!health.overallReady;
        const color = isReady ? 'var(--success-green)' : 'var(--warning-yellow)';
        let statusText;
        if (isReady) {
            if (health.conflictsAutoOverridden && conflictCount > 0) {
                statusText = window._t('health_conflicts_auto_overridden', conflictCount)
                    || `${conflictCount} conflicts auto-overridden`;
            } else {
                statusText = window._t('health_no_conflicts');
            }
        } else {
            statusText = healthSummary(health, conflictCount);
        }
        const statusLabel = isReady ? window._t('health_status_ready') : window._t('health_status_needs_attention');

        const ini = health.iniVerified || { status: 'warn' };
        const mods = health.modFilesPresent || { status: 'warn' };
        const profile = health.profileSynced || { status: 'warn' };
        const deploy = health.deployState || { status: 'warn', state: 'unknown' };

        const missingNames = missingModNames(mods);
        const missingExtra = mods.missingCount > 0
            ? (missingNames.length ? ` (${missingNames.slice(0, 2).join(', ')})` : ` (${mods.missingCount})`)
            : '';
        let deployExtra = '';
        if (deploy.state === 'stale') {
            deployExtra = ` — ${window._t('health_deploy_stale')}`;
            if (deploy.detail) deployExtra += ` (${deploy.detail})`;
        } else if (deploy.state === 'virtual') {
            deployExtra = ` — ${window._t('health_deploy_virtual')}`;
        }

        return `
            <div class="widget">
                <div class="widget-header">
                    <i data-lucide="shield-check"></i>
                    <h3>${window._t('config_health_header')}</h3>
                </div>
                <div class="widget-content">
                    <div id="conflict-status-box" class="health-status" style="border-left-color: ${color}; cursor: ${conflictCount > 0 ? 'pointer' : 'default'}">
                        <span class="status-value" style="color:${color}">${statusLabel}</span>
                        <span class="status-text">${statusText}</span>
                    </div>
                    <ul class="health-details">
                        ${renderHealthLine('health_ini_verified', ini)}
                        ${renderHealthLine('health_mod_files_present', mods, missingExtra)}
                        ${renderHealthLine('health_profile_synced', profile)}
                        ${renderHealthLine('health_deploy_synced', deploy, deployExtra)}
                    </ul>
                </div>
            </div>
        `;
    }
}

export { formatBytes };
