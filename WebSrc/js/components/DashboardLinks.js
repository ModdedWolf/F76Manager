import { escapeAttr, escapeHtml } from '../utils/htmlSafe.js';

const STORAGE_KEY = 'dashboardUserLinks';
const SEEDED_KEY = 'dashboardUserLinksSeeded';
const MAX_LINKS = 4;
const END_TARGET = '__end__';

const KNOWN_FAVICONS = {
    'nukacrypt.com': 'https://nukacrypt.com/static/img/sfcrypt.png',
    'nukaknights.com': 'https://nukaknights.com/cms/templates/nukaknights/android-chrome-192x192.png'
};

const DEFAULT_LINKS = [
    {
        id: 'nukacrypt',
        name: 'Nukacrypt',
        url: 'https://nukacrypt.com/'
    },
    {
        id: 'minerva',
        name: 'Minerva schedule',
        url: 'https://nukaknights.com/minerva-dates-inventory.html'
    }
];

function label(key, fallback) {
    const translate = typeof window !== 'undefined' ? window._t : null;
    const value = typeof translate === 'function' ? translate(key) : '';
    if (!value || value === key) return fallback;
    return value;
}

function newId() {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
        return crypto.randomUUID();
    }
    return `link-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

export function normalizeLinkUrl(raw) {
    let trimmed = String(raw ?? '').trim();
    if (!trimmed) return '';
    if (!/^[a-z][a-z0-9+.-]*:/i.test(trimmed)) trimmed = `https://${trimmed}`;
    try {
        const parsed = new URL(trimmed);
        if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return '';
        if (!parsed.hostname || !parsed.hostname.includes('.')) return '';
        return parsed.href;
    } catch {
        return '';
    }
}

export function faviconCandidates(pageUrl) {
    try {
        const parsed = new URL(pageUrl);
        const host = parsed.hostname.toLowerCase().replace(/^www\./, '');
        const urls = [];
        if (KNOWN_FAVICONS[host]) urls.push(KNOWN_FAVICONS[host]);
        urls.push(`${parsed.origin}/apple-touch-icon.png`);
        urls.push(`${parsed.origin}/android-chrome-192x192.png`);
        urls.push(`https://www.google.com/s2/favicons?sz=128&domain=${encodeURIComponent(host)}`);
        urls.push(`${parsed.origin}/favicon.ico`);
        return urls.filter((url, index) => urls.indexOf(url) === index);
    } catch {
        return [];
    }
}

function hostLabel(url) {
    try {
        return new URL(url).hostname.replace(/^www\./i, '');
    } catch {
        return url;
    }
}

const MAX_ICON_CHARS = 150000;

export function sanitizeIcon(raw) {
    const value = String(raw ?? '').trim();
    if (!/^data:image\/(png|jpeg|webp|gif);base64,[a-z0-9+/=\s]+$/i.test(value)) return '';
    if (value.length > MAX_ICON_CHARS) return '';
    return value.replace(/\s/g, '');
}

function resizeIconFile(file) {
    return new Promise((resolve, reject) => {
        const type = String(file?.type || '').toLowerCase();
        if (!/^image\/(png|jpeg|webp|gif|bmp)$/.test(type)) {
            reject(new Error('type'));
            return;
        }
        const reader = new FileReader();
        reader.onerror = () => reject(new Error('read'));
        reader.onload = () => {
            const img = new Image();
            img.onload = () => {
                if (!img.width || !img.height) {
                    reject(new Error('decode'));
                    return;
                }
                const size = 128;
                const canvas = document.createElement('canvas');
                canvas.width = size;
                canvas.height = size;
                const ctx = canvas.getContext('2d');
                if (!ctx) {
                    reject(new Error('canvas'));
                    return;
                }
                const scale = Math.min(size / img.width, size / img.height);
                const width = img.width * scale;
                const height = img.height * scale;
                ctx.imageSmoothingEnabled = true;
                ctx.imageSmoothingQuality = 'high';
                ctx.clearRect(0, 0, size, size);
                ctx.drawImage(img, (size - width) / 2, (size - height) / 2, width, height);
                const icon = sanitizeIcon(canvas.toDataURL('image/png'));
                if (!icon) reject(new Error('encode'));
                else resolve(icon);
            };
            img.onerror = () => reject(new Error('decode'));
            img.src = String(reader.result || '');
        };
        reader.readAsDataURL(file);
    });
}

function sanitizeLink(item) {
    const url = normalizeLinkUrl(item?.url);
    if (!url) return null;
    const name = String(item?.name ?? '').trim().slice(0, 80) || hostLabel(url);
    const id = String(item?.id || newId()).slice(0, 80);
    const icon = sanitizeIcon(item?.icon);
    return icon ? { id, name, url, icon } : { id, name, url };
}

export class DashboardLinks {
    constructor() {
        this.dragId = null;
        this.dropTargetId = null;
        this.dropAfter = false;
        this.pendingIcon = '';
        this.editingId = null;
    }

    ensureSeeded() {
        if (localStorage.getItem(SEEDED_KEY) === '1') return;
        if (!localStorage.getItem(STORAGE_KEY)) {
            localStorage.setItem(STORAGE_KEY, JSON.stringify(DEFAULT_LINKS));
        }
        localStorage.setItem(SEEDED_KEY, '1');
    }

    getLinks() {
        this.ensureSeeded();
        try {
            const parsed = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
            return Array.isArray(parsed) ? parsed.map(sanitizeLink).filter(Boolean) : [];
        } catch {
            return [];
        }
    }

    setLinks(links) {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(links));
        localStorage.setItem(SEEDED_KEY, '1');
    }

    displayName(link) {
        if (link.id === 'nukacrypt') return label('user_links_nukacrypt', 'Nukacrypt');
        if (link.id === 'minerva') return label('user_links_minerva', 'Minerva schedule');
        return link.name;
    }

    render() {
        const links = this.getLinks().slice(0, MAX_LINKS);
        const rows = links.map(link => this.renderRow(link)).join('');
        const empties = Array.from({ length: MAX_LINKS - links.length }, () => this.renderEmptySlot()).join('');

        return `
            <div class="widget user-links-widget">
                <div class="widget-header user-links-header">
                    <i data-lucide="link"></i>
                    <h3>${escapeHtml(label('user_links', 'Your links'))}</h3>
                </div>
                <div class="user-links-list">
                    ${rows}
                    ${empties}
                </div>
            </div>
        `;
    }

    renderEmptySlot() {
        return `
            <button type="button" class="user-link-slot user-link-slot-empty">
                <i data-lucide="plus"></i>
                <span class="user-link-copy">
                    <span class="user-link-name">${escapeHtml(label('user_links_add', 'Add link'))}</span>
                </span>
            </button>
        `;
    }

    renderRow(link) {
        const name = this.displayName(link);
        const safeUrl = escapeAttr(link.url);
        const safeName = escapeHtml(name);
        const customIcon = sanitizeIcon(link.icon);
        const candidates = customIcon ? [] : faviconCandidates(link.url);
        const initial = customIcon || candidates[0] || '';
        const letter = escapeHtml((name.trim().charAt(0) || '?').toUpperCase());
        const iconTitle = escapeAttr(label('user_links_icon', 'Choose icon'));
        return `
            <div class="user-link-slot" data-id="${escapeAttr(link.id)}" draggable="true" title="${escapeAttr(label('user_links_reorder', 'Drag to reorder'))}">
                <span class="user-link-grip">
                    <i data-lucide="grip-vertical"></i>
                </span>
                <button type="button" class="user-link-favicon-wrap" data-edit-id="${escapeAttr(link.id)}" title="${iconTitle}">
                    <img class="user-link-favicon" alt="" draggable="false" src="${escapeAttr(initial)}" ${customIcon ? 'data-custom="1"' : `data-candidates="${escapeAttr(candidates.join('|'))}" data-candidate-index="0"`}>
                    <span class="user-link-favicon-letter" hidden>${letter}</span>
                </button>
                <a class="external-link user-link-copy" draggable="false" href="${safeUrl}" data-url="${safeUrl}" title="${safeUrl}">
                    <span class="user-link-name">${safeName}</span>
                    <span class="user-link-host">${escapeHtml(hostLabel(link.url))}</span>
                </a>
                <button type="button" class="user-link-remove" data-remove-id="${escapeAttr(link.id)}" title="${escapeAttr(label('user_links_remove', 'Remove'))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `;
    }

    mount(root) {
        if (!root || root.dataset.userLinksBound) return;
        root.dataset.userLinksBound = '1';

        root.addEventListener('error', (e) => {
            const img = e.target;
            if (!img?.classList?.contains('user-link-favicon')) return;
            if (img.dataset.custom === '1') {
                img.hidden = true;
                const letter = img.parentElement?.querySelector('.user-link-favicon-letter');
                if (letter) letter.hidden = false;
                return;
            }
            const candidates = String(img.dataset.candidates || '').split('|').filter(Boolean);
            const next = Number(img.dataset.candidateIndex || '0') + 1;
            if (next < candidates.length) {
                img.dataset.candidateIndex = String(next);
                img.src = candidates[next];
                return;
            }
            img.hidden = true;
            const letter = img.parentElement?.querySelector('.user-link-favicon-letter');
            if (letter) letter.hidden = false;
        }, true);

        root.addEventListener('click', (e) => {
            if (e.target.closest('.user-link-slot-empty')) {
                e.preventDefault();
                this.showPopup(null);
                return;
            }
            const editBtn = e.target.closest('.user-link-favicon-wrap');
            if (editBtn && root.contains(editBtn)) {
                e.preventDefault();
                e.stopPropagation();
                const id = editBtn.getAttribute('data-edit-id');
                const link = this.getLinks().find(item => item.id === id);
                if (link) this.showPopup(link);
                return;
            }
            const removeBtn = e.target.closest('.user-link-remove');
            if (removeBtn) {
                e.preventDefault();
                e.stopPropagation();
                const id = removeBtn.getAttribute('data-remove-id');
                if (id) this.removeLink(id);
            }
        });

        root.addEventListener('dragstart', (e) => {
            const row = e.target.closest?.('.user-link-slot[data-id]');
            if (!row || !root.contains(row)) return;
            this.dragId = row.dataset.id;
            this.dropTargetId = null;
            e.stopPropagation();
            if (e.dataTransfer) {
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData('text/plain', this.dragId);
            }
            row.classList.add('dragging');
        });

        root.addEventListener('dragover', (e) => {
            if (!this.dragId) return;
            const list = e.target.closest?.('.user-links-widget');
            if (!list || !root.contains(list)) return;
            e.preventDefault();
            e.stopPropagation();
            if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';

            this.clearDropMarks();
            this.dropTargetId = null;
            const empty = e.target.closest?.('.user-link-slot-empty');
            if (empty) {
                this.dropTargetId = END_TARGET;
                empty.classList.add('drop-end');
                return;
            }
            const row = e.target.closest?.('.user-link-slot[data-id]');
            if (!row || row.dataset.id === this.dragId) return;
            const rect = row.getBoundingClientRect();
            this.dropAfter = e.clientY > rect.top + rect.height / 2;
            this.dropTargetId = row.dataset.id;
            row.classList.add(this.dropAfter ? 'drop-after' : 'drop-before');
        });

        root.addEventListener('drop', (e) => {
            if (!this.dragId) return;
            const list = e.target.closest?.('.user-links-widget');
            if (!list || !root.contains(list)) return;
            e.preventDefault();
            e.stopPropagation();
            this.commitDrop();
        });

        root.addEventListener('dragend', () => {
            this.clearDropMarks();
            root.querySelectorAll('.user-link-slot.dragging').forEach(row => row.classList.remove('dragging'));
            this.dragId = null;
            this.dropTargetId = null;
        });
    }

    showPopup(link) {
        this.closePopup();
        this.editingId = link?.id || null;
        this.pendingIcon = sanitizeIcon(link?.icon);
        const preview = this.pendingIcon || (link ? (faviconCandidates(link.url)[0] || '') : '');
        const title = this.editingId
            ? label('user_links_edit', 'Edit link')
            : label('user_links_add', 'Add link');
        const saveLabel = this.editingId
            ? label('user_links_update', 'Save')
            : label('user_links_save', 'Add');
        const nameValue = link ? this.displayName(link) : '';
        const urlValue = link?.url || '';
        document.body.insertAdjacentHTML('beforeend', `
            <div class="add-button-popup-overlay" id="user-link-popup">
                <form class="add-button-popup user-link-popup">
                    <div class="add-button-popup-header">
                        <h3>${escapeHtml(title)}</h3>
                        <button type="button" class="add-button-popup-close user-link-popup-close" aria-label="${escapeAttr(label('cancel', 'Cancel'))}">
                            <i data-lucide="x"></i>
                        </button>
                    </div>
                    <div class="add-button-popup-content">
                        <button type="button" class="user-links-icon-pick" title="${escapeAttr(label('user_links_icon', 'Choose icon'))}">
                            ${preview
                                ? `<img class="user-links-icon-preview" alt="" src="${escapeAttr(preview)}">`
                                : '<i data-lucide="image-plus"></i>'}
                        </button>
                        <input type="text" class="user-links-name" maxlength="80" placeholder="${escapeAttr(label('user_links_name', 'Name'))}" value="${escapeAttr(nameValue)}" autocomplete="off">
                        <input type="text" class="user-links-url" placeholder="${escapeAttr(label('user_links_url', 'Website'))}" value="${escapeAttr(urlValue)}" autocomplete="off" spellcheck="false">
                        <input type="file" class="user-links-icon-file" accept="image/png,image/jpeg,image/webp,image/gif,image/bmp" hidden>
                        <p class="user-links-error" hidden></p>
                        <div class="user-link-popup-actions">
                            <button type="button" class="user-link-popup-cancel">${escapeHtml(label('cancel', 'Cancel'))}</button>
                            <button type="submit" class="user-link-popup-save">${escapeHtml(saveLabel)}</button>
                        </div>
                    </div>
                </form>
            </div>
        `);
        if (window.lucide) window.lucide.createIcons();
        const overlay = document.getElementById('user-link-popup');
        const form = overlay?.querySelector('form');
        if (!overlay || !form) return;
        const close = () => this.closePopup();
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) close();
        });
        overlay.querySelector('.user-link-popup-close')?.addEventListener('click', close);
        overlay.querySelector('.user-link-popup-cancel')?.addEventListener('click', close);
        overlay.querySelector('.user-links-icon-pick')?.addEventListener('click', () => {
            overlay.querySelector('.user-links-icon-file')?.click();
        });
        overlay.querySelector('.user-links-icon-file')?.addEventListener('change', (e) => {
            const file = e.target.files && e.target.files[0];
            e.target.value = '';
            if (file) this.applyIconFile(file, form);
        });
        form.addEventListener('submit', (e) => {
            e.preventDefault();
            this.saveFromPopup(form);
        });
        this.popupKeyHandler = (e) => {
            if (e.key === 'Escape') close();
        };
        document.addEventListener('keydown', this.popupKeyHandler);
        form.querySelector('.user-links-name')?.focus();
    }

    closePopup() {
        document.getElementById('user-link-popup')?.remove();
        if (this.popupKeyHandler) {
            document.removeEventListener('keydown', this.popupKeyHandler);
            this.popupKeyHandler = null;
        }
        this.editingId = null;
        this.pendingIcon = '';
    }

    saveFromPopup(form) {
        const nameInput = form.querySelector('.user-links-name');
        const urlInput = form.querySelector('.user-links-url');
        const url = normalizeLinkUrl(urlInput ? urlInput.value : '');
        if (!url) {
            this.showError(form, label('user_links_invalid', 'Enter a website, like nukacrypt.com'));
            return;
        }
        const name = String(nameInput ? nameInput.value : '').trim().slice(0, 80) || hostLabel(url);
        const icon = sanitizeIcon(this.pendingIcon);
        const links = this.getLinks();
        if (this.editingId) {
            const link = links.find(item => item.id === this.editingId);
            if (link) {
                link.name = name;
                link.url = url;
                if (icon) link.icon = icon;
                else delete link.icon;
            }
        } else {
            if (links.length >= MAX_LINKS) {
                this.closePopup();
                return;
            }
            links.push(icon ? { id: newId(), name, url, icon } : { id: newId(), name, url });
        }
        this.setLinks(links);
        this.closePopup();
        this.refresh();
    }

    async applyIconFile(file, form) {
        try {
            this.pendingIcon = await resizeIconFile(file);
        } catch {
            this.showError(form, label('user_links_icon_invalid', 'Choose a PNG, JPG, WEBP, or GIF.'));
            return;
        }
        const pick = form.querySelector('.user-links-icon-pick');
        if (pick) pick.innerHTML = `<img class="user-links-icon-preview" alt="" src="${this.pendingIcon}">`;
        this.clearError(form);
    }

    removeLink(id) {
        this.setLinks(this.getLinks().filter(link => link.id !== id));
        this.refresh();
    }

    commitDrop() {
        const fromId = this.dragId;
        const targetId = this.dropTargetId;
        const after = this.dropAfter;
        this.dragId = null;
        this.dropTargetId = null;
        this.clearDropMarks();
        if (!fromId || !targetId || fromId === targetId) return;

        const links = this.getLinks();
        const moving = links.find(link => link.id === fromId);
        if (!moving) return;
        const rest = links.filter(link => link.id !== fromId);
        if (targetId === END_TARGET) {
            rest.push(moving);
            this.setLinks(rest);
            this.refresh();
            return;
        }
        let index = rest.findIndex(link => link.id === targetId);
        if (index < 0) return;
        if (after) index += 1;
        rest.splice(index, 0, moving);
        this.setLinks(rest);
        this.refresh();
    }

    clearDropMarks() {
        document.querySelectorAll('.user-link-slot.drop-before, .user-link-slot.drop-after, .user-link-slot.drop-end').forEach(row => {
            row.classList.remove('drop-before', 'drop-after', 'drop-end');
        });
    }

    showError(form, message) {
        const error = form.querySelector('.user-links-error');
        if (!error) return;
        error.textContent = message;
        error.removeAttribute('hidden');
    }

    clearError(form) {
        const error = form.querySelector('.user-links-error');
        if (!error) return;
        error.textContent = '';
        error.setAttribute('hidden', '');
    }

    refresh() {
        const existing = document.querySelector('.user-links-widget');
        if (!existing) return;
        existing.outerHTML = this.render();
        if (window.lucide) window.lucide.createIcons();
    }
}
