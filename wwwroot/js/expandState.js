// Tiny localStorage helpers for hierarchical row expansion state.
// Key namespace: "expand:<scope>"; value is a comma-separated list of expanded keys.
// hasPreference distinguishes "user explicitly chose (possibly empty) set" from "no
// preference yet" so the page can fall back to its own auto-default in the latter case.

window.expandState = {
    hasPreference: function (scope) {
        try {
            return window.localStorage.getItem("expand:" + scope) !== null;
        } catch (_) {
            return false;
        }
    },
    get: function (scope) {
        try {
            const raw = window.localStorage.getItem("expand:" + scope);
            return raw ? raw.split(",").filter(s => s.length > 0) : [];
        } catch (_) {
            return [];
        }
    },
    set: function (scope, keys) {
        try {
            window.localStorage.setItem("expand:" + scope, (keys || []).join(","));
        } catch (_) {
            // localStorage may be unavailable (private mode, quota); fall through silently.
        }
    }
};
