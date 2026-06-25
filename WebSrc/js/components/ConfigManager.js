import { ConfigRenderer } from './ConfigRenderer.js';
import { applyStylesheet as applyModTypeBadgeStylesheet } from '../utils/modTypeBadgeTheme.js';
import { openTabBadgeColorModal } from '../utils/badgeColorModal.js';

let _configGlobalEventsAttached = false;

export class ConfigManager {
    constructor() {
        this.sortField = localStorage.getItem('config_sort_field') || 'name';
        this.sortOrder = localStorage.getItem('config_sort_order') || 'asc';
        this.lastSearchTerm = '';
        this.data = null;
        this.customOrder = this._loadCustomOrder();
        this._configActionsMenuAbort = null;
        this._dnd = {
            draggedRow: null,
            placeholder: null,
            isReordering: false,
            dropped: false,
        };
        this.initGlobalHandlers();
    }

    _loadCustomOrder() {
        try {
            const raw = localStorage.getItem('config_custom_order');
            if (!raw) return [];
            const parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed.map(String) : [];
        } catch (_) {
            return [];
        }
    }

    _saveCustomOrder(next) {
        try {
            const arr = Array.isArray(next) ? next.map(String).filter(Boolean) : [];
            localStorage.setItem('config_custom_order', JSON.stringify(arr));
            this.customOrder = arr;
        } catch (_) {
        }
    }

    render(data) {
        this.data = data;
        return ConfigRenderer.render(this, data);
    }

    onMount() {
        this.setupEventListeners();
        this._mountConfigActionsMenu();
        this.setupRowReordering();
        applyModTypeBadgeStylesheet();
        if (window.lucide) window.lucide.createIcons();
        this.filterRowsInPlace((this.lastSearchTerm || '').trim().toLowerCase());
    }

    updateValues(data) {
        this.data = data;
        const contentArea = document.getElementById('content-area');
        if (!contentArea) return;
        contentArea.classList.add('content-refreshing');
        contentArea.innerHTML = `<div class="section-content">${this.render(data)}</div>`;
        this.onMount(data);
        requestAnimationFrame(() => {
            requestAnimationFrame(() => contentArea.classList.remove('content-refreshing'));
        });
    }

    initGlobalHandlers() {
        if (_configGlobalEventsAttached) return;
        _configGlobalEventsAttached = true;
    }

    filterRowsInPlace(searchTerm) {
        const rows = document.querySelectorAll('.mod-row');
        const term = (searchTerm || '').trim().toLowerCase();
        rows.forEach(row => {
            const searchText = (row.dataset.searchText || '').toLowerCase();
            row.style.display = !term || searchText.includes(term) ? '' : 'none';
        });
    }

    _getDeletableConfigCount() {
        const mods = this.data?.mods || [];
        return mods.filter(m => this._isDeletableConfig(m?.originalName)).length;
    }

    _isProtectedCoreConfig(originalName) {
        const original = String(originalName || '');
        if (/^coreini\//i.test(original)) return true;
        const fileName = original.replace(/^.*[/\\]/, '').toLowerCase();
        return fileName === 'fallout76custom.ini' || fileName === 'fallout76prefs.ini'
            || fileName === 'project76custom.ini' || fileName === 'project76prefs.ini';
    }

    _isDeletableConfig(originalName) {
        const original = String(originalName || '');
        const lower = original.toLowerCase();
        if (!lower.endsWith('.ini') && !lower.endsWith('.json') && !lower.endsWith('.txt')) return false;
        if (this._isProtectedCoreConfig(original)) return false;
        return true;
    }

    _closeConfigActionsMenu() {
        const dropdown = document.getElementById('config-actions-dropdown');
        const trigger = document.getElementById('btn-config-actions');
        if (dropdown) dropdown.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    _mountConfigActionsMenu() {
        const trigger = document.getElementById('btn-config-actions');
        const dropdown = document.getElementById('config-actions-dropdown');
        if (!trigger || !dropdown) return;

        if (this._configActionsMenuAbort) {
            this._configActionsMenuAbort.abort();
        }
        this._configActionsMenuAbort = new AbortController();
        const { signal } = this._configActionsMenuAbort;

        dropdown.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');

        trigger.addEventListener('click', (e) => {
            e.stopPropagation();
            const open = dropdown.hidden;
            dropdown.hidden = !open;
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (open && window.lucide) window.lucide.createIcons();
        }, { signal });

        document.addEventListener('click', (e) => {
            if (!document.getElementById('btn-config-actions')) return;
            if (!e.target.closest('.mods-actions-menu-wrap')) {
                this._closeConfigActionsMenu();
            }
        }, { signal });

        document.getElementById('config-action-badge-color')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeConfigActionsMenu();
            void openTabBadgeColorModal({ mods: this.data?.mods || [], scope: 'configs' });
        }, { signal });

        document.getElementById('config-action-backup')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeConfigActionsMenu();
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage({ type: 'BACKUP_CONFIGS' });
            }
        }, { signal });

        document.getElementById('config-action-transfer-to-other')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeConfigActionsMenu();
            void window.transferModsAcrossPlatforms?.('to-other', 'configs');
        }, { signal });

        document.getElementById('config-action-transfer-from-other')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeConfigActionsMenu();
            void window.transferModsAcrossPlatforms?.('from-other', 'configs');
        }, { signal });

        document.getElementById('config-action-delete-all')?.addEventListener('click', async (e) => {
            e.stopPropagation();
            this._closeConfigActionsMenu();
            const configCount = this._getDeletableConfigCount();
            const ok = await window.appConfirm({
                title: window._t('delete_all_configs'),
                message: window._t('delete_all_configs_confirm', configCount),
                okText: window._t('delete_all_configs'),
                cancelText: window._t('cancel'),
                danger: true
            });
            if (ok) {
                window.chrome.webview.postMessage({ type: 'DELETE_ALL_CONFIGS' });
            }
        }, { signal });
    }

    setupEventListeners() {
        const searchInput = document.getElementById('config-search');
        const searchClear = document.getElementById('config-search-clear');
        if (searchInput) {
            searchInput.addEventListener('input', (e) => {
                const next = String(e.target.value ?? '');
                this.lastSearchTerm = next;
                this.filterRowsInPlace(next.trim().toLowerCase());
            });
        }
        if (searchClear && searchInput) {
            searchClear.addEventListener('click', () => {
                searchInput.value = '';
                this.lastSearchTerm = '';
                this.filterRowsInPlace('');
                searchInput.focus();
            });
        }

        document.querySelectorAll('.mods-table th.sortable').forEach(th => {
            th.addEventListener('click', () => {
                const field = th.dataset.sort;
                if (this.sortField === field) {
                    this.sortOrder = this.sortOrder === 'asc' ? 'desc' : 'asc';
                } else {
                    this.sortField = field;
                    this.sortOrder = 'asc';
                }

                localStorage.setItem('config_sort_field', this.sortField);
                localStorage.setItem('config_sort_order', this.sortOrder);

                if (window.app) window.app.refreshCurrentSection();
            });
        });
    }

    setupRowReordering() {
        const tbody = document.getElementById('config-list-body');
        if (!tbody) return;

        tbody.addEventListener('dragstart', (e) => {
            const handle = e.target.closest('.drag-handle-wrapper');
            if (!handle) return;
            const row = handle.closest('.mod-row');
            if (!row) return;

            this._dnd.draggedRow = row;
            this._dnd.isReordering = true;
            this._dnd.dropped = false;

            if (this.sortField !== 'index') {
                this.sortField = 'index';
                this.sortOrder = 'asc';
                localStorage.setItem('config_sort_field', 'index');
                localStorage.setItem('config_sort_order', 'asc');
            }

            try {
                e.dataTransfer?.setData('text/plain', row.dataset.name || '');
                if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
                if (e.dataTransfer?.setDragImage) e.dataTransfer.setDragImage(row, 0, 0);
            } catch (_) { }

            setTimeout(() => {
                if (this._dnd.draggedRow) {
                    this._createPlaceholder(this._dnd.draggedRow);
                    this._dnd.draggedRow.style.display = 'none';
                }
            }, 0);
        });

        tbody.addEventListener('dragover', (e) => {
            if (!this._dnd.isReordering) return;
            e.preventDefault();
            e.stopPropagation();
            if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
            if (!this._dnd.placeholder) return;

            const afterElement = this._getDragAfterElement(tbody, e.clientY);
            if (afterElement == null) tbody.appendChild(this._dnd.placeholder);
            else tbody.insertBefore(this._dnd.placeholder, afterElement);
        });

        tbody.addEventListener('drop', (e) => {
            if (!this._dnd.isReordering) return;
            e.preventDefault();
            e.stopPropagation();

            if (this._dnd.placeholder && this._dnd.placeholder.parentNode && this._dnd.draggedRow) {
                this._dnd.placeholder.parentNode.insertBefore(this._dnd.draggedRow, this._dnd.placeholder);
                this._dnd.placeholder.remove();
                this._dnd.draggedRow.style.display = '';
                this._dnd.dropped = true;

                const newOrder = Array.from(tbody.querySelectorAll('.mod-row')).map(r => r.dataset.name).filter(Boolean);
                this._saveCustomOrder(newOrder);
            }

            this._cleanupDrag();
        });

        tbody.addEventListener('dragend', () => {
            if (!this._dnd.isReordering) return;
            if (!this._dnd.dropped && this._dnd.placeholder && this._dnd.placeholder.parentNode && this._dnd.draggedRow) {
                this._dnd.placeholder.parentNode.insertBefore(this._dnd.draggedRow, this._dnd.placeholder);
            }
            this._cleanupDrag();
        });
    }

    _createPlaceholder(row) {
        if (this._dnd.placeholder) this._dnd.placeholder.remove();
        const ph = document.createElement('tr');
        ph.classList.add('placeholder');
        const rect = row.getBoundingClientRect();
        ph.style.height = `${rect.height}px`;
        ph.innerHTML = `<td colspan="5"></td>`;
        this._dnd.placeholder = ph;
        row.parentNode?.insertBefore(ph, row);
    }

    _cleanupDrag() {
        if (this._dnd.placeholder && this._dnd.placeholder.parentNode) this._dnd.placeholder.remove();
        this._dnd.placeholder = null;

        if (this._dnd.draggedRow) {
            this._dnd.draggedRow.style.display = '';
            this._dnd.draggedRow = null;
        }

        this._dnd.isReordering = false;
        this._dnd.dropped = false;
    }

    _getDragAfterElement(container, y) {
        const draggableElements = [...container.querySelectorAll('tr:not(.placeholder)')];
        return draggableElements.reduce((closest, child) => {
            const box = child.getBoundingClientRect();
            const offset = y - box.top - box.height / 2;
            if (offset < 0 && offset > closest.offset) return { offset, element: child };
            return closest;
        }, { offset: Number.NEGATIVE_INFINITY }).element;
    }
}

