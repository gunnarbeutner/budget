// Follow the OS color-scheme preference. Runs synchronously in <head> before
// stylesheets paint, so the data-bs-theme attribute is on <html> by the time
// Bootstrap's variables resolve — no light-mode flash for dark-mode users.
(function () {
    if (!window.matchMedia) return;
    var mq = window.matchMedia("(prefers-color-scheme: dark)");

    function apply() {
        document.documentElement.setAttribute("data-bs-theme", mq.matches ? "dark" : "light");
    }
    apply();

    if (mq.addEventListener) {
        mq.addEventListener("change", apply);
    } else if (mq.addListener) {
        mq.addListener(apply); // Safari < 14
    }

    // Clean up any stored choice left over from the earlier in-app toggle so
    // returning users aren't pinned to an old preference.
    try { localStorage.removeItem("theme"); } catch (e) { /* private mode */ }
})();

// The Browse sidebar's width (set by dragging its edge; see uiBehaviors.js), restored before the first paint.
(function () {
    try {
        var w = parseInt(localStorage.getItem("ob.sidebarWidth"), 10);
        if (w >= 180 && w <= 640) document.documentElement.style.setProperty("--ob-sidebar-w", w + "px");
    } catch (e) { /* storage blocked: default width */ }
})();
