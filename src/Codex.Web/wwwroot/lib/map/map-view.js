// The map page's browser half (Components/Campaign/MapView.razor, MAPS_DESIGN.md 9.5 and 9.6).
//
// It zooms and pans the picture (Ctrl or ⌘ and the wheel, or the wheel once the map has focus; pinch, drag, + and -, 0 to fit, the arrow
// keys) and turns a click into the picture's own pixels. The server hears only clicks and, while placing, the keys it acts on: never a
// mouse move, and no key at all otherwise. The stage it moves is its own: the page never renders a style on it, so a re-render never
// resets the zoom.

const MAX_SCALE = 8;
const CLICK_SLOP = 5;
const KEY_PAN = 60;
// How much of the picture always stays in sight, in screen pixels, however far it is dragged.
const KEEP_IN_VIEW = 48;
// How near a click must be to a corner, in screen pixels, to count as on it (an area's first corner finishes it).
const CORNER_SLOP = 8;
const PLACING_KEYS = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', ' ', 'Spacebar', 'Enter', 'Backspace', 'Escape']);

export function attach(viewport, stage, dotnet, width, height) {
    const view = { scale: 1, x: 0, y: 0, width, height, touched: false };
    // Placing is the page's: it says so on the stage in the same render that shows the cross-hair, so no key can beat a message.
    const placing = () => viewport.dataset.placing === 'true';
    const pointers = new Map();
    let press = null;
    let pinch = null;

    const box = () => viewport.getBoundingClientRect();
    const fitScale = () => {
        const r = box();
        return Math.min(r.width / view.width, r.height / view.height) || 1;
    };
    const minScale = () => Math.min(fitScale(), 1) * 0.5;
    const clampScale = s => Math.min(MAX_SCALE, Math.max(minScale(), s));

    // Keeps at least KEEP_IN_VIEW pixels of the picture (all of it, when it is smaller) inside the viewport on each axis.
    function clampPan() {
        const r = box();
        const w = view.width * view.scale;
        const h = view.height * view.scale;
        const keepX = Math.min(KEEP_IN_VIEW, w, r.width);
        const keepY = Math.min(KEEP_IN_VIEW, h, r.height);
        view.x = Math.min(r.width - keepX, Math.max(keepX - w, view.x));
        view.y = Math.min(r.height - keepY, Math.max(keepY - h, view.y));
    }

    function apply() {
        clampPan();
        stage.style.transform = `translate(${view.x}px, ${view.y}px) scale(${view.scale})`;
        // Markers keep their size on screen whatever the zoom (app.css: .map-marker-shape).
        stage.style.setProperty('--map-inv', String(1 / view.scale));
    }

    function fit() {
        const r = box();
        view.scale = Math.min(fitScale(), MAX_SCALE);
        view.x = (r.width - view.width * view.scale) / 2;
        view.y = (r.height - view.height * view.scale) / 2;
        view.touched = false;
        apply();
    }

    function zoomAt(factor, cx, cy) {
        const s = clampScale(view.scale * factor);
        const k = s / view.scale;
        view.x = cx - (cx - view.x) * k;
        view.y = cy - (cy - view.y) * k;
        view.scale = s;
        view.touched = true;
        apply();
    }

    function zoomCentre(factor) {
        const r = box();
        zoomAt(factor, r.width / 2, r.height / 2);
    }

    function pan(dx, dy) {
        view.x += dx;
        view.y += dy;
        view.touched = true;
        apply();
    }

    // The picture's own pixels under a point on screen. The stage's box already includes the zoom.
    function toImage(clientX, clientY) {
        const r = stage.getBoundingClientRect();
        return { x: (clientX - r.left) / r.width * view.width, y: (clientY - r.top) / r.height * view.height };
    }

    function onPointerDown(e) {
        if (e.button !== undefined && e.button !== 0 && e.pointerType === 'mouse') return;
        // Enter and Escape after clicking corners reach the stage only if it has focus.
        viewport.focus({ preventScroll: true });
        viewport.setPointerCapture?.(e.pointerId);
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pointers.size === 1) {
            press = { x: e.clientX, y: e.clientY, startX: view.x, startY: view.y, moved: false, target: e.target };
        } else if (pointers.size === 2) {
            const [a, b] = [...pointers.values()];
            const r = box();
            pinch = { distance: Math.hypot(a.x - b.x, a.y - b.y) || 1, scale: view.scale, cx: (a.x + b.x) / 2 - r.left, cy: (a.y + b.y) / 2 - r.top };
            if (press) press.moved = true;
        }
    }

    function onPointerMove(e) {
        if (!pointers.has(e.pointerId)) return;
        pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
        if (pinch && pointers.size >= 2) {
            const [a, b] = [...pointers.values()];
            const target = pinch.scale * (Math.hypot(a.x - b.x, a.y - b.y) / pinch.distance);
            zoomAt(target / view.scale, pinch.cx, pinch.cy);
            return;
        }
        if (!press) return;
        const dx = e.clientX - press.x;
        const dy = e.clientY - press.y;
        if (!press.moved && Math.abs(dx) + Math.abs(dy) > CLICK_SLOP) press.moved = true;
        if (press.moved) {
            view.x = press.startX + dx;
            view.y = press.startY + dy;
            view.touched = true;
            apply();
        }
    }

    function onPointerUp(e) {
        if (!pointers.has(e.pointerId)) return;
        pointers.delete(e.pointerId);
        if (pointers.size < 2) pinch = null;
        if (pointers.size > 0) return;
        const done = press;
        press = null;
        if (!done || done.moved || e.type === 'pointercancel') return;

        const marker = !placing() && done.target instanceof Element ? done.target.closest('[data-marker]') : null;
        if (marker) {
            dotnet.invokeMethodAsync('MarkerClicked', marker.getAttribute('data-marker'));
            return;
        }
        const point = toImage(e.clientX, e.clientY);
        if (point.x < 0 || point.y < 0 || point.x > view.width || point.y > view.height) return;
        dotnet.invokeMethodAsync('MapClicked', Math.round(point.x), Math.round(point.y), CORNER_SLOP / view.scale);
    }

    // A double-click finishes an area; its two clicks have already added the last corner (once: the second is on the same spot).
    function onDoubleClick(e) {
        if (!placing()) return;
        e.preventDefault();
        dotnet.invokeMethodAsync('FinishAreaByDoubleClick');
    }

    // The wheel scrolls the page past the map, unless Ctrl or ⌘ is held or the map has focus (clicked, or reached by Tab): then it zooms.
    function onWheel(e) {
        if (!(e.ctrlKey || e.metaKey || document.activeElement === viewport)) return;
        e.preventDefault();
        const r = box();
        zoomAt(Math.exp(-e.deltaY * 0.0015), e.clientX - r.left, e.clientY - r.top);
    }

    function sendPlacingKey(e) {
        e.preventDefault();
        dotnet.invokeMethodAsync('PlacingKey', e.key, e.shiftKey);
    }

    function onKeyDown(e) {
        if (e.target !== viewport || e.altKey || e.ctrlKey || e.metaKey) return;
        // While placing, the placing keys are the server's (the arrows move the cross-hair, not the picture); nothing else is sent.
        if (placing() && PLACING_KEYS.has(e.key)) {
            sendPlacingKey(e);
            return;
        }
        switch (e.key) {
            case '+':
            case '=':
                e.preventDefault();
                zoomCentre(1.25);
                return;
            case '-':
            case '_':
                e.preventDefault();
                zoomCentre(0.8);
                return;
            case '0':
                e.preventDefault();
                fit();
                return;
            case 'ArrowLeft':
            case 'ArrowRight':
            case 'ArrowUp':
            case 'ArrowDown': {
                e.preventDefault();
                const step = KEY_PAN * (e.shiftKey ? 4 : 1);
                // The picture moves the other way to the view: ArrowRight shows what is to the right.
                if (e.key === 'ArrowLeft') pan(step, 0);
                else if (e.key === 'ArrowRight') pan(-step, 0);
                else if (e.key === 'ArrowUp') pan(0, step);
                else pan(0, -step);
                return;
            }
        }
    }

    // Escape stops placing from anywhere on the page (the side panel, the placing buttons), except inside a dialog, whose own Escape
    // closes it.
    function onDocumentKeyDown(e) {
        if (e.key !== 'Escape' || e.defaultPrevented || !placing() || e.target === viewport) return;
        if (e.target instanceof Element && e.target.closest('dialog, [role="dialog"]')) return;
        sendPlacingKey(e);
    }

    const resized = new ResizeObserver(() => {
        if (!view.touched) fit();
        else apply();
    });

    viewport.addEventListener('pointerdown', onPointerDown);
    viewport.addEventListener('pointermove', onPointerMove);
    viewport.addEventListener('pointerup', onPointerUp);
    viewport.addEventListener('pointercancel', onPointerUp);
    viewport.addEventListener('dblclick', onDoubleClick);
    viewport.addEventListener('wheel', onWheel, { passive: false });
    viewport.addEventListener('keydown', onKeyDown);
    document.addEventListener('keydown', onDocumentKeyDown);
    resized.observe(viewport);
    fit();

    return {
        zoomIn: () => zoomCentre(1.25),
        zoomOut: () => zoomCentre(0.8),
        fit,
        // Keeps a point of the picture (the cross-hair) in sight, panning only when it is near an edge.
        reveal: (x, y) => {
            const r = box();
            const margin = Math.min(48, r.width / 4, r.height / 4);
            const sx = view.x + x * view.scale;
            const sy = view.y + y * view.scale;
            let dx = 0;
            let dy = 0;
            if (sx < margin) dx = margin - sx;
            else if (sx > r.width - margin) dx = r.width - margin - sx;
            if (sy < margin) dy = margin - sy;
            else if (sy > r.height - margin) dy = r.height - margin - sy;
            if (dx || dy) pan(dx, dy);
        },
        dispose: () => {
            resized.disconnect();
            viewport.removeEventListener('pointerdown', onPointerDown);
            viewport.removeEventListener('pointermove', onPointerMove);
            viewport.removeEventListener('pointerup', onPointerUp);
            viewport.removeEventListener('pointercancel', onPointerUp);
            viewport.removeEventListener('dblclick', onDoubleClick);
            viewport.removeEventListener('wheel', onWheel);
            viewport.removeEventListener('keydown', onKeyDown);
            document.removeEventListener('keydown', onDocumentKeyDown);
        }
    };
}
