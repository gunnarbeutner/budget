// Shows the remote browser of a web login in a canvas and forwards mouse/keyboard/paste to the server over a WebSocket
// (/browser/ws/<session id>, authenticated by the app's cookie). No eval, no external code. Frames arrive as binary JPEGs;
// only the newest frame is decoded and painted (once per animation frame). Input goes back as small JSON messages;
// hover moves and wheel deltas are coalesced per frame; drag paths retain every event. Nothing typed is kept here.
export function attach(canvas, dotnet, sessionId) {
    const ctx = canvas.getContext('2d');
    let cssW = 1366, cssH = 850;
    let disposed = false;
    let ws = null, wsOpen = false, firstFrame = false;

    // ---- frames: decode newest only, paint on animation frame ----
    let latest = null, raf = 0, decoding = false, pendingFrame = null;
    const paint = () => {
        raf = 0;
        const b = latest;
        if (!b || disposed) return;
        latest = null;
        if (canvas.width !== b.width || canvas.height !== b.height) { canvas.width = b.width; canvas.height = b.height; }
        ctx.drawImage(b, 0, 0);
        b.close();
        if (!firstFrame) { firstFrame = true; dotnet.invokeMethodAsync('OnFirstFrame').catch(() => {}); }
    };
    const decode = async () => {
        if (decoding) return;
        decoding = true;
        try {
            while (pendingFrame && !disposed) {
                const v = pendingFrame; pendingFrame = null;
                cssW = (v[1] << 8) | v[2]; cssH = (v[3] << 8) | v[4];
                const bmp = await createImageBitmap(new Blob([v.subarray(5)], { type: 'image/jpeg' }));
                if (disposed) { bmp.close(); return; }
                if (latest) latest.close();
                latest = bmp;
                // hidden tabs get no animation frames: paint at once
                if (document.hidden) paint(); else if (!raf) raf = requestAnimationFrame(paint);
            }
        } catch { /* broken frame: the next one repairs it */ }
        finally { decoding = false; }
    };
    const onFrame = (data) => { pendingFrame = data instanceof Uint8Array ? data : new Uint8Array(data); decode(); };

    // ---- messages to .NET ----
    const send = (o) => {
        if (disposed) return;
        const s = JSON.stringify(o);
        if (wsOpen) ws.send(s);
    };

    const pos = (e) => {
        const r = canvas.getBoundingClientRect();
        const x = (e.clientX - r.left) / r.width * cssW, y = (e.clientY - r.top) / r.height * cssH;
        return [Math.min(Math.max(x, 0), cssW), Math.min(Math.max(y, 0), cssH)];
    };
    const stamp = (e) => (e.timeStamp > 1e12 ? e.timeStamp : performance.timeOrigin + e.timeStamp) / 1000;
    const btn = (e) => e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left';
    const mods = (e) => (e.altKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.metaKey ? 4 : 0) | (e.shiftKey ? 8 : 0);

    // Hover / wheel: at most one message per animation frame, flushed before other input to keep the order.
    let move = null, wheel = null, flushRaf = 0, pressed = 0;
    const flush = () => {
        flushRaf = 0;
        if (move) { const m = move; move = null; send({ t: 'm', k: 'move', x: m.x, y: m.y, bs: m.bs, m: m.m, ts: m.ts }); }
        if (wheel) { const w = wheel; wheel = null; send({ t: 'm', k: 'wheel', x: w.x, y: w.y, dx: w.dx, dy: w.dy, m: w.m, ts: w.ts }); }
    };
    const tick = new MessageChannel(), tickPort = tick.port2;
    tick.port1.onmessage = () => flush();
    const schedule = () => {
        if (flushRaf) return;
        // animation frames stop in hidden tabs (and timers are throttled there): use a message-channel task instead
        if (document.hidden) { flushRaf = 1; tickPort.postMessage(0); } else flushRaf = requestAnimationFrame(flush);
    };

    const on = (target, t, f, o) => { target.addEventListener(t, f, o); return () => target.removeEventListener(t, f, o); };
    const offs = [];
    // while a button is held the events come from the window, so that dragging out of the canvas keeps selecting
    offs.push(on(window, 'mousemove', e => {
        if (!pressed && e.target !== canvas) return;
        const [x, y] = pos(e);
        if (e.buttons) {
            // A drag's path and timing matter (sliders, drawing and verification widgets).
            flush();
            send({ t: 'm', k: 'move', x, y, bs: e.buttons, m: mods(e), ts: stamp(e) });
        } else {
            move = { x, y, bs: e.buttons, m: mods(e), ts: stamp(e) };
            schedule();
        }
    }));
    offs.push(on(canvas, 'mousedown', e => {
        canvas.focus({ preventScroll: true });
        flush();
        pressed = e.buttons || 1;
        const [x, y] = pos(e);
        send({ t: 'm', k: 'down', x, y, b: btn(e), bs: e.buttons, c: e.detail || 1, m: mods(e), ts: stamp(e) });
        e.preventDefault();
    }));
    offs.push(on(window, 'mouseup', e => {
        if (!pressed) return;
        flush();
        pressed = e.buttons;
        const [x, y] = pos(e);
        send({ t: 'm', k: 'up', x, y, b: btn(e), bs: e.buttons, c: e.detail || 1, m: mods(e), ts: stamp(e) });
        e.preventDefault();
    }));
    offs.push(on(canvas, 'contextmenu', e => e.preventDefault()));
    offs.push(on(canvas, 'wheel', e => {
        const [x, y] = pos(e);
        const k = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? cssH : 1;
        if (move) flush();
        wheel = { x, y, dx: (wheel?.dx || 0) + e.deltaX * k, dy: (wheel?.dy || 0) + e.deltaY * k, m: mods(e), ts: stamp(e) };
        schedule();
        e.preventDefault();
    }, { passive: false }));

    // Dead keys (^ ´ ` on German keyboards): composition events only fire in text fields, not on this canvas, so the accent
    // is remembered here and combined with the next key: ^ + a = â, ^ + space = ^, ^ + x = ^x.
    const COMBINING = { '^': '\u0302', '´': '\u0301', '`': '\u0300', '~': '\u0303', '¨': '\u0308' };
    let layout = null;
    try { navigator.keyboard?.getLayoutMap?.().then(m => { layout = m; }, () => {}); } catch { /* not supported (Safari, Firefox) */ }
    const deadChar = (e) => {
        const base = layout?.get(e.code);
        if (base && !e.shiftKey && COMBINING[base]) return base;
        if (e.code === 'Backquote' || e.code === 'IntlBackslash') return e.shiftKey ? null : '^'; // German: ^ (Mac ISO reports either code)
        if (e.code === 'Equal') return e.shiftKey ? '`' : '´';
        if (e.code === 'BracketRight' && e.altKey) return '~';
        return base && COMBINING[base] ? base : '^';
    };
    let dead = null;
    const sendText = (text) => { flush(); send({ t: 'text', text }); };
    const isText = (e) => e.key.length === 1 && (!e.ctrlKey && !e.metaKey || (e.ctrlKey && e.altKey));
    offs.push(on(canvas, 'keydown', e => {
        // Ctrl/Cmd+V: the browser fires a paste event with the local clipboard text (below)
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'v') return;
        if (e.key === 'Dead') {
            if (dead) sendText(dead); // two dead keys in a row: the first one stands alone
            dead = deadChar(e);
            e.preventDefault();
            return;
        }
        if (e.key === 'Process' || e.key === 'Unidentified' || e.isComposing) return;
        if (dead && !['Shift', 'Alt', 'Control', 'Meta', 'CapsLock', 'AltGraph'].includes(e.key)) {
            const accent = dead; dead = null;
            if (e.key === ' ') { sendText(accent); e.preventDefault(); return; }
            if (isText(e)) {
                const composed = /^[\x00-\x7f]$/.test(e.key) ? (e.key + COMBINING[accent]).normalize('NFC') : e.key;
                // the browser may already have composed it (â arrives as one key); otherwise accent and key stay separate
                sendText(composed.length === 1 ? composed : accent + e.key);
                e.preventDefault();
                return;
            }
            sendText(accent); // e.g. ^ then Backspace: the accent appears first, the key is handled as usual
        }
        flush();
        if (isText(e)) {
            const m = e.ctrlKey && e.altKey ? 0 : mods(e) & ~8; // AltGr characters carry no modifiers
            send({ t: 'k', k: 'down', key: e.key, code: e.code, text: e.key, m });
        } else {
            // Ctrl/Cmd+A is recognised on the server; pasting goes through the paste event above
            send({ t: 'k', k: 'raw', key: e.key, code: e.code, text: e.key === 'Enter' ? '\r' : e.key === 'Tab' ? '\t' : null, m: mods(e) });
            if (e.key === 'Enter') send({ t: 'k', k: 'char', key: e.key, code: e.code, text: '\r', m: mods(e) });
        }
        e.preventDefault();
    }));
    offs.push(on(canvas, 'keyup', e => {
        if (e.key === 'Dead' || e.key === 'Process' || e.key === 'Unidentified') return;
        flush();
        send({ t: 'k', k: 'up', key: e.key, code: e.code, m: isText(e) ? 0 : mods(e) });
        e.preventDefault();
    }));
    // dead keys / composed characters (e.g. ^ + a, IME) arrive here
    offs.push(on(canvas, 'beforeinput', e => { if (e.isComposing || e.inputType === 'insertCompositionText') e.preventDefault(); }));
    offs.push(on(canvas, 'compositionend', e => { if (e.data) { dead = null; send({ t: 'text', text: e.data }); } }));
    offs.push(on(canvas, 'paste', e => {
        const t = e.clipboardData && e.clipboardData.getData('text');
        if (t) { flush(); send({ t: 'text', text: t }); }
        e.preventDefault();
    }));

    // ---- size of the view: the server matches the frame size to it ----
    const size = () => { const r = canvas.getBoundingClientRect(); return [r.width, r.height, window.devicePixelRatio || 1]; };
    let resizeTimer = 0;
    const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => { const [w, h, dpr] = size(); if (w > 0) send({ t: 'v', w, h, dpr }); }, 200);
    }) : null;
    ro?.observe(canvas);

    // WebSocket to the server; only its owner (cookie + unguessable id) is accepted.
    try {
        const u = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/browser/ws/' + encodeURIComponent(sessionId);
        ws = new WebSocket(u);
        ws.binaryType = 'arraybuffer';
        ws.onopen = () => {
            wsOpen = true;
            const [w, h, dpr] = size();
            if (w > 0) send({ t: 'v', w, h, dpr });
        };
        ws.onmessage = (e) => {
            if (typeof e.data !== 'string') { onFrame(e.data); return; }
            try { const m = JSON.parse(e.data); if (m.t === 'url' && typeof m.url === 'string') dotnet.invokeMethodAsync('OnUrl', m.url).catch(() => {}); } catch { /* ignore */ }
        };
        ws.onclose = ws.onerror = () => {
            const was = wsOpen;
            wsOpen = false;
            if (!disposed) dotnet.invokeMethodAsync('OnSocketClosed', was).catch(() => {});
        };
    } catch { dotnet.invokeMethodAsync('OnSocketClosed', false).catch(() => {}); }

    return {
        focus() { canvas.focus({ preventScroll: true }); },
        nav(k) { send({ t: 'nav', k }); canvas.focus({ preventScroll: true }); },
        dispose() {
            disposed = true;
            clearTimeout(resizeTimer); tick.port1.close(); ro?.disconnect();
            offs.forEach(f => f());
            try { ws?.close(); } catch { /* closed already */ }
            if (latest) { latest.close(); latest = null; }
        },
    };
}
