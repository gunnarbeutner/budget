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
