// Small UI helpers that don't need a full Blazor JSInterop bridge — just a couple of
// global event listeners attached once at page load.

// Click-away to close any <details class="project-selector"> dropdown. The native
// <details> only toggles on summary clicks; users expect dropdowns to close when
// they click elsewhere on the page.
// Following a link inside a menu closes it too (client-side navigation keeps the layout, and with it the open menu).
document.addEventListener("click", function (e) {
    var link = e.target.closest && e.target.closest("a[href]");
    document.querySelectorAll("details.project-selector[open], details.app-menu[open]").forEach(function (d) {
        if (!d.contains(e.target) || (link && d.contains(link))) {
            d.open = false;
        }
    });
});

// Escape closes an open top-bar menu and puts focus back on its button; arrow keys move between its entries.
document.addEventListener("keydown", function (e) {
    var menu = e.target.closest && e.target.closest("details.project-selector[open], details.app-menu[open]");
    // A row's context menu inside the menu (the project switcher's) handles its own keys: Escape closes only it.
    if (!menu || e.target.closest(".ctx-menu")) return;
    if (e.key === "Escape") {
        menu.open = false;
        var summary = menu.querySelector("summary");
        if (summary) summary.focus();
        e.preventDefault();
    } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        var items = Array.prototype.slice.call(menu.querySelectorAll(":scope > :not(summary) a[href], :scope > :not(summary) button:not([disabled])"));
        if (items.length === 0) return;
        var i = items.indexOf(document.activeElement);
        i = e.key === "ArrowDown" ? (i + 1) % items.length : (i <= 0 ? items.length - 1 : i - 1);
        items[i].focus();
        e.preventDefault();
    }
});

// The last project the user opened, so "/" can take them back to it (read server-side by the start page).
window.obNav = {
    rememberProject: function (id) {
        document.cookie = "ob.lastProject=" + encodeURIComponent(id) + ";path=/;max-age=31536000;samesite=lax";
    },
    // Called by MainLayout after its first interactive render: the prerendered markup has been replaced.
    markInteractive: function () {
        document.documentElement.setAttribute("data-interactive", "");
    }
};

// Modal accessibility: move focus into the dialog, trap Tab within it while open, and restore
// focus to the triggering element on close. On open, focus goes to the element marked data-initial-focus, else the
// first form field of the body, else the footer's primary button, never the close button. The Modal component calls
// activate() once the dialog
// renders and deactivate() when it closes or is disposed. Keeping this state here (rather than in
// C#) avoids a DotNetObjectReference just to cycle Tab. Esc-to-close stays on the component's
// keydown handler — this only manages focus.
window.modalA11y = (function () {
    let returnFocusEl = null;
    let trapEl = null;
    let keydownHandler = null;

    function focusableIn(container) {
        return Array.from(container.querySelectorAll(
            'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
        )).filter(function (el) { return el.offsetParent !== null; }); // visible only
    }

    return {
        activate: function (modal) {
            if (!modal) return;
            // Remember where focus was so it can be restored when the dialog closes.
            returnFocusEl = document.activeElement;
            trapEl = modal;
            var visible = function (el) { return el && el.offsetParent !== null && !el.disabled; };
            var fields = Array.from(modal.querySelectorAll('.modal-body input:not([type=hidden]), .modal-body select, .modal-body textarea')).filter(visible);
            var marked = modal.querySelector('[data-initial-focus]');
            var primary = modal.querySelector('.modal-footer .btn-primary, .modal-footer .btn-danger');
            var target = (visible(marked) && marked) || fields[0] || (visible(primary) && primary)
                || focusableIn(modal).find(function (el) { return !el.classList.contains('btn-close'); }) || modal;
            target.focus();

            keydownHandler = function (e) {
                if (e.key !== "Tab") return;
                var f = focusableIn(modal);
                if (f.length === 0) { e.preventDefault(); modal.focus(); return; }
                var first = f[0], last = f[f.length - 1], active = document.activeElement;
                if (e.shiftKey) {
                    if (active === first || !modal.contains(active)) { e.preventDefault(); last.focus(); }
                } else {
                    if (active === last || !modal.contains(active)) { e.preventDefault(); first.focus(); }
                }
            };
            modal.addEventListener("keydown", keydownHandler);
        },
        deactivate: function () {
            if (trapEl && keydownHandler) trapEl.removeEventListener("keydown", keydownHandler);
            trapEl = null; keydownHandler = null;
            if (returnFocusEl && typeof returnFocusEl.focus === "function") {
                try { returnFocusEl.focus(); } catch (e) { /* trigger gone */ }
            }
            returnFocusEl = null;
        }
    };
})();

// Transaction list shortcuts and touch support (Browse page). The page attaches one handler with a
// DotNetObjectReference; keys are decided here (so preventDefault can be conditional) and forwarded
// to OnShortcut(key, shift, ctrlOrMeta). Typing in inputs, open dialogs and open popups
// ([data-ob-overlay]) are never hijacked.
window.obTx = (function () {
    let ref = null;
    let keyHandler = null;
    let touchStart = null, touchTimer = null, suppressClickUntil = 0;

    function typingTarget(el) {
        if (!el || !el.tagName) return false;
        var tag = el.tagName.toLowerCase();
        return tag === "input" || tag === "textarea" || tag === "select" || el.isContentEditable === true;
    }

    function overlayOpen() {
        return document.querySelector("[data-ob-overlay], .modal.show") !== null;
    }

    var plain = new Set(["ArrowUp", "ArrowDown", " ", "c", "C", "n", "N", "x", "X", "/", "?", "Escape"]);

    function onKey(e) {
        if (!ref || e.defaultPrevented || e.altKey) return;
        if (typingTarget(e.target) || overlayOpen()) return;
        var mod = e.ctrlKey || e.metaKey;
        var handled = false;
        if (mod) {
            handled = (e.key === "a" || e.key === "A") && !e.shiftKey;
        } else {
            handled = plain.has(e.key);
            // Space must keep activating a focused button/link/tree row.
            if (e.key === " ") {
                var t = e.target && e.target.tagName ? e.target.tagName.toLowerCase() : "";
                if (t === "button" || t === "a" || (e.target && e.target.getAttribute && e.target.getAttribute("role") === "treeitem")) handled = false;
            }
        }
        if (!handled) return;
        e.preventDefault();
        ref.invokeMethodAsync("OnShortcut", e.key, e.shiftKey, mod);
    }

    function startPress(e) {
        if (e.touches.length !== 1) return;
        var row = window.obTxMenuRow(e.target);
        if (!row) return;
        var t = e.touches[0];
        touchStart = { x: t.clientX, y: t.clientY };
        touchTimer = setTimeout(function () {
            touchTimer = null;
            suppressClickUntil = Date.now() + 800;
            row.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: touchStart.x, clientY: touchStart.y }));
        }, 550);
    }

    function movePress(e) {
        if (!touchTimer || !touchStart) return;
        var t = e.touches[0];
        if (Math.abs(t.clientX - touchStart.x) > 10 || Math.abs(t.clientY - touchStart.y) > 10) endPress();
    }

    function endPress() {
        if (touchTimer) { clearTimeout(touchTimer); touchTimer = null; }
    }

    function swallowClick(e) {
        if (Date.now() < suppressClickUntil) { e.stopPropagation(); e.preventDefault(); }
    }

    return {
        attach: function (dotnetRef) {
            this.detach();
            ref = dotnetRef;
            keyHandler = onKey;
            document.addEventListener("keydown", keyHandler);
            document.addEventListener("touchstart", startPress, { passive: true });
            document.addEventListener("touchmove", movePress, { passive: true });
            document.addEventListener("touchend", endPress, { passive: true });
            document.addEventListener("touchcancel", endPress, { passive: true });
            document.addEventListener("click", swallowClick, true);
        },
        detach: function () {
            if (keyHandler) document.removeEventListener("keydown", keyHandler);
            document.removeEventListener("touchstart", startPress);
            document.removeEventListener("touchmove", movePress);
            document.removeEventListener("touchend", endPress);
            document.removeEventListener("touchcancel", endPress);
            document.removeEventListener("click", swallowClick, true);
            keyHandler = null; ref = null; endPress();
        },
        // Scroll a transaction row into view if it is rendered (Virtualize may not have it).
        reveal: function (id) {
            var el = document.querySelector('[data-tx-id="' + id + '"]');
            if (el && el.scrollIntoView) el.scrollIntoView({ block: "nearest" });
        },
        // Where a popup for a row should open (viewport coordinates), or null when the row is not rendered.
        rowAnchor: function (id) {
            var el = document.querySelector('[data-tx-id="' + id + '"] .tx-row');
            if (!el) return null;
            var r = el.getBoundingClientRect();
            return [r.left + Math.min(r.width / 2, 320), r.bottom];
        },
        focusSearch: function () {
            var el = document.querySelector('[data-testid="tx-search"]');
            if (el) { el.focus(); el.select && el.select(); }
        }
    };
})();

// Keeps the highlighted entry of a popup list (category picker, context menu) in view.
window.obScrollActive = function (container) {
    if (!container) return;
    var el = container.querySelector(".cp-active, .ctx-active");
    if (el && el.scrollIntoView) el.scrollIntoView({ block: "nearest" });
};

// Places a context menu opened at (x, y) so it fits the window: below/right of the pointer when there is room,
// otherwise above/left of it, always at least a margin from the edges; a menu taller than the window scrolls inside.
window.obPlaceMenu = function (el, x, y) {
    if (!el) return;
    var m = 8, vw = window.innerWidth, vh = window.innerHeight;
    el.style.maxHeight = (vh - 2 * m) + "px";
    var r = el.getBoundingClientRect();
    var w = r.width, h = Math.min(r.height, vh - 2 * m);
    var left = x + w + m > vw ? x - w : x;
    var top = y + h + m > vh ? y - h : y;
    left = Math.max(m, Math.min(left, vw - w - m));
    top = Math.max(m, Math.min(top, vh - h - m));
    el.style.left = left + "px";
    el.style.top = top + "px";
    el.style.visibility = "visible";
};

// Places the category picker for the point (x, y): below it when at least 360px are free there, otherwise on the side
// with more room (growing upward from the point), and gives the list the free height of that side minus a margin,
// at least 120px and at most 70% of the window. Horizontally it stays inside the window. It follows window resizes
// and scrolling while it is open.
window.obPlacePicker = function (panel, list, x, y) {
    if (!panel || !list) return;
    var place = function () {
        var m = 8, vw = window.innerWidth, vh = window.innerHeight;
        var below = vh - y - m, above = y - m;
        var down = below >= 360 || below >= above;
        var chrome = panel.offsetHeight - list.offsetHeight;
        var space = (down ? below : above) - chrome;
        list.style.maxHeight = Math.max(120, Math.min(space, vh * 0.7 - chrome)) + "px";
        var w = panel.offsetWidth, h = panel.offsetHeight;
        panel.style.left = Math.max(m, Math.min(x, vw - w - m)) + "px";
        if (down) {
            panel.style.bottom = "auto";
            panel.style.top = Math.max(m, Math.min(y, vh - h - m)) + "px";
        } else {
            panel.style.top = "auto";
            panel.style.bottom = Math.max(m, Math.min(vh - y, vh - h - m)) + "px";
        }
        panel.dataset.placement = down ? "below" : "above";
        panel.style.visibility = "visible";
    };
    panel.__obPlace = place;
    place();
    if (!panel.__obPlaceBound) {
        panel.__obPlaceBound = true;
        var onChange = function (e) {
            if (e && e.target instanceof Node && panel.contains(e.target)) return;
            if (!panel.isConnected) {
                window.removeEventListener("resize", onChange);
                window.removeEventListener("scroll", onChange, true);
                return;
            }
            panel.__obPlace();
        };
        window.addEventListener("resize", onChange);
        window.addEventListener("scroll", onChange, true);
    }
};

// Places the open submenu of a context menu beside its entry: to the right, or to the left when there is no room,
// top-aligned with the entry and kept inside the window like the menu itself.
window.obPlaceSubmenu = function (menu) {
    if (!menu) return;
    var sub = menu.querySelector(".ctx-submenu");
    var item = menu.querySelector(".ctx-item.ctx-open");
    if (!sub || !item) return;
    var m = 8, vw = window.innerWidth, vh = window.innerHeight;
    sub.style.maxHeight = (vh - 2 * m) + "px";
    var a = item.getBoundingClientRect(), r = sub.getBoundingClientRect();
    var w = r.width, h = Math.min(r.height, vh - 2 * m);
    var left = a.right + w + m > vw ? a.left - w : a.right;
    left = Math.max(m, Math.min(left, vw - w - m));
    var top = Math.max(m, Math.min(a.top - 4, vh - h - m));
    sub.style.left = left + "px";
    sub.style.top = top + "px";
    sub.style.visibility = "visible";
};

window.obFocus = function (el) {
    if (el && el.focus) el.focus();
};

// Focus a text field and select its content (inline rename in the sidebar).
window.obFocusSelect = function (el) {
    if (!el || !el.focus) return;
    el.focus();
    if (el.select) el.select();
};

// Sidebar drag and drop (rows marked [data-ob-drag]): Blazor tracks what is dragged where; Firefox only starts a
// drag when the data transfer carries something.
document.addEventListener("dragstart", function (e) {
    var el = e.target && e.target.closest && e.target.closest("[data-ob-drag]");
    if (!el || !e.dataTransfer) return;
    e.dataTransfer.effectAllowed = "move";
    try { e.dataTransfer.setData("text/plain", el.getAttribute("title") || ""); } catch (err) { /* older browsers */ }
});

// Dropping on a group row moves into the group; its top edge instead places the row before the group. The edge is
// marked while dragging, and obDropBefore tells Blazor where the last drop landed (it asks after the drop).
(function () {
    var row = null, before = false;
    function clear() { if (row) row.classList.remove("drop-edge-before"); }
    document.addEventListener("dragstart", function () { clear(); row = null; before = false; });
    document.addEventListener("dragover", function (e) {
        var r = e.target && e.target.closest && e.target.closest("[data-ob-drop-group]");
        if (r !== row) { clear(); row = r; }
        if (!r) { before = false; return; }
        var box = r.getBoundingClientRect();
        before = e.clientY - box.top < box.height * 0.3;
        r.classList.toggle("drop-edge-before", before);
    }, true);
    document.addEventListener("dragend", clear);
    document.addEventListener("drop", clear, true);
    window.obDropBefore = function () { return before; };
})();

// Resizable Browse sidebar: drag the handle on its right edge (or focus it and use the arrow keys); double-click
// resets. The width is a CSS variable on <html>, so Blazor re-renders never undo it, and is remembered per browser.
(function () {
    var MIN = 180, MAX = 640, DEFAULT = 250, KEY = "ob.sidebarWidth";
    function current() {
        var v = parseInt(getComputedStyle(document.documentElement).getPropertyValue("--ob-sidebar-w"), 10);
        return isNaN(v) ? DEFAULT : v;
    }
    function set(w, save) {
        w = Math.round(Math.min(MAX, Math.max(MIN, w)));
        document.documentElement.style.setProperty("--ob-sidebar-w", w + "px");
        if (save) { try { localStorage.setItem(KEY, String(w)); } catch (e) { /* storage blocked */ } }
    }
    document.addEventListener("pointerdown", function (e) {
        var handle = e.target.closest && e.target.closest(".browse-resizer");
        if (!handle || e.button !== 0) return;
        e.preventDefault();
        var shell = handle.parentElement, left = shell.getBoundingClientRect().left;
        handle.setPointerCapture(e.pointerId);
        handle.classList.add("dragging");
        document.body.classList.add("resizing-sidebar");
        function move(ev) { set(ev.clientX - left, false); }
        function up() {
            handle.removeEventListener("pointermove", move);
            handle.removeEventListener("pointerup", up);
            handle.removeEventListener("pointercancel", up);
            handle.classList.remove("dragging");
            document.body.classList.remove("resizing-sidebar");
            set(current(), true);
        }
        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", up);
        handle.addEventListener("pointercancel", up);
    });
    document.addEventListener("dblclick", function (e) {
        if (e.target.closest && e.target.closest(".browse-resizer")) {
            set(DEFAULT, false);
            try { localStorage.removeItem(KEY); } catch (err) { /* storage blocked */ }
        }
    });
    document.addEventListener("keydown", function (e) {
        if (!e.target.classList || !e.target.classList.contains("browse-resizer")) return;
        var step = e.shiftKey ? 50 : 10;
        if (e.key === "ArrowLeft") { set(current() - step, true); e.preventDefault(); }
        else if (e.key === "ArrowRight") { set(current() + step, true); e.preventDefault(); }
    });
})();

// The row whose menu a right-click / long-press on a booking opens: the booking's row, also from its expanded details,
// but not from the day header, text fields (they keep the browser's menu for copy/paste) or links.
window.obTxMenuRow = function (target) {
    if (!(target instanceof Element)) return null;
    var item = target.closest(".tx-item");
    if (!item || target.closest(".tx-day, input, textarea, select, a, [contenteditable]")) return null;
    return item.querySelector(":scope > .tx-row");
};

// A right-click in a booking's expanded details opens the row's menu, as a right-click on the row does.
document.addEventListener("contextmenu", function (e) {
    if (!(e.target instanceof Element) || !e.target.closest(".tx-details")) return;
    var row = window.obTxMenuRow(e.target);
    if (!row) return;
    e.preventDefault();
    e.stopPropagation();
    row.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: e.clientX, clientY: e.clientY }));
}, true);

// Long-press on touch opens the context menu of an element marked [data-ob-longpress] (the sidebar's accounts and
// categories, see BrowseTreeRail's node menu): a held finger dispatches "contextmenu" like a right-click; the click
// that follows the release is swallowed so the row is not also selected.
// Where a keyboard-opened context menu goes: below the focused element.
window.obFocusAnchor = function () {
    var el = document.activeElement;
    if (!el || el === document.body) return null;
    var r = el.getBoundingClientRect();
    return [r.left + 24, r.bottom];
};

(function () {
    var start = null, timer = null, suppressUntil = 0;
    function end() { if (timer) { clearTimeout(timer); timer = null; } }
    document.addEventListener("touchstart", function (e) {
        if (e.touches.length !== 1) return;
        var el = e.target.closest && e.target.closest("[data-ob-longpress]");
        if (!el) return;
        var t = e.touches[0];
        start = { x: t.clientX, y: t.clientY };
        timer = setTimeout(function () {
            timer = null;
            suppressUntil = Date.now() + 800;
            el.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: start.x, clientY: start.y }));
        }, 550);
    }, { passive: true });
    document.addEventListener("touchmove", function (e) {
        if (!timer || !start) return;
        var t = e.touches[0];
        if (Math.abs(t.clientX - start.x) > 10 || Math.abs(t.clientY - start.y) > 10) end();
    }, { passive: true });
    document.addEventListener("touchend", end, { passive: true });
    document.addEventListener("touchcancel", end, { passive: true });
    document.addEventListener("click", function (e) {
        if (Date.now() < suppressUntil && e.target.closest && e.target.closest("[data-ob-longpress]")) { e.stopPropagation(); e.preventDefault(); }
    }, true);
})();

// Selecting text in a transaction row must not also toggle the row: swallow the click that ends a drag-selection.
window.addEventListener("click", function (e) {
    var row = e.target instanceof Element ? e.target.closest(".tx-row") : null;
    if (!row) return;
    var sel = window.getSelection();
    if (sel && !sel.isCollapsed && sel.toString().trim().length > 0 && row.contains(sel.anchorNode)) {
        e.stopPropagation();
        e.preventDefault();
    }
}, true);

// Closes the open top-bar menus (the project switcher before it opens one of its dialogs).
window.obCloseMenus = function () {
    document.querySelectorAll("details.project-selector[open], details.app-menu[open]").forEach(function (d) { d.open = false; });
};

// Dragging a project switcher row to reorder: mark it as a move (Firefox only starts a drag with data set) so the
// browser doesn't treat it as dragging the row's link somewhere else.
document.addEventListener("dragstart", function (e) {
    var row = e.target.closest && e.target.closest(".project-switcher-row[draggable=true]");
    if (!row || !e.dataTransfer) return;
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", row.getAttribute("data-project-id") || "");
});

// A right-click on a row with a context menu must not first select the word under the pointer (macOS does that on
// mousedown): keep the secondary button's mousedown from starting a selection there.
// Safari selects the word on the contextmenu event itself, so a selection it made there is cleared right after.
document.addEventListener("contextmenu", function (e) {
    var el = e.target instanceof Element ? e.target.closest("[data-ob-longpress]") : null;
    if (!el || e.target.closest("input, textarea, [contenteditable]")) return;
    setTimeout(function () {
        var sel = window.getSelection();
        if (sel && !sel.isCollapsed && el.contains(sel.anchorNode)) sel.removeAllRanges();
    }, 0);
}, true);

document.addEventListener("mousedown", function (e) {
    if (e.button !== 2) return;
    var el = e.target instanceof Element ? e.target.closest("[data-ob-longpress]") : null;
    if (el) e.preventDefault();
}, true);

// Shift/Cmd/Ctrl-click on a booking extends or toggles the selection; without this the browser would also select the
// text between the two clicks.
document.addEventListener("mousedown", function (e) {
    if (e.button !== 0 || !(e.shiftKey || e.metaKey || e.ctrlKey)) return;
    var row = e.target instanceof Element ? e.target.closest(".tx-row") : null;
    if (row && !e.target.closest("input, textarea, select, [contenteditable]")) e.preventDefault();
}, true);

// Category picker: toggles .cp-has-more on the list's wrapper while content remains below the visible part (fade + ▾ hint).
window.obPickerMoreHint = function (list) {
    if (!list || !list.parentElement) return;
    var update = function () {
        var more = list.scrollHeight - list.scrollTop - list.clientHeight > 4;
        list.parentElement.classList.toggle("cp-has-more", more);
    };
    if (!list.__obMoreHint) {
        list.__obMoreHint = true;
        list.addEventListener("scroll", update, { passive: true });
    }
    update();
};

// While a dialog is open, Tab and Shift+Tab cycle through the topmost dialog's own controls instead of reaching the page
// behind it; focus that lands outside (a click on the backdrop page, say) is pulled back in.
(function () {
    var focusable = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), ' +
        'textarea:not([disabled]), [tabindex]:not([tabindex="-1"]), [contenteditable="true"]';
    function topDialog() {
        var open = document.querySelectorAll('.modal.show[aria-modal="true"]');
        if (open.length === 0) return null;
        var top = open[0];
        open.forEach(function (m) { if (m.classList.contains("ob-modal-on-top")) top = m; });
        return top;
    }
    function items(dialog) {
        return Array.prototype.filter.call(dialog.querySelectorAll(focusable), function (el) {
            return el.offsetParent !== null || el === document.activeElement;
        });
    }
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Tab") return;
        var dialog = topDialog();
        if (!dialog) return;
        var list = items(dialog);
        if (list.length === 0) { e.preventDefault(); dialog.focus(); return; }
        var first = list[0], last = list[list.length - 1];
        var inside = dialog.contains(document.activeElement);
        if (e.shiftKey && (document.activeElement === first || !inside)) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && (document.activeElement === last || !inside)) { e.preventDefault(); first.focus(); }
    }, true);
    document.addEventListener("focusin", function (e) {
        var dialog = topDialog();
        if (!dialog || dialog.contains(e.target)) return;
        // a second open dialog (a picker over a dialog) keeps its own focus
        if (e.target.closest && e.target.closest('.modal.show, .dropdown-menu.show, [data-ob-popup]')) return;
        var list = items(dialog);
        (list[0] || dialog).focus();
    });
})();

// --ob-topbar-h follows the top bar's real height: below 1200px the period selector takes a second row, and the
// sticky page toolbars sit under the whole bar.
(function () {
    if (typeof ResizeObserver === "undefined") return;
    var observed = null;
    var ro = new ResizeObserver(function (entries) {
        var h = Math.round(entries[0].target.getBoundingClientRect().height);
        if (h > 0) document.documentElement.style.setProperty("--ob-topbar-h", h + "px");
    });
    function attach() {
        var bar = document.querySelector(".app-topbar");
        if (bar && bar !== observed) {
            if (observed) ro.unobserve(observed);
            observed = bar;
            ro.observe(bar);
        }
    }
    new MutationObserver(attach).observe(document.documentElement, { childList: true, subtree: true });
    attach();
})();

// Copies text to the clipboard; resolves to whether it worked.
// Safari lets a page write the clipboard only while it handles the click, not after the round trip to the server. So a
// click on an element with data-copy copies right away, and obCopyText (called by the server afterwards) reports that
// copy's outcome; other callers copy then and there.
(function () {
    var pending = null;
    function write(text) {
        if (!navigator.clipboard) return Promise.resolve(false);
        return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
    }
    document.addEventListener("click", function (e) {
        var el = e.target.closest && e.target.closest("[data-copy]");
        if (!el || el.disabled) return;
        pending = { text: el.getAttribute("data-copy"), result: write(el.getAttribute("data-copy")) };
    }, true);
    window.obCopyText = function (text) {
        if (pending && pending.text === text) {
            var result = pending.result;
            pending = null;
            return result;
        }
        return write(text);
    };
})();

// Pending bookings' stripes continue from row to row: each pending row gets its offset within the transaction list as
// --tx-row-top (see .tx-item-pending in site.css). Offsets change when Virtualize moves its window, rows expand or
// collapse, or the list is resized (the spacers' style changes too); each change is handled once per frame.
(function () {
    var scheduled = false;
    var resizes = new ResizeObserver(schedule);
    var observed = new WeakSet();
    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(update);
    }
    function update() {
        scheduled = false;
        document.querySelectorAll(".tx-list").forEach(function (list) {
            if (!observed.has(list)) { observed.add(list); resizes.observe(list); }
            var top = list.getBoundingClientRect().top;
            list.querySelectorAll(".tx-item-pending").forEach(function (row) {
                if (!observed.has(row)) { observed.add(row); resizes.observe(row); }
                var value = (row.getBoundingClientRect().top - top) + "px";
                if (row.style.getPropertyValue("--tx-row-top") !== value) row.style.setProperty("--tx-row-top", value);
            });
        });
    }
    function start() {
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["class", "style"] });
        schedule();
    }
    if (document.body) start(); else document.addEventListener("DOMContentLoaded", start);
})();
