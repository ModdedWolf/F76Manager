
export const KEYBIND_STORAGE_KEY = 'f76_manager_keybinds';

export const DEFAULT_KEYBINDS = {
    play: 'Ctrl+Shift+P',
    deploy: 'Ctrl+Shift+D',
    apply: 'Ctrl+Shift+A',
    profileCycle: 'Ctrl+Shift+N',
    modsSearch: 'Ctrl+Shift+F'
};

export const KEYBIND_DEFINITIONS = [
    { id: 'play', labelKey: 'keybind_play' },
    { id: 'deploy', labelKey: 'keybind_deploy' },
    { id: 'apply', labelKey: 'keybind_apply' },
    { id: 'profileCycle', labelKey: 'keybind_profile_cycle' },
    { id: 'modsSearch', labelKey: 'keybind_mods_search' }
];

const MODIFIER_KEYS = new Set(['Control', 'Shift', 'Alt', 'Meta']);

export function normalizeKeybinds(source) {
    const out = { ...DEFAULT_KEYBINDS };
    if (!source || typeof source !== 'object') return out;
    for (const def of KEYBIND_DEFINITIONS) {
        const val = source[def.id];
        if (typeof val === 'string' && val.trim()) {
            out[def.id] = val.trim();
        }
    }
    return out;
}

export function getKeybinds(managerSettings) {
    let merged = normalizeKeybinds(managerSettings?.keybinds);
    try {
        const raw = localStorage.getItem(KEYBIND_STORAGE_KEY);
        if (raw) {
            merged = normalizeKeybinds({ ...merged, ...JSON.parse(raw) });
        }
    } catch (_) { }
    return merged;
}

export function saveKeybindsLocal(bindings) {
    localStorage.setItem(KEYBIND_STORAGE_KEY, JSON.stringify(bindings));
}

export function syncKeybindsFromManagerSettings(managerSettings) {
    if (!managerSettings?.keybinds) return;
    saveKeybindsLocal(normalizeKeybinds(managerSettings.keybinds));
}

export function formatChordFromEvent(e) {
    if (MODIFIER_KEYS.has(e.key)) return null;
    if (e.key === 'Escape') return null;

    const parts = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.shiftKey) parts.push('Shift');
    if (e.altKey) parts.push('Alt');
    if (e.metaKey) parts.push('Meta');

    let key = e.key;
    if (key === ' ') key = 'Space';
    else if (key === 'Delete') key = 'Del';
    else if (key === 'ArrowUp') key = 'Up';
    else if (key === 'ArrowDown') key = 'Down';
    else if (key === 'ArrowLeft') key = 'Left';
    else if (key === 'ArrowRight') key = 'Right';
    else if (key.length === 1) key = key.toUpperCase();

    if (!parts.length) return null;
    parts.push(key);
    return parts.join('+');
}

export function parseChord(chord) {
    const parts = String(chord || '').split('+').map(p => p.trim()).filter(Boolean);
    const mods = new Set(parts.slice(0, -1).map(p => p.toLowerCase()));
    const keyPart = parts[parts.length - 1] || '';
    return {
        ctrl: mods.has('ctrl') || mods.has('control'),
        shift: mods.has('shift'),
        alt: mods.has('alt'),
        meta: mods.has('meta'),
        key: keyPart.toLowerCase()
    };
}

export function eventMatchesChord(e, chord) {
    const p = parseChord(chord);
    const eventKey = e.key === ' ' ? 'space' : e.key.toLowerCase();
    const wantKey = p.key === 'space' ? 'space' : p.key;
    return e.ctrlKey === p.ctrl &&
        e.shiftKey === p.shift &&
        e.altKey === p.alt &&
        e.metaKey === p.meta &&
        eventKey === wantKey;
}

export function findKeybindConflict(bindings, id, chord) {
    const norm = chord.toLowerCase();
    for (const def of KEYBIND_DEFINITIONS) {
        if (def.id === id) continue;
        if (bindings[def.id].toLowerCase() === norm) return def.id;
    }
    return null;
}

export function executeKeybindAction(id, app) {
    switch (id) {
        case 'play':
            window.chrome?.webview?.postMessage({ type: 'LAUNCH_GAME' });
            break;
        case 'deploy':
            window.chrome?.webview?.postMessage({ type: 'DEPLOY_ALL', force: false, mods: [] });
            break;
        case 'apply':
            window.chrome?.webview?.postMessage({ type: 'APPLY_CHANGES' });
            break;
        case 'profileCycle':
            app.cycleProfile();
            break;
        case 'modsSearch':
            app.navigateTo('mods', true, false);
            requestAnimationFrame(() => document.getElementById('mods-search')?.focus());
            break;
        default:
            break;
    }
}
