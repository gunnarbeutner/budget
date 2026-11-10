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
// focus to the triggering element on close. The Modal component calls activate() once the dialog
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
            var items = focusableIn(modal);
            (items[0] || modal).focus();

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
        var row = e.target.closest && e.target.closest(".tx-row");
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
document.addEventListener("mousedown", function (e) {
    if (e.button !== 2) return;
    var el = e.target instanceof Element ? e.target.closest("[data-ob-longpress]") : null;
    if (el) e.preventDefault();
}, true);
