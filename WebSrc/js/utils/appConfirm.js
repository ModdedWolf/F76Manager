let _confirmPendingResolve = null;
let _confirmCleanup = null;

function _escapeConfirmHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function _formatConfirmMessageHtml(message) {
    const raw = String(message ?? '');
    const re = /["'«「]([^"'»」]+)["'»」]/g;
    let last = 0;
    let match;
    const parts = [];
    while ((match = re.exec(raw)) !== null) {
        if (match.index > last) {
            parts.push(_escapeConfirmHtml(raw.slice(last, match.index)));
        }
        parts.push(`<code>${_escapeConfirmHtml(match[1])}</code>`);
        last = match.index + match[0].length;
    }
    if (last < raw.length) {
        parts.push(_escapeConfirmHtml(raw.slice(last)));
    }
    return parts.join('').replace(/\n/g, '<br>');
}

export function dismissAppConfirm(result = false) {
    const pending = _confirmPendingResolve;
    const cleanup = _confirmCleanup;
    _confirmPendingResolve = null;
    _confirmCleanup = null;
    try { cleanup?.(); } catch (_) { }
    const overlay = document.getElementById('confirm-modal');
    if (overlay) overlay.classList.remove('active');
    if (pending) {
        try { pending(result); } catch (_) { }
    }
}

export function appConfirm(options = {}) {
    return new Promise((resolve) => {
        const overlay = document.getElementById('confirm-modal');
        const titleEl = document.getElementById('confirm-title');
        const msgEl = document.getElementById('confirm-message');
        const iconEl = document.getElementById('confirm-icon');
        const okBtn = document.getElementById('confirm-ok');
        const cancelBtn = document.getElementById('confirm-cancel');
        const closeTopBtn = document.getElementById('confirm-close-top');
        const promptWrap = document.getElementById('confirm-prompt-wrap');
        const promptInput = document.getElementById('confirm-prompt-input');
        const promptLabel = document.getElementById('confirm-prompt-label');
        if (!overlay || !titleEl || !msgEl || !okBtn || !cancelBtn) {
            console.warn('[appConfirm] #confirm-modal elements missing');
            resolve(false);
            return;
        }

        dismissAppConfirm(false);

        const t = window._t;
        const isDanger = !!options.danger;
        const isPrompt = !!options.prompt;
        titleEl.textContent = options.title ?? 'Confirmation';
        msgEl.innerHTML = _formatConfirmMessageHtml(options.message ?? '');
        msgEl.style.whiteSpace = 'normal';
        msgEl.hidden = isPrompt && !options.message;

        if (promptWrap && promptInput) {
            if (isPrompt) {
                promptWrap.hidden = false;
                if (promptLabel) {
                    promptLabel.textContent = options.promptLabel || '';
                    promptLabel.hidden = !options.promptLabel;
                }
                promptInput.value = options.defaultValue ?? '';
                promptInput.placeholder = options.placeholder ?? '';
                promptInput.maxLength = options.maxLength || 120;
            } else {
                promptWrap.hidden = true;
                promptInput.value = '';
            }
        }

        okBtn.textContent = options.okText ?? options.confirmLabel ?? 'OK';
        cancelBtn.textContent = options.cancelText ?? options.cancelLabel
            ?? (typeof t === 'function' ? t('cancel') : 'Cancel');
        if (closeTopBtn) {
            closeTopBtn.setAttribute('aria-label', cancelBtn.textContent);
        }

        if (iconEl) {
            iconEl.setAttribute('data-lucide', isDanger ? 'trash-2' : (isPrompt ? 'pencil' : 'help-circle'));
        }

        const previousFocus = document.activeElement;

        const cleanupListeners = () => {
            okBtn.removeEventListener('click', onOk);
            cancelBtn.removeEventListener('click', onCancel);
            if (closeTopBtn) closeTopBtn.removeEventListener('click', onCancel);
            overlay.removeEventListener('click', onOverlayClick);
            document.removeEventListener('keydown', onKeydown);
            if (promptInput) promptInput.removeEventListener('keydown', onPromptKeydown);
        };

        const finish = (value) => {
            if (_confirmPendingResolve !== resolve) return;
            _confirmPendingResolve = null;
            _confirmCleanup = null;
            overlay.classList.remove('active');
            if (promptWrap) promptWrap.hidden = true;
            if (msgEl) msgEl.hidden = false;
            cleanupListeners();
            resolve(value);
            try {
                if (previousFocus && typeof previousFocus.focus === 'function') {
                    previousFocus.focus();
                }
            } catch (_) { }
        };

        const onOk = () => {
            if (isPrompt) {
                const raw = (promptInput?.value ?? '').trim();
                if (!raw) {
                    try { promptInput?.focus(); } catch (_) { }
                    return;
                }
                finish(raw);
                return;
            }
            finish(true);
        };
        const onCancel = () => finish(isPrompt ? null : false);
        const onOverlayClick = (e) => {
            if (e.target === overlay) finish(isPrompt ? null : false);
        };
        const onKeydown = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                finish(isPrompt ? null : false);
            }
        };
        const onPromptKeydown = (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                onOk();
            }
        };

        _confirmPendingResolve = resolve;
        _confirmCleanup = cleanupListeners;

        okBtn.addEventListener('click', onOk);
        cancelBtn.addEventListener('click', onCancel);
        if (closeTopBtn) closeTopBtn.addEventListener('click', onCancel);
        overlay.addEventListener('click', onOverlayClick);
        document.addEventListener('keydown', onKeydown);
        if (isPrompt && promptInput) promptInput.addEventListener('keydown', onPromptKeydown);

        overlay.classList.add('active');
        if (window.lucide) {
            try {
                if (iconEl) {
                    iconEl.parentElement?.querySelectorAll('svg').forEach((s) => s.remove());
                }
                window.lucide.createIcons({ nodes: [overlay] });
            } catch (_) { }
        }
        try {
            if (isPrompt && promptInput) {
                promptInput.focus();
                promptInput.select();
            } else {
                okBtn.focus();
            }
        } catch (_) { }
    });
}

export function appPrompt(options = {}) {
    return appConfirm({
        ...options,
        prompt: true,
        title: options.title ?? (typeof window._t === 'function' ? window._t('new_preset') : 'Name'),
        message: options.message ?? '',
        okText: options.okText ?? (typeof window._t === 'function' ? window._t('ok') || 'OK' : 'OK'),
        cancelText: options.cancelText ?? (typeof window._t === 'function' ? window._t('cancel') : 'Cancel')
    });
}

export function installAppConfirmGlobals() {
    window.appConfirm = appConfirm;
    window.appPrompt = appPrompt;
    window.dismissAppConfirm = () => dismissAppConfirm(false);
}
