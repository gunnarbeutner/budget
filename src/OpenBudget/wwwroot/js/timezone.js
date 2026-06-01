// Carry the browser's IANA timezone to the server in a cookie. Runs synchronously in <head>
// before blazor.server.js connects, so the negotiate/websocket requests already carry the
// cookie and the very first interactive render knows the right zone (see ClockService / App.razor).
(function () {
    function readCookie(name) {
        var m = document.cookie.match(new RegExp("(?:^|; )" + name + "=([^;]*)"));
        return m ? decodeURIComponent(m[1]) : null;
    }

    try {
        var tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        if (!tz) return;

        var existing = readCookie("tz");
        if (existing === tz) return;

        document.cookie = "tz=" + encodeURIComponent(tz) + ";path=/;max-age=31536000;samesite=lax";

        // First-ever visit: the page was already rendered server-side without the cookie. Reload
        // once so the server picks up the zone immediately. Only on the absent-cookie case — a
        // changed zone (travel) is picked up on the next navigation and never forces a reload,
        // so this can't loop.
        if (!existing) location.reload();
    } catch (e) {
        // Intl unavailable or cookies blocked (private mode): fall back to the server default.
    }
})();
