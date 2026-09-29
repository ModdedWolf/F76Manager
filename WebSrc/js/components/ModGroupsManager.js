export class ModPresetsManager {
    static DEFAULT_NAME = 'Default';

    constructor(app) {
        this.app = app;
        this.presets = {};
        this.activePreset = ModPresetsManager.DEFAULT_NAME;
    }

    isDefaultPreset(name) {
        return String(name || '').localeCompare(ModPresetsManager.DEFAULT_NAME, undefined, { sensitivity: 'accent' }) === 0;
    }

    setFromHost(modPresets, activeName) {
        this.presets = this._normalize(modPresets);
        if (activeName && this.presets[activeName]) {
            this.activePreset = activeName;
        } else if (!this.presets[this.activePreset]) {
            this.activePreset = this.presets[ModPresetsManager.DEFAULT_NAME]
                ? ModPresetsManager.DEFAULT_NAME
                : (Object.keys(this.presets)[0] || ModPresetsManager.DEFAULT_NAME);
        }
    }

    setGroups(groups) {
        this.setFromHost(groups, this.activePreset);
    }

    getGroups() {
        const out = {};
        for (const [name, p] of Object.entries(this.presets)) {
            out[name] = [...(p.mods || [])];
        }
        return out;
    }

    getPresets() {
        return this.presets;
    }

    getActiveName() {
        return this.activePreset;
    }

    getActive() {
        return this.presets[this.activePreset] || { mods: [], enabled: [] };
    }

    listNames() {
        return Object.keys(this.presets);
    }

    createPreset(name) {
        const trimmed = String(name || '').trim();
        if (!trimmed || this.presets[trimmed]) return false;
        this.presets[trimmed] = { mods: [], enabled: [], receiveUpdates: false };
        this.activePreset = trimmed;
        this.save();
        return true;
    }

    deletePreset(name) {
        const trimmed = String(name || '').trim();
        if (!this.presets[trimmed]) return false;
        if (this.isDefaultPreset(trimmed)) return false;
        delete this.presets[trimmed];
        if (Object.keys(this.presets).length === 0 || !this.presets[ModPresetsManager.DEFAULT_NAME]) {
            this.presets[ModPresetsManager.DEFAULT_NAME] = { mods: [], enabled: [], receiveUpdates: false };
        }
        if (this.activePreset === trimmed) {
            this.activePreset = this.presets[ModPresetsManager.DEFAULT_NAME]
                ? ModPresetsManager.DEFAULT_NAME
                : Object.keys(this.presets)[0];
        }
        this.save();
        return true;
    }

    renamePreset(oldName, newName) {
        const from = String(oldName || '').trim();
        const to = String(newName || '').trim();
        if (!from || !to || !this.presets[from] || this.presets[to]) return false;
        this.presets[to] = this.presets[from];
        delete this.presets[from];
        if (this.activePreset === from) this.activePreset = to;
        this.save();
        return true;
    }

    duplicatePreset(name) {
        const from = String(name || '').trim();
        if (!this.presets[from]) return null;
        let base = `${from} Copy`;
        let n = 2;
        let dest = base;
        while (this.presets[dest]) {
            dest = `${base} ${n++}`;
        }
        const src = this.presets[from];
        this.presets[dest] = {
            mods: [...(src.mods || [])],
            enabled: [...(src.enabled || [])],
            receiveUpdates: !!src.receiveUpdates
        };
        this.activePreset = dest;
        this.save();
        return dest;
    }

    static normalizeModKey(modName) {
        let n = String(modName || '').replace(/\\/g, '/').trim();
        if (/^Disabled\//i.test(n)) n = n.substring('Disabled/'.length);
        if (/^Strings\//i.test(n)) n = n.substring('Strings/'.length);
        return n;
    }

    _sameModKey(a, b) {
        return ModPresetsManager.normalizeModKey(a)
            .localeCompare(ModPresetsManager.normalizeModKey(b), undefined, { sensitivity: 'accent' }) === 0;
    }

    _memberKeySet(mods) {
        const set = new Set();
        for (const m of mods || []) {
            const key = ModPresetsManager.normalizeModKey(m);
            if (key) set.add(key.toLowerCase());
        }
        return set;
    }

    addModsToPreset(presetName, modNames) {
        const p = this.presets[presetName];
        if (!p) return false;
        const set = new Set((p.mods || []).map(m => ModPresetsManager.normalizeModKey(m)).filter(Boolean));
        for (const n of modNames || []) {
            const key = ModPresetsManager.normalizeModKey(n);
            if (key) set.add(key);
        }
        p.mods = [...set];
        this.save();
        return true;
    }

    removeModsFromPreset(presetName, modNames) {
        const p = this.presets[presetName];
        if (!p) return false;
        const remove = this._memberKeySet(modNames);
        p.mods = (p.mods || []).filter(m => !remove.has(ModPresetsManager.normalizeModKey(m).toLowerCase()));
        p.enabled = (p.enabled || []).filter(m => !remove.has(ModPresetsManager.normalizeModKey(m).toLowerCase()));
        this.save();
        return true;
    }

    setReceiveUpdates(name, on) {
        const trimmed = String(name || '').trim();
        const p = this.presets[trimmed];
        if (!p) return false;
        p.receiveUpdates = !!on;
        this.save();
        return true;
    }

    getReceiveUpdates(name) {
        const p = this.presets[String(name || '').trim()];
        return !!(p && p.receiveUpdates);
    }

    syncEnabledFromMods(allMods) {
        const p = this.presets[this.activePreset];
        if (!p) return;
        const memberSet = this._memberKeySet(p.mods || []);
        const enabled = [];
        for (const m of allMods || []) {
            const key = ModPresetsManager.normalizeModKey(m.originalName);
            if (!key) continue;
            if (memberSet.size > 0 && !memberSet.has(key.toLowerCase())) continue;
            if (m.status === 'enabled') enabled.push(key);
        }
        p.enabled = enabled;
        this.save(false);
    }

    captureCurrentLoadout(allMods, { includeAllAsMembers = false } = {}) {
        const p = this.presets[this.activePreset] || { mods: [], enabled: [] };
        this.presets[this.activePreset] = p;
        const enabled = [];
        const mods = includeAllAsMembers
            ? []
            : [...(p.mods || [])].map(m => ModPresetsManager.normalizeModKey(m)).filter(Boolean);
        const memberSet = this._memberKeySet(mods);
        for (const m of allMods || []) {
            const key = ModPresetsManager.normalizeModKey(m.originalName);
            if (!key) continue;
            if (includeAllAsMembers) {
                mods.push(key);
                memberSet.add(key.toLowerCase());
            }
            if (m.status === 'enabled' && (includeAllAsMembers || memberSet.size === 0 || memberSet.has(key.toLowerCase()))) {
                enabled.push(key);
            }
        }
        if (includeAllAsMembers) p.mods = mods;
        p.enabled = enabled;
        this.save();
    }

    isModInPreset(presetName, modName) {
        const mods = this.presets[presetName]?.mods || [];
        if (mods.length === 0) return false;
        return mods.some(m => this._sameModKey(m, modName));
    }

    isModInActivePreset(modName) {
        return this.isModInPreset(this.activePreset, modName);
    }

    _isProtectedCoreIni(modName) {
        const n = String(modName || '').replace(/\\/g, '/');
        if (/^CoreIni\//i.test(n)) return true;
        const base = n.split('/').pop() || '';
        return /^(Fallout76|Project76)(Custom|Prefs)\.ini$/i.test(base);
    }

    shouldShowMod(modName, extraNames) {
        if (this._isProtectedCoreIni(modName)) return true;
        if (this.isModInActivePreset(modName)) return true;
        if (Array.isArray(extraNames)) {
            for (const name of extraNames) {
                if (name && this.isModInActivePreset(name)) return true;
            }
        }
        return false;
    }

    save(notifyHost = true) {
        if (!notifyHost) return;
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({
                type: 'UPDATE_MOD_PRESETS',
                presets: this.presets
            });
            window.chrome.webview.postMessage({
                type: 'SET_ACTIVE_MOD_PRESET',
                name: this.activePreset
            });
        }
    }

    applyActive() {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({
                type: 'APPLY_MOD_PRESET',
                name: this.activePreset
            });
        }
    }

    exportActive() {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({
                type: 'EXPORT_MOD_PRESET',
                name: this.activePreset
            });
        }
    }

    importPreset() {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({ type: 'IMPORT_MOD_PRESET' });
        }
    }

    _normalize(modPresets) {
        let raw = modPresets;
        if (typeof raw === 'string') {
            try {
                raw = JSON.parse(raw);
            } catch {
                raw = {};
            }
        }
        if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
            raw = {};
        }

        const out = {};
        for (const [key, val] of Object.entries(raw)) {
            if (Array.isArray(val)) {
                out[key] = { mods: [...val], enabled: [], receiveUpdates: false };
                continue;
            }
            if (!val || typeof val !== 'object') continue;
            const mods = Array.isArray(val.mods) ? val.mods
                : (Array.isArray(val.Mods) ? val.Mods : []);
            const enabled = Array.isArray(val.enabled) ? val.enabled
                : (Array.isArray(val.Enabled) ? val.Enabled : []);
            const receiveUpdates = !!(val.receiveUpdates ?? val.ReceiveUpdates);
            out[key] = { mods: [...mods], enabled: [...enabled], receiveUpdates };
        }

        if (Object.keys(out).length === 0) {
            out[ModPresetsManager.DEFAULT_NAME] = { mods: [], enabled: [], receiveUpdates: false };
            out['Preset 1'] = { mods: [], enabled: [], receiveUpdates: false };
            out['Preset 2'] = { mods: [], enabled: [], receiveUpdates: false };
        }
        return out;
    }
}

export { ModPresetsManager as ModGroupsManager };
