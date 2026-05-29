// Small UI helpers that don't need a full Blazor JSInterop bridge — just a couple of
// global event listeners attached once at page load.

// Click-away to close any <details class="project-selector"> dropdown. The native
// <details> only toggles on summary clicks; users expect dropdowns to close when
// they click elsewhere on the page.
document.addEventListener("click", function (e) {
    document.querySelectorAll("details.project-selector[open]").forEach(function (d) {
        if (!d.contains(e.target)) {
            d.open = false;
        }
    });
});

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
