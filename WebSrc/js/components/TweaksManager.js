import { IniEditorModal } from './IniEditorModal.js';

export class TweaksManager {
    constructor() {
        this.categories = [
            {
                id: 'performance',
                labelKey: 'cat_performance',
                icon: 'zap',
                tweaks: [
                    { id: 'godrays', labelKey: 'tweak_godrays', descKey: 'tweak_godrays_desc', value: true },
                    { id: 'grass', labelKey: 'tweak_grass', descKey: 'tweak_grass_desc', value: true },
                    { id: 'rendergrass', labelKey: 'tweak_rendergrass', descKey: 'tweak_rendergrass_desc', value: true, warning: 'tweak_rendergrass_warning' },
                    { id: 'grassfade', labelKey: 'tweak_grassfade', descKey: 'tweak_grassfade_desc', type: 'range', min: 0, max: 15000, value: 7000 },
                    { id: 'shadows', labelKey: 'tweak_shadows', descKey: 'tweak_shadows_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high', 'opt_ultra'], options: ['Low', 'Medium', 'High', 'Ultra'], value: 'Medium' },
                    { id: 'shadowres', labelKey: 'tweak_shadowres', descKey: 'tweak_shadowres_desc', type: 'select', optionKeys: ['opt_512', 'opt_1024', 'opt_2048', 'opt_4096'], options: ['512', '1024', '2048', '4096'], value: '2048' },
                    { id: 'shadowfilter', labelKey: 'tweak_shadowfilter', descKey: 'tweak_shadowfilter_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high'], options: ['Low', 'Medium', 'High'], value: 'High' },
                    { id: 'focusshadows', labelKey: 'tweak_focusshadows', descKey: 'tweak_focusshadows_desc', value: true },
                    { id: 'volumquality', labelKey: 'tweak_volumquality', descKey: 'tweak_volumquality_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high'], options: ['Low', 'Medium', 'High'], value: 'High' },
                    { id: 'fastload', labelKey: 'tweak_fastload', descKey: 'tweak_fastload_desc', value: false, warning: 'tweak_fastload_warning' },
                    { id: 'texturequality', labelKey: 'tweak_texturequality', descKey: 'tweak_texturequality_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high', 'opt_ultra'], options: ['Low', 'Medium', 'High', 'Ultra'], value: 'High' },
                    { id: 'treedist', labelKey: 'tweak_treedist', descKey: 'tweak_treedist_desc', type: 'range', min: 5000, max: 100000, value: 25000 },
                    { id: 'lod', labelKey: 'tweak_lod', descKey: 'tweak_lod_desc', type: 'range', min: 10, max: 100, value: 50 },
                    { id: 'decals', labelKey: 'tweak_decals', descKey: 'tweak_decals_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high', 'opt_ultra'], options: ['Low', 'Medium', 'High', 'Ultra'], value: 'High' },
                    { id: 'decalsperframe', labelKey: 'tweak_decalsperframe', descKey: 'tweak_decalsperframe_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high'], options: ['Low', 'Medium', 'High'], value: 'High' },
                    { id: 'ssr', labelKey: 'tweak_ssr', descKey: 'tweak_ssr_desc', value: true },
                    { id: 'rainocclusion', labelKey: 'tweak_rainocclusion', descKey: 'tweak_rainocclusion_desc', value: true },
                    { id: 'npcshadowlights', labelKey: 'tweak_npcshadowlights', descKey: 'tweak_npcshadowlights_desc', value: true },
                    { id: 'tiledlighting', labelKey: 'tweak_tiledlighting', descKey: 'tweak_tiledlighting_desc', value: true },
                    { id: 'ao', labelKey: 'tweak_ao', descKey: 'tweak_ao_desc', value: true },
                    { id: 'blood', labelKey: 'tweak_blood', descKey: 'tweak_blood_desc', value: true },
                    { id: 'gridload', labelKey: 'tweak_gridload', descKey: 'tweak_gridload_desc', type: 'select', optionKeys: ['opt_3', 'opt_5', 'opt_7', 'opt_9'], options: ['3', '5', '7', '9'], value: '5', warning: 'tweak_gridload_warning' },
                    { id: 'cellloads', labelKey: 'tweak_cellloads', descKey: 'tweak_cellloads_desc', value: true },
                    { id: 'vsync', labelKey: 'tweak_vsync', descKey: 'tweak_vsync_desc', value: true, warning: 'tweak_vsync_warning' },
                    { id: 'fpscap', labelKey: 'tweak_fpscap', descKey: 'tweak_fpscap_desc', type: 'select', optionKeys: ['opt_60', 'opt_90', 'opt_120', 'opt_144', 'opt_unlimited'], options: ['60', '90', '120', '144', 'Unlimited'], value: '144' }
                ]
            },
            {
                id: 'graphics',
                labelKey: 'cat_graphics',
                icon: 'image',
                tweaks: [
                    { id: 'fov', labelKey: 'tweak_fov', descKey: 'tweak_fov_desc', type: 'range', min: 70, max: 120, value: 90 },
                    { id: 'fov1st', labelKey: 'tweak_fov1st', descKey: 'tweak_fov1st_desc', type: 'range', min: 70, max: 120, value: 90 },
                    { id: 'fovPipboy', labelKey: 'tweak_fov_pipboy', descKey: 'tweak_fov_pipboy_desc', type: 'range', min: 70, max: 120, value: 90 },
                    { id: 'motionblur', labelKey: 'tweak_motionblur', descKey: 'tweak_motionblur_desc', value: true },
                    { id: 'dof', labelKey: 'tweak_dof', descKey: 'tweak_dof_desc', value: true },
                    { id: 'lensflare', labelKey: 'tweak_lensflare', descKey: 'tweak_lensflare_desc', value: true },
                    { id: 'extrablur', labelKey: 'tweak_extrablur', descKey: 'tweak_extrablur_desc', value: true },
                    { id: 'vatsblur', labelKey: 'tweak_vatsblur', descKey: 'tweak_vatsblur_desc', value: true },
                    { id: 'taa', labelKey: 'tweak_taa', descKey: 'tweak_taa_desc', type: 'select', optionKeys: ['opt_none', 'opt_fxaa', 'opt_taa'], options: ['None', 'FXAA', 'TAA'], value: 'TAA' },
                    { id: 'aniso', labelKey: 'tweak_aniso', descKey: 'tweak_aniso_desc', type: 'select', optionKeys: ['opt_none', 'opt_4x', 'opt_8x', 'opt_16x'], options: ['None', '4x', '8x', '16x'], value: '16x' },
                    { id: 'water', labelKey: 'tweak_water', descKey: 'tweak_water_desc', type: 'select', optionKeys: ['opt_low', 'opt_medium', 'opt_high'], options: ['Low', 'Medium', 'High'], value: 'High' },
                    { id: 'lodsky', labelKey: 'tweak_lodsky', descKey: 'tweak_lodsky_desc', type: 'range', min: 1, max: 20, value: 10 },
                    { id: 'leafanim', labelKey: 'tweak_leafanim', descKey: 'tweak_leafanim_desc', type: 'range', min: 1000, max: 8000, value: 3600 },
                    { id: 'gamma', labelKey: 'tweak_gamma', descKey: 'tweak_gamma_desc', type: 'range', min: 8, max: 14, value: 10, warning: 'tweak_gamma_warning' },
                    { id: 'glassshader', labelKey: 'tweak_glassshader', descKey: 'tweak_glassshader_desc', value: true },
                    { id: 'pbrshadows', labelKey: 'tweak_pbrshadows', descKey: 'tweak_pbrshadows_desc', value: true },
                    { id: 'corpsehighlight', labelKey: 'tweak_corpsehighlight', descKey: 'tweak_corpsehighlight_desc', type: 'select', optionKeys: ['opt_off', 'opt_low', 'opt_high'], options: ['Off', 'Low', 'High'], value: 'Low' },
                    { id: 'playernames', labelKey: 'tweak_playernames', descKey: 'tweak_playernames_desc', value: true },
                    { id: 'playerpings', labelKey: 'tweak_playerpings', descKey: 'tweak_playerpings_desc', value: true },
                    { id: 'conversationhistory', labelKey: 'tweak_conversationhistory', descKey: 'tweak_conversationhistory_desc', type: 'range', min: 1, max: 10, value: 4 },
                    { id: 'pipboyfx', labelKey: 'tweak_pipboyfx', descKey: 'tweak_pipboyfx_desc', value: true, warning: 'tweak_pipboyfx_warning' }
                ]
            },
            {
                id: 'network',
                labelKey: 'cat_network',
                icon: 'wifi',
                tweaks: [
                    { id: 'ping', labelKey: 'tweak_ping', descKey: 'tweak_ping_desc', value: true },
                    { id: 'bandwidth', labelKey: 'tweak_bandwidth', descKey: 'tweak_bandwidth_desc', value: true }
                ]
            }
        ];

        const presetBase = {
            godrays: true, grass: true, rendergrass: true, grassfade: 7000,
            shadows: 'High', shadowres: '2048', shadowfilter: 'High', focusshadows: true,
            volumquality: 'High', texturequality: 'High', treedist: 25000, lod: 50,
            decals: 'High', decalsperframe: 'High', ssr: true, rainocclusion: true,
            npcshadowlights: true, tiledlighting: true, ao: true, blood: true,
            gridload: '5', cellloads: true, fastload: false,
            vsync: true, fpscap: '144',
            fov: 90, fov1st: 90, fovPipboy: 90, motionblur: true, dof: true,
            lensflare: true, extrablur: true, vatsblur: true, taa: 'TAA', aniso: '16x', water: 'High',
            lodsky: 10, leafanim: 3600, gamma: 10, glassshader: true, pbrshadows: true,
            corpsehighlight: 'Low', playernames: true, playerpings: true, conversationhistory: 4,
            pipboyfx: true, ping: false, bandwidth: false
        };

        this.presets = {
            'tweak_preset_vanilla': { ...presetBase },
            'tweak_preset_performance': {
                ...presetBase,
                godrays: false, grass: false, shadows: 'Low', shadowres: '1024', shadowfilter: 'Medium',
                focusshadows: false, volumquality: 'Medium', texturequality: 'Medium', treedist: 15000,
                lod: 20, decals: 'Low', decalsperframe: 'Medium', ssr: false, rendergrass: true,
                grassfade: 4000, fastload: true, vsync: false, fpscap: '120', motionblur: false,
                dof: false, lensflare: false, extrablur: false, vatsblur: false, taa: 'FXAA', aniso: '4x', water: 'Low',
                pipboyfx: true, ping: true, bandwidth: true, gridload: '5'
            },
            'tweak_preset_ultra': {
                ...presetBase,
                shadows: 'Ultra', shadowres: '4096', shadowfilter: 'High', volumquality: 'High',
                texturequality: 'Ultra', treedist: 50000, lod: 100, decals: 'Ultra',
                decalsperframe: 'High', fov: 100, fov1st: 100, fovPipboy: 100, lodsky: 20,
                ping: true, bandwidth: true, fpscap: '144'
            },
            'tweak_preset_potato': {
                ...presetBase,
                godrays: false, grass: false, rendergrass: false, grassfade: 2000, shadows: 'Low',
                shadowres: '512', shadowfilter: 'Low', focusshadows: false, volumquality: 'Low',
                texturequality: 'Low', treedist: 8000, lod: 10, decals: 'Low', decalsperframe: 'Low',
                ssr: false, rainocclusion: false, npcshadowlights: false, tiledlighting: false,
                ao: true, blood: false, gridload: '3', cellloads: false, fastload: true,
                vsync: false, fpscap: '60', fov: 70, fov1st: 70, fovPipboy: 70,
                motionblur: false, dof: false, lensflare: false, extrablur: false, vatsblur: false, taa: 'None',
                aniso: 'None', water: 'Low', lodsky: 5, leafanim: 2000, gamma: 10,
                glassshader: false, pbrshadows: false, pipboyfx: true, ping: true, bandwidth: true
            }
        };

        this.activePreset = null;
        this.pendingChanges = {};
        this.activeCategoryId = this.categories.length > 0 ? this.categories[0].id : null;
    }

    render(data) {
        if (data && data.activeTweaksPreset) {
            this.activePreset = data.activeTweaksPreset;
        }

        if (data && data.settings) {
            this.categories.forEach(cat => {
                cat.tweaks.forEach(tweak => {
                    if (this.pendingChanges[tweak.id] !== undefined) {
                        tweak.value = this.pendingChanges[tweak.id];
                    } else if (data.settings[tweak.id] !== undefined) {
                        let incoming = this.normalizeSettingValue(tweak.id, data.settings[tweak.id]);
                        tweak.value = (tweak.type === 'select' && incoming != null) ? String(incoming) : incoming;
                    }
                });
            });
        }

        if (!this.activeCategoryId && this.categories.length > 0) {
            this.activeCategoryId = this.categories[0].id;
        }

        const activeCategory = this.categories.find(c => c.id === this.activeCategoryId) || this.categories[0];

        return `
            <div class="tweaks-page animate-fade">
                <div class="tweaks-toolbar" style="display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 20px;">
                    <div class="presets-bar" style="margin-bottom: 0;">
                        <span class="presets-label">${window._t('quick_presets')}:</span>
                        ${Object.keys(this.presets).map(p => `
                            <button class="preset-btn ${this.activePreset === p ? 'active' : ''}" data-preset="${p}">${window._t(p)}</button>
                        `).join('')}
                    </div>
                    <div class="tweaks-actions" style="display: flex; gap: 10px;">
                        <button class="btn" id="btn-edit-ini">
                            <i data-lucide="file-edit"></i> ${window._t('tweak_edit_ini_btn')}
                        </button>
                        <button class="btn primary" id="btn-save-tweaks">
                            <i data-lucide="save"></i> ${window._t('tweak_save_btn')}
                        </button>
                    </div>
                </div>

                <div class="tweaks-layout">
                    <div class="tweaks-sidebar">
                        ${this.categories.map(cat => `
                            <button class="tweaks-sidebar-item ${this.activeCategoryId === cat.id ? 'active' : ''}" data-category-id="${cat.id}">
                                <i data-lucide="${cat.icon}"></i>
                                <span>${window._t(cat.labelKey)}</span>
                            </button>
                        `).join('')}
                    </div>
                    <div class="tweaks-content" id="tweaks-content">
                        ${this.renderCategoryContent(activeCategory)}
                    </div>
                </div>
            </div>
        `;
    }

    renderCategoryContent(category) {
        if (!category) return '';

        return `
            <div class="tweak-category">
                <div class="category-header">
                    <i data-lucide="${category.icon}"></i>
                    <h3>${window._t(category.labelKey)}</h3>
                </div>
                <div class="tweak-list">
                    ${category.tweaks.map(tweak => this.renderTweak(tweak)).join('')}
                </div>
            </div>
        `;
    }

    refreshIcons(root) {
        if (!window.lucide || !root?.querySelectorAll) return;
        const nodes = root.querySelectorAll('[data-lucide]');
        if (!nodes.length) return;
        try {
            window.lucide.createIcons({ nodes });
        } catch (_) { }
    }

    formatRangeDisplay(tweak, value) {
        if (tweak.id === 'gamma') return (value / 10).toFixed(1);
        return value;
    }

    renderTweak(tweak) {
        let control = '';
        const isPending = this.pendingChanges[tweak.id] !== undefined;
        const currentValue = '' + tweak.value;

        if (tweak.type === 'select') {
            control = `
                <select class="tweak-select ${isPending ? 'pending' : ''}" data-id="${tweak.id}">
                    ${tweak.options.map((opt, i) => `<option value="${opt}" ${opt == currentValue ? 'selected' : ''}>${tweak.optionKeys ? window._t(tweak.optionKeys[i]) : opt}</option>`).join('')}
                </select>
            `;
        } else if (tweak.type === 'range') {
            control = `
                <div class="range-control">
                    <input type="range" min="${tweak.min}" max="${tweak.max}" value="${tweak.value}" class="tweak-range ${isPending ? 'pending' : ''}" data-id="${tweak.id}">
                    <span class="range-value" id="val-${tweak.id}">${this.formatRangeDisplay(tweak, tweak.value)}</span>
                </div>
            `;
        } else {
            control = `
                <label class="switch">
                    <input type="checkbox" ${tweak.value === true || tweak.value === '1' ? 'checked' : ''} class="tweak-check ${isPending ? 'pending' : ''}" data-id="${tweak.id}">
                    <span class="slider"></span>
                </label>
            `;
        }

        return `
            <div class="tweak-item">
                <div class="tweak-info">
                    <div class="tweak-label">${window._t(tweak.labelKey)} ${isPending ? '*' : ''}</div>
                    <div class="tweak-desc">${window._t(tweak.descKey)}</div>
                    ${tweak.warning ? `<div class="tweak-warning"><i data-lucide="alert-triangle"></i> ${window._t(tweak.warning)}</div>` : ''}
                </div>
                <div class="tweak-control">
                    ${control}
                </div>
            </div>
        `;
    }

    normalizeSettingValue(id, value) {
        const tweakDef = this.categories.flatMap(c => c.tweaks).find(t => t.id === id);
        if (!tweakDef || tweakDef.type === 'select' || tweakDef.type === 'range') {
            if (id === 'gamma' && typeof value === 'number' && value < 2)
                return Math.round(value * 10);
            return value;
        }
        if (value === true || value === false) return value;
        if (value === '1' || value === 1) return true;
        if (value === '0' || value === 0) return false;
        return value === 'true';
    }

    onMount(data) {
        document.querySelectorAll('.preset-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const presetName = btn.getAttribute('data-preset');
                const settings = this.presets[presetName];
                
                Object.keys(settings).forEach(key => {
                    this.pendingChanges[key] = settings[key];
                });

                this.updateInputsFromPending();
                this.showPendingBanner();
            });
        });

        const sidebarItems = document.querySelectorAll('.tweaks-sidebar-item');
        const contentEl = document.getElementById('tweaks-content');
        if (sidebarItems && contentEl) {
            sidebarItems.forEach(btn => {
                btn.addEventListener('click', () => {
                    const categoryId = btn.getAttribute('data-category-id');
                    if (!categoryId || categoryId === this.activeCategoryId) return;

                    this.activeCategoryId = categoryId;

                    sidebarItems.forEach(b => b.classList.remove('active'));
                    btn.classList.add('active');

                    const category = this.categories.find(c => c.id === this.activeCategoryId);
                    contentEl.innerHTML = this.renderCategoryContent(category);

                    this.bindTweakInputHandlers(contentEl);
                    this.updateInputsFromPending();
                    this.refreshIcons(contentEl);
                });
            });
        }

        this.bindTweakInputHandlers(document);

        const tweaksRoot = document.querySelector('.tweaks-page');
        if (tweaksRoot) this.refreshIcons(tweaksRoot);

        if (data?.settings) {
            this.updateValues(data);
        }

        const saveBtn = document.getElementById('btn-save-tweaks');
        if (saveBtn) {
            saveBtn.onclick = () => {
                if (Object.keys(this.pendingChanges).length === 0) {
                    if (window.app) window.app.showBanner({ type: 'info', text: window._t('no_changes_to_save') });
                    return;
                }

                window.chrome.webview.postMessage({ type: 'UPDATE_SETTINGS_BATCH', settings: this.pendingChanges });
                this.pendingChanges = {};
                this.hidePendingBanner();
            };
        }

        const editIniBtn = document.getElementById('btn-edit-ini');
        if (editIniBtn) {
            editIniBtn.onclick = () => {
                if (window.app && window.app.iniEditor) {
                    window.app.iniEditor.show();
                }
            };
        }
    }

    bindTweakInputHandlers(root = document) {
        root.querySelectorAll('.tweak-check').forEach(el => {
            el.addEventListener('change', (e) => {
                const id = el.getAttribute('data-id');
                this.handleInputChange(id, e.target.checked);
            });
        });

        root.querySelectorAll('.tweak-select').forEach(el => {
            el.addEventListener('change', (e) => {
                const id = el.getAttribute('data-id');
                this.handleInputChange(id, e.target.value);
            });
        });

        root.querySelectorAll('.tweak-range').forEach(el => {
            const updateSliderFill = (input) => {
                const min = parseInt(input.min) || 0;
                const max = parseInt(input.max) || 100;
                const val = parseInt(input.value) || 0;
                const percent = ((val - min) / (max - min)) * 100;
                input.style.setProperty('--value', percent + '%');
            };

            updateSliderFill(el);

            el.addEventListener('input', (e) => {
                const id = el.getAttribute('data-id');
                const valEl = document.getElementById(`val-${id}`);
                const tweakDef = this.categories.flatMap(c => c.tweaks).find(t => t.id === id);
                if (valEl) valEl.textContent = tweakDef ? this.formatRangeDisplay(tweakDef, parseInt(e.target.value)) : e.target.value;
                updateSliderFill(e.target);
            });

            el.addEventListener('change', (e) => {
                const id = el.getAttribute('data-id');
                this.handleInputChange(id, parseInt(e.target.value));
            });
        });
    }

    handleInputChange(id, value) {
        this.pendingChanges[id] = value;
        this.showPendingBanner();
    }

    showPendingBanner() {
        if (window.app) window.app.syncTweaksPendingBanner();
    }

    hidePendingBanner() {
        if (window.app) window.app.syncTweaksPendingBanner();
    }

    updateInputsFromPending() {
        Object.keys(this.pendingChanges).forEach(key => {
            const val = this.pendingChanges[key];
            
            const check = document.querySelector(`.tweak-check[data-id="${key}"]`);
            if (check) check.checked = val;

            const select = document.querySelector(`.tweak-select[data-id="${key}"]`);
            if (select) select.value = val;

            const range = document.querySelector(`.tweak-range[data-id="${key}"]`);
            if (range) {
                range.value = val;
                const valEl = document.getElementById(`val-${key}`);
                const tweakDef = this.categories.flatMap(c => c.tweaks).find(t => t.id === key);
                if (valEl) valEl.textContent = tweakDef ? this.formatRangeDisplay(tweakDef, val) : val;
                const min = parseInt(range.min) || 0;
                const max = parseInt(range.max) || 100;
                const percent = ((val - min) / (max - min)) * 100;
                range.style.setProperty('--value', percent + '%');
            }
        });
    }

    updateValues(data) {
        if (!data || !data.settings) return;


        if (data.activeTweaksPreset) {
            this.activePreset = data.activeTweaksPreset;
            document.querySelectorAll('.preset-btn').forEach(btn => {
                const p = btn.getAttribute('data-preset');
                if (p === this.activePreset) btn.classList.add('active');
                else btn.classList.remove('active');
            });
        }

        document.querySelectorAll('.tweak-check').forEach(el => {
            const id = el.getAttribute('data-id');
            if (this.pendingChanges[id] === undefined && data.settings[id] !== undefined) {
                el.checked = this.normalizeSettingValue(id, data.settings[id]);
            }
        });

        document.querySelectorAll('.tweak-select').forEach(el => {
            const id = el.getAttribute('data-id');
            if (this.pendingChanges[id] === undefined && data.settings[id] !== undefined) {
                el.value = String(data.settings[id]);
            }
        });

        document.querySelectorAll('.tweak-range').forEach(el => {
            const id = el.getAttribute('data-id');
            if (this.pendingChanges[id] === undefined && data.settings[id] !== undefined) {
                 if (document.activeElement !== el) {
                    let displayVal = this.normalizeSettingValue(id, data.settings[id]);
                    el.value = displayVal;
                    const valEl = document.getElementById(`val-${id}`);
                    const tweakDef = this.categories.flatMap(c => c.tweaks).find(t => t.id === id);
                    if (valEl) valEl.textContent = tweakDef ? this.formatRangeDisplay(tweakDef, displayVal) : displayVal;
                    
                    const min = parseInt(el.min) || 0;
                    const max = parseInt(el.max) || 100;
                    const val = parseInt(el.value) || 0;
                    const percent = ((val - min) / (max - min)) * 100;
                    el.style.setProperty('--value', percent + '%');
                }
            }
        });
    }
}
