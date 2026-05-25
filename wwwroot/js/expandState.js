// Tiny localStorage helpers for hierarchical row expansion state.
// Key namespace: "expand:<scope>"; value is a comma-separated list of expanded keys.

window.expandState = {
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
