export class PipBoy {
    constructor() {
        this.activeTab = 'pipboy';
        this.syncEnabled = false;
        this.iniLoaded = false;

        this.colors = {
            pipboy:    { r: 26, g: 255, b: 128, exists: false },
            quickboy:  { r: -1, g: -1, b: -1, exists: false },
            pa:        { r: -1, g: -1, b: -1, exists: false }
        };

        this.presets = [
            { name: 'preset_classic', color: '#1eff00', r: 30, g: 255, b: 0 },
            { name: 'preset_amber', color: '#ffb642', r: 255, g: 182, b: 66 },
            { name: 'preset_blue', color: '#46c7ff', r: 70, g: 199, b: 255 },
            { name: 'preset_white', color: '#ffffff', r: 255, g: 255, b: 255 }
        ];

        this.tabConfig = {
            pipboy:   { icon: 'monitor', labelKey: 'interface_tab_pipboy',   descKey: 'pip_boy_desc' },
            quickboy: { icon: 'zap',     labelKey: 'interface_tab_quickboy', descKey: 'quickboy_desc' },
            pa:       { icon: 'shield',  labelKey: 'interface_tab_pa',       descKey: 'pa_desc' }
        };
    }

    render(data) {
        try {
            if (data && data.settings) {
                if (data.settings.pipboyRed !== undefined) {
                    this.colors.pipboy.r = data.settings.pipboyRed;
                    this.colors.pipboy.g = data.settings.pipboyGreen;
                    this.colors.pipboy.b = data.settings.pipboyBlue;
                }
                if (data.settings.quickboyRed !== undefined && data.settings.quickboyRed >= 0) {
                    this.colors.quickboy.r = data.settings.quickboyRed;
                    this.colors.quickboy.g = data.settings.quickboyGreen;
                    this.colors.quickboy.b = data.settings.quickboyBlue;
                    this.colors.quickboy.exists = true;
                }
                if (data.settings.paRed !== undefined && data.settings.paRed >= 0) {
                    this.colors.pa.r = data.settings.paRed;
                    this.colors.pa.g = data.settings.paGreen;
                    this.colors.pa.b = data.settings.paBlue;
                    this.colors.pa.exists = true;
                }
            }

            var tabsHtml = this._renderTabs();
            var panelHtml = this._renderTabPanel();
            return '<div class="pipboy-page">' + tabsHtml + '<div class="interface-tab-content">' + panelHtml + '</div></div>';
        } catch (err) {
            console.error('[PipBoy] Render error:', err);
            return '<div style="padding:48px;color:#ff5252;"><h2>PipBoy Render Error</h2><pre>' + (err.message || err) + '</pre><pre>' + (err.stack || '') + '</pre></div>';
        }
    }

    _renderTabs() {
        var tabs = ['pipboy', 'quickboy', 'pa'];
        var t = function(key) { try { return window._t(key) || key; } catch(e) { return key; } };

        var tabBtns = '';
        for (var i = 0; i < tabs.length; i++) {
            var tab = tabs[i];
            var cfg = this.tabConfig[tab];
            var active = this.activeTab === tab ? ' active' : '';
            tabBtns += '<button class="interface-tab' + active + '" data-tab="' + tab + '">'
                     + '<i data-lucide="' + cfg.icon + '"></i>'
                     + '<span>' + t(cfg.labelKey) + '</span>'
                     + '</button>';
        }

        return '<div class="interface-tabs">'
            + tabBtns
            + '<div class="interface-tabs-spacer"></div>'
            + '<div class="interface-settings-wrap">'
            +   '<button class="interface-settings-btn" id="interface-settings-btn" title="' + t('interface_settings') + '">'
            +     '<i data-lucide="settings"></i>'
            +   '</button>'
            +   '<div class="sync-popover hidden" id="sync-popover">'
            +     '<div class="sync-popover-header">'
            +       '<i data-lucide="link"></i>'
            +       '<span>' + t('interface_sync_label') + '</span>'
            +     '</div>'
            +     '<p class="sync-popover-desc">' + t('interface_sync_desc') + '</p>'
            +     '<label class="switch-row">'
            +       '<span>' + t('interface_sync_label') + '</span>'
            +       '<label class="switch">'
            +         '<input type="checkbox" id="sync-toggle"' + (this.syncEnabled ? ' checked' : '') + '>'
            +         '<span class="slider round"></span>'
            +       '</label>'
            +     '</label>'
            +   '</div>'
            + '</div>'
            + '</div>';
    }

    _renderTabPanel() {
        var t = function(key) { try { return window._t(key) || key; } catch(e) { return key; } };
        var tab = this.activeTab;
        var c = this.colors[tab];
        var r = c.r >= 0 ? c.r : 26;
        var g = c.g >= 0 ? c.g : 255;
        var b = c.b >= 0 ? c.b : 128;
        var currentHex = this.rgbToHex(r, g, b);
        var headerKey = this.tabConfig[tab].labelKey;
        var descKey = this.tabConfig[tab].descKey;

        var notesHtml = '';
        if (!c.exists && tab !== 'pipboy') {
            notesHtml = '<div class="not-set-note"><i data-lucide="alert-circle"></i><span>' + t('interface_not_set') + '</span></div>';
        }

        var presetsHtml = '';
        for (var i = 0; i < this.presets.length; i++) {
            var p = this.presets[i];
            presetsHtml += '<button class="preset-chip" data-r="' + p.r + '" data-g="' + p.g + '" data-b="' + p.b + '" style="--chip-color: ' + p.color + '">'
                + '<div class="chip-glow"></div>'
                + '<span class="chip-name">' + t(p.name) + '</span>'
                + '</button>';
        }

        var sliderBlock = function(channel, label, cssClass, val) {
            return '<div class="slider-row">'
                + '<span class="channel-label ' + cssClass + '">' + label + '</span>'
                + '<div class="slider-track ' + cssClass + '">'
                + '<input type="range" min="0" max="255" value="' + val + '" class="modern-slider" data-target="' + tab + '" data-channel="' + channel + '">'
                + '</div>'
                + '<span class="channel-val" id="val-' + tab + '-' + channel + '">' + val + '</span>'
                + '</div>';
        };

        var previewHtml = this._renderPreview(tab, r, g, b);

        return '<div class="pipboy-layout">'
            + '<div class="pipboy-controls">'
            +   '<div class="glass-panel-unified">'
            +     '<div class="panel-header">'
            +       '<h2>' + t(headerKey) + '</h2>'
            +       '<p class="text-muted">' + t(descKey) + '</p>'
            +     '</div>'
            +     '<div class="notes-placeholder">' + notesHtml + '</div>'
            +     '<div class="presets-section">'
            +       '<label class="section-label">' + t('presets_label') + '</label>'
            +       '<div class="presets-chips">' + presetsHtml + '</div>'
            +     '</div>'
            +     '<div class="divider-line"></div>'
            +     '<div class="customizer-section">'
            +       '<div class="customizer-header">'
            +         '<label class="section-label">' + t('custom_values_label') + '</label>'
            +         '<div class="hex-badge"><span class="hash">#</span>'
            +           '<input type="text" class="hex-input-transparent" id="hex-' + tab + '" value="' + currentHex + '" maxlength="6">'
            +         '</div>'
            +       '</div>'
            +       '<div class="sliders-stack">'
            +         sliderBlock('r', 'R', 'red', r)
            +         sliderBlock('g', 'G', 'green', g)
            +         sliderBlock('b', 'B', 'blue', b)
            +       '</div>'
            +     '</div>'
            +     '<div class="panel-actions">'
            +       '<button class="btn-glass primary" id="save-colors"><i data-lucide="check"></i> ' + t('apply') + '</button>'
            +       '<button class="btn-glass" id="reset-colors"><i data-lucide="rotate-ccw"></i> ' + t('reset') + '</button>'
            +     '</div>'
            +   '</div>'
            + '</div>'
            + '<div class="pipboy-preview-container">'
            +   previewHtml
            + '</div>'
            + '</div>';
    }

    _renderPreview(tab, r, g, b) {
        var color = 'rgb(' + r + ', ' + g + ', ' + b + ')';

        var weaponList = ''
            + '<div class="qb-item active"><span class="qb-name">Cultist Piercer</span><span class="qb-stars">\u2605 \u2605 \u2605</span></div>'
            + '<div class="qb-item"><span class="qb-name">Elder\'s Mark</span><span class="qb-stars">\u2605 \u2605 \u2605 \u2605</span></div>'
            + '<div class="qb-item"><span class="qb-name">Endangerol Syringer</span></div>'
            + '<div class="qb-item"><span class="qb-name">Furious Clandestine Service Prime...</span><span class="qb-stars">\u2605 \u2605 \u2605 \u2605</span></div>'
            + '<div class="qb-item"><span class="qb-name">The Fixer</span><span class="qb-stars">\u2605 \u2605 \u2605 \u2605</span></div>'
            + '<div class="qb-item"><span class="qb-name">Ultracite Terror Sword</span><span class="qb-stars">\u2605 \u2605 \u2605 \u2605</span></div>'
            + '<div class="qb-item"><span class="qb-name">White Crocodile 105mm ProSnap Deluxe Camera</span></div>';

        if (tab === 'pipboy') {
            return '<div class="pipboy-wrapper">'
                + '<div class="fo76-screen fo76-pipboy" id="live-pb-screen" style="--pb-color: ' + color + '">'
                +   '<div class="screen-scanline"></div>'
                +   '<div class="fo76-maintabs">'
                +     '<span class="fo76-mt">STAT</span>'
                +     '<span class="fo76-mt active">\u250C ITEM \u2510</span>'
                +     '<span class="fo76-mt">DATA</span>'
                +     '<span class="fo76-mt">MAP</span>'
                +     '<span class="fo76-mt">RADIO</span>'
                +   '</div>'
                +   '<div class="qb-subtabs">'
                +     '<span class="qb-subtab">NEW</span>'
                +     '<span class="qb-subtab active">WEAPONS</span>'
                +     '<span class="qb-subtab">ARMOR</span>'
                +     '<span class="qb-subtab">APPAREL</span>'
                +   '</div>'
                +   '<div class="qb-body">' + weaponList + '</div>'
                +   '<div class="qb-footer">'
                +     '<span>487/533</span>'
                +     '<span>33,904</span>'
                +   '</div>'
                +   '<div class="pb-flashlight-glow"></div>'
                + '</div>'
                + '</div>';
        }

        if (tab === 'quickboy' || tab === 'pa') {
            return '<div class="pipboy-wrapper">'
                + '<div class="fo76-screen fo76-quickboy" id="live-pb-screen" style="--pb-color: ' + color + '">'
                +   '<div class="screen-scanline"></div>'
                +   '<div class="fo76-maintabs">'
                +     '<span class="fo76-mt">STAT</span>'
                +     '<span class="fo76-mt active">\u250C ITEM \u2510</span>'
                +     '<span class="fo76-mt">DATA</span>'
                +     '<span class="fo76-mt">MAP</span>'
                +     '<span class="fo76-mt">RADIO</span>'
                +   '</div>'
                +   '<div class="qb-subtabs">'
                +     '<span class="qb-subtab">NEW</span>'
                +     '<span class="qb-subtab active">WEAPONS</span>'
                +     '<span class="qb-subtab">ARMOR</span>'
                +     '<span class="qb-subtab">APPAREL</span>'
                +   '</div>'
                +   '<div class="qb-body">' + weaponList + '</div>'
                +   '<div class="qb-footer">'
                +     '<span>487/533</span>'
                +     '<span>33,904</span>'
                +   '</div>'
                + '</div>'
                + '</div>';
        }

        return '';
    }

    updateValues() {
    }

    onMount(data) {
        this._bindTabs();
        this._bindSettingsWheel();
        this._bindControls();
        this._requestIniColors();
    }

    _bindTabs() {
        var self = this;
        document.querySelectorAll('.interface-tab').forEach(function(btn) {
            btn.addEventListener('click', function() {
                var tab = btn.dataset.tab;
                if (tab === self.activeTab) return;
                self.activeTab = tab;
                var tabsEl = document.querySelector('.interface-tabs');
                var contentEl = document.querySelector('.interface-tab-content');
                if (tabsEl) tabsEl.outerHTML = self._renderTabs();
                if (contentEl) contentEl.innerHTML = self._renderTabPanel();
                lucide.createIcons();
                self._bindTabs();
                self._bindSettingsWheel();
                self._bindControls();
            });
        });
    }

    _bindSettingsWheel() {
        var btn = document.getElementById('interface-settings-btn');
        var popover = document.getElementById('sync-popover');
        if (btn && popover) {
            btn.addEventListener('click', function(e) {
                e.stopPropagation();
                popover.classList.toggle('hidden');
            });
            document.addEventListener('click', function(e) {
                if (!popover.contains(e.target) && e.target !== btn && !btn.contains(e.target)) {
                    popover.classList.add('hidden');
                }
            }, { once: false });
        }

        var syncToggle = document.getElementById('sync-toggle');
        if (syncToggle) {
            syncToggle.checked = this.syncEnabled;
            var self = this;
            syncToggle.addEventListener('change', function() {
                self.syncEnabled = syncToggle.checked;
                if (self.syncEnabled) {
                     var tab = self.activeTab;
                     var rS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="r"]');
                     var gS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="g"]');
                     var bS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="b"]');
                     var cR = rS ? parseInt(rS.value) : 26;
                     var cG = gS ? parseInt(gS.value) : 255;
                     var cB = bS ? parseInt(bS.value) : 128;
                     
                     var targets = ['pipboy', 'quickboy', 'pa'];
                     targets.forEach(function(tgt) {
                        if (tgt !== tab) {
                            self.colors[tgt].r = cR;
                            self.colors[tgt].g = cG;
                            self.colors[tgt].b = cB;
                            self.colors[tgt].exists = true; 
                        }
                     });
                     console.log('[PipBoy] Sync enabled. Forced all colors to match ' + tab);
                }
            });
        }
    }

    _bindControls() {
        var tab = this.activeTab;
        var self = this;

        var updatePreviews = function(fromHex) {
            var cR, cG, cB;
            var hexInput = document.getElementById('hex-' + tab);

            if (fromHex) {
                var hexVal = hexInput ? hexInput.value : '';
                if (/^[0-9A-Fa-f]{6}$/.test(hexVal)) {
                    var rgb = self.hexToRgb(hexVal);
                    cR = rgb.r; cG = rgb.g; cB = rgb.b;
                    var rS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="r"]');
                    var gS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="g"]');
                    var bS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="b"]');
                    if (rS) rS.value = cR;
                    if (gS) gS.value = cG;
                    if (bS) bS.value = cB;
                } else return;
            } else {
                var rS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="r"]');
                var gS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="g"]');
                var bS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="b"]');
                cR = rS ? parseInt(rS.value) : 0;
                cG = gS ? parseInt(gS.value) : 0;
                cB = bS ? parseInt(bS.value) : 0;
            }

            if (!fromHex && hexInput) hexInput.value = self.rgbToHex(cR, cG, cB);

            var rVal = document.getElementById('val-' + tab + '-r');
            var gVal = document.getElementById('val-' + tab + '-g');
            var bVal = document.getElementById('val-' + tab + '-b');
            if (rVal) rVal.textContent = cR;
            if (gVal) gVal.textContent = cG;
            if (bVal) bVal.textContent = cB;

            var screen = document.getElementById('live-pb-screen');
            if (screen) screen.style.setProperty('--pb-color', 'rgb(' + cR + ', ' + cG + ', ' + cB + ')');
            
            self.colors[tab].r = cR;
            self.colors[tab].g = cG;
            self.colors[tab].b = cB;

            if (self.syncEnabled) {
                var targets = ['pipboy', 'quickboy', 'pa'];
                targets.forEach(function(tgt) {
                    if (tgt !== tab) {
                        self.colors[tgt].r = cR;
                        self.colors[tgt].g = cG;
                        self.colors[tgt].b = cB;
                        self.colors[tgt].exists = true; 
                    }
                });
            }
        };

        document.querySelectorAll('.modern-slider[data-target="' + tab + '"]').forEach(function(el) {
            el.addEventListener('input', function() { updatePreviews(false); });
        });

        var hexInput = document.getElementById('hex-' + tab);
        if (hexInput) hexInput.addEventListener('input', function() { updatePreviews(true); });

        document.querySelectorAll('.preset-chip').forEach(function(btn) {
            btn.addEventListener('click', function() {
                var rS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="r"]');
                var gS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="g"]');
                var bS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="b"]');
                if (rS) rS.value = btn.dataset.r;
                if (gS) gS.value = btn.dataset.g;
                if (bS) bS.value = btn.dataset.b;
                updatePreviews(false);
            });
        });

        var saveBtn = document.getElementById('save-colors');
        if (saveBtn) {
            saveBtn.addEventListener('click', function() {
                if (self.syncEnabled) {
                    var activeColor = self.colors[self.activeTab];
                    
                    var payload = {
                        type: 'SAVE_INTERFACE_COLORS_BATCH',
                        colors: [
                            { target: 'pipboy', r: activeColor.r, g: activeColor.g, b: activeColor.b },
                            { target: 'quickboy', r: activeColor.r, g: activeColor.g, b: activeColor.b },
                            { target: 'pa', r: activeColor.r, g: activeColor.g, b: activeColor.b }
                        ]
                    };
                    window.chrome.webview.postMessage(payload);
                    
                    self.colors.pipboy = { r: activeColor.r, g: activeColor.g, b: activeColor.b, exists: true };
                    self.colors.quickboy = { r: activeColor.r, g: activeColor.g, b: activeColor.b, exists: true };
                    self.colors.pa = { r: activeColor.r, g: activeColor.g, b: activeColor.b, exists: true };
                    
                } else {
                    var tgt = self.activeTab;
                    var clr = self.colors[tgt];
                    window.chrome.webview.postMessage({
                        type: 'SAVE_INTERFACE_COLORS',
                        target: tgt,
                        r: clr.r, g: clr.g, b: clr.b
                    });
                }
            });
        }

        var resetBtn = document.getElementById('reset-colors');
        if (resetBtn) {
            resetBtn.addEventListener('click', function() {
                var rS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="r"]');
                var gS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="g"]');
                var bS = document.querySelector('.modern-slider[data-target="' + tab + '"][data-channel="b"]');
                if (rS) rS.value = 26;
                if (gS) gS.value = 255;
                if (bS) bS.value = 128;
                updatePreviews(false);
            });
        }
    }

    _getSaveTargets() {
        if (this.syncEnabled) {
            return ['pipboy', 'quickboy', 'pa'];
        }
        return [this.activeTab];
    }

    _requestIniColors() {
        if (!window.chrome || !window.chrome.webview) return;
        if (this._iniRequested) return;
        this._iniRequested = true;
        var self = this;

        var handler = function(event) {
            if (event.data.type === 'INTERFACE_COLORS') {
                window.chrome.webview.removeEventListener('message', handler);
                self.iniLoaded = true;

                var d = event.data;
                if (d.pipboy && d.pipboy.exists) {
                    self.colors.pipboy.r = d.pipboy.r;
                    self.colors.pipboy.g = d.pipboy.g;
                    self.colors.pipboy.b = d.pipboy.b;
                    self.colors.pipboy.exists = true;
                }
                if (d.quickboy && d.quickboy.exists) {
                    self.colors.quickboy.r = d.quickboy.r;
                    self.colors.quickboy.g = d.quickboy.g;
                    self.colors.quickboy.b = d.quickboy.b;
                    self.colors.quickboy.exists = true;
                }
                if (d.pa && d.pa.exists) {
                    self.colors.pa.r = d.pa.r;
                    self.colors.pa.g = d.pa.g;
                    self.colors.pa.b = d.pa.b;
                    self.colors.pa.exists = true;
                }

                var contentEl = document.querySelector('.interface-tab-content');
                if (contentEl) {
                    contentEl.innerHTML = self._renderTabPanel();
                    lucide.createIcons();
                    self._bindControls();
                }
            }
        };

        window.chrome.webview.addEventListener('message', handler);
        window.chrome.webview.postMessage({ type: 'GET_INTERFACE_COLORS' });
    }

    rgbToHex(r, g, b) {
        return ((1 << 24) + (r << 16) + (g << 8) + b).toString(16).slice(1).toUpperCase();
    }

    hexToRgb(hex) {
        var bigint = parseInt(hex, 16);
        return { r: (bigint >> 16) & 255, g: (bigint >> 8) & 255, b: bigint & 255 };
    }
}
