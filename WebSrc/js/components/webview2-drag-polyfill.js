
(function () {
    console.log('[Polyfill] Initializing WebView2 Touch-to-Drag Polyfill...');

    let dragSource = null;
    let dragImage = null;
    let lastTouch = null;

    function getDraggable(el) {
        while (el && el.dataset) {
            if (el.getAttribute('draggable') === 'true') return el;
            el = el.parentElement;
        }
        return null;
    }

    function createDragEvent(type, touch, target) {
        const ev = new Event(type, { bubbles: true, cancelable: true });
        ev.button = 0;
        ev.buttons = 1;
        ev.screenX = touch.screenX;
        ev.screenY = touch.screenY;
        ev.clientX = touch.clientX;
        ev.clientY = touch.clientY;
        ev.pageX = touch.pageX;
        ev.pageY = touch.pageY;
        ev.view = window;
        ev.dataTransfer = {
            effectAllowed: 'move',
            dropEffect: 'move',
            setData: () => { },
            getData: () => { },
            setDragImage: (img) => { dragImage = img; }
        };
        return ev;
    }

    window.addEventListener('touchstart', (e) => {
        if (e.touches.length !== 1) return;
        const touch = e.touches[0];
        const target = document.elementFromPoint(touch.clientX, touch.clientY);
        const draggable = getDraggable(target);

        if (draggable) {
            dragSource = draggable;
            lastTouch = touch;
            const evt = createDragEvent('dragstart', touch, draggable);
            if (draggable.dispatchEvent(evt)) {
                draggable.classList.add('poly-dragging');
            } else {
                dragSource = null;
            }
        }
    }, { passive: false });

    window.addEventListener('touchmove', (e) => {
        if (!dragSource || e.touches.length !== 1) return;
        e.preventDefault();
        const touch = e.touches[0];
        lastTouch = touch;

        const target = document.elementFromPoint(touch.clientX, touch.clientY);
        if (target) {
            const overEvt = createDragEvent('dragover', touch, target);
            target.dispatchEvent(overEvt);
        }
    }, { passive: false });

    window.addEventListener('touchend', (e) => {
        if (!dragSource) return;
        const touch = lastTouch;
        const target = document.elementFromPoint(touch.clientX, touch.clientY);

        if (target) {
            const dropEvt = createDragEvent('drop', touch, target);
            target.dispatchEvent(dropEvt);
        }

        const endEvt = createDragEvent('dragend', touch, dragSource);
        dragSource.dispatchEvent(endEvt);

        dragSource.classList.remove('poly-dragging');
        dragSource = null;
        lastTouch = null;
    });

    console.log('[Polyfill] Touch-to-Drag Loaded.');
})();
