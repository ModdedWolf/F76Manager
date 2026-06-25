export const ModsDndHandler = {
    manager: null,
    draggedRow: null,
    placeholder: null,
    isReordering: false,
    scrollContainer: null,
    scrollTbody: null,
    pointerY: 0,
    autoScrollRAF: null,

    init(manager) {
        this.manager = manager;
        this.setupFileImport();
        this.setupRowReordering();
    },

    setupFileImport() {
        const container = document.querySelector('.mods-page');
        if (!container) return;

        ['dragenter', 'dragover'].forEach(eventName => {
            container.addEventListener(eventName, e => {
                if (this.isReordering) return;
                e.preventDefault();
                container.classList.add('drag-over');
            }, false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            container.addEventListener(eventName, () => {
                container.classList.remove('drag-over');
            }, false);
        });
    },

    setupRowReordering() {
        const tbody = document.getElementById('mods-list-body');
        if (!tbody) return;

        this.scrollContainer = tbody.closest('.mods-table-container');
        this.scrollTbody = tbody;

        let dropped = false;

        tbody.addEventListener('dragstart', (e) => {
            const handle = e.target.closest('.drag-handle-wrapper');
            if (!handle) {
                return;
            }

            this.draggedRow = handle.closest('.mod-row');
            if (!this.draggedRow) return;

            this.isReordering = true;
            dropped = false;
            this.pointerY = e.clientY;
            this.startAutoScroll();

            e.dataTransfer.setData('text/plain', this.draggedRow.dataset.name);
            e.dataTransfer.effectAllowed = 'move';

            if (e.dataTransfer.setDragImage) {
                e.dataTransfer.setDragImage(this.draggedRow, 0, 0);
            }

            setTimeout(() => {
                if (this.draggedRow) {
                    this.createPlaceholder(this.draggedRow);
                    this.draggedRow.style.display = 'none';
                }
            }, 0);
        });

        tbody.addEventListener('dragover', (e) => {
            if (!this.isReordering) return;

            e.preventDefault();
            e.stopPropagation();
            e.dataTransfer.dropEffect = 'move';
            this.pointerY = e.clientY;

            this.updatePlaceholderPosition(tbody, e.clientY);
        });

        tbody.addEventListener('drop', (e) => {
            if (!this.isReordering) return;

            e.preventDefault();
            e.stopPropagation();

            if (this.placeholder && this.placeholder.parentNode && this.draggedRow) {
                this.placeholder.parentNode.insertBefore(this.draggedRow, this.placeholder);
                this.placeholder.remove();
                this.draggedRow.style.display = '';
                this.draggedRow.classList.remove('dragging');

                const newOrder = Array.from(tbody.querySelectorAll('.mod-row')).map(row => row.dataset.name);
                window.chrome.webview.postMessage({ type: 'UPDATE_MOD_ORDER', order: newOrder });
                dropped = true;
            }
            this.cleanupDrag();
        });

        tbody.addEventListener('dragend', (e) => {
            if (!this.isReordering) return;

            if (!dropped && this.placeholder && this.placeholder.parentNode && this.draggedRow) {
                this.placeholder.parentNode.insertBefore(this.draggedRow, this.placeholder);
            }
            this.cleanupDrag();
        });
    },

    createPlaceholder(row) {
        if (this.placeholder) this.placeholder.remove();
        this.placeholder = document.createElement('tr');
        this.placeholder.classList.add('placeholder');
        const rect = row.getBoundingClientRect();
        this.placeholder.style.height = `${rect.height}px`;
        this.placeholder.innerHTML = `<td colspan="5"></td>`;
        row.parentNode.insertBefore(this.placeholder, row);
    },

    startAutoScroll() {
        if (this.autoScrollRAF != null) return;
        this.autoScrollRAF = requestAnimationFrame(() => this.autoScrollStep());
    },

    stopAutoScroll() {
        if (this.autoScrollRAF != null) {
            cancelAnimationFrame(this.autoScrollRAF);
            this.autoScrollRAF = null;
        }
    },

    autoScrollStep() {
        if (!this.isReordering || !this.scrollContainer) {
            this.autoScrollRAF = null;
            return;
        }

        const EDGE = 60;
        const MAX_SPEED = 18;
        const rect = this.scrollContainer.getBoundingClientRect();
        let delta = 0;

        if (this.pointerY < rect.top + EDGE) {
            const depth = (rect.top + EDGE) - this.pointerY;
            delta = -MAX_SPEED * (depth / EDGE);
        } else if (this.pointerY > rect.bottom - EDGE) {
            const depth = this.pointerY - (rect.bottom - EDGE);
            delta = MAX_SPEED * (depth / EDGE);
        }

        if (delta !== 0) {
            this.scrollContainer.scrollTop += delta;
            if (this.scrollTbody && this.placeholder) {
                this.updatePlaceholderPosition(this.scrollTbody, this.pointerY);
            }
        }

        this.autoScrollRAF = requestAnimationFrame(() => this.autoScrollStep());
    },

    updatePlaceholderPosition(tbody, y) {
        if (!this.placeholder) return;

        const afterElement = this.getDragAfterElement(tbody, y);
        if (afterElement == null) {
            tbody.appendChild(this.placeholder);
        } else {
            tbody.insertBefore(this.placeholder, afterElement);
        }
    },

    cleanupDrag() {
        this.stopAutoScroll();

        if (this.placeholder && this.placeholder.parentNode) {
            this.placeholder.remove();
        }
        this.placeholder = null;

        if (this.draggedRow) {
            this.draggedRow.style.display = '';
            this.draggedRow = null;
        }

        this.isReordering = false;

        const rows = document.querySelectorAll('.mod-row');
        rows.forEach(r => r.style.display = '');
    },

    getDragAfterElement(container, y) {
        const draggableElements = [...container.querySelectorAll('tr:not(.placeholder)')];

        return draggableElements.reduce((closest, child) => {
            const box = child.getBoundingClientRect();
            const offset = y - box.top - box.height / 2;
            if (offset < 0 && offset > closest.offset) {
                return { offset: offset, element: child };
            } else {
                return closest;
            }
        }, { offset: Number.NEGATIVE_INFINITY }).element;
    }
};
