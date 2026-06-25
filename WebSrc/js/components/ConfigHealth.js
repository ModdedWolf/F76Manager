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
    return `<li class="${cls}"><i data-lucide="${icon}"></i> ${window._t(labelKey)}${extra}</li>`;
}

export class ConfigHealth {
    render(data) {
        const health = data?.configHealth || {};
        const conflictCount = typeof health.conflictCount === 'number'
            ? health.conflictCount
            : ((data && typeof data.conflictsCount !== 'undefined') ? data.conflictsCount : 0);

        const isReady = !!health.overallReady;
        const color = isReady ? 'var(--success-green)' : 'var(--warning-yellow)';
        const statusText = isReady
            ? window._t('health_no_conflicts')
            : (conflictCount > 0
                ? window._t('widget_conflict_count', conflictCount)
                : window._t('health_status_needs_attention'));
        const statusLabel = isReady ? window._t('health_status_ready') : window._t('health_status_needs_attention');

        const ini = health.iniVerified || { status: 'warn' };
        const mods = health.modFilesPresent || { status: 'warn' };
        const profile = health.profileSynced || { status: 'warn' };
        const deploy = health.deployState || { status: 'warn', state: 'unknown' };

        const missingExtra = mods.missingCount > 0 ? ` (${mods.missingCount})` : '';
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
