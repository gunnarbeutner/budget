// Starts Blazor with our own reconnection handling.
//
// A tab left in the background gets its timers throttled, misses the server's keep-alives and is
// disconnected. Blazor's default handler then burns through its retries while the tab is still
// throttled and leaves a dead page behind once the server has dropped the circuit. Instead we:
//   - retry only while the tab is visible (and immediately when it becomes visible again),
//   - reload the page when the server no longer knows the circuit (Blazor.reconnect() === false),
//   - reload after a few failed attempts while visible, so the page never stays dead.
(() => {
    const maxVisibleAttempts = 5;
    const retryDelayMs = 2000;
    const german = (navigator.language || '').toLowerCase().startsWith('de');
    const text = german ? 'Verbindung wird wiederhergestellt …' : 'Reconnecting…';

    let banner = null;
    const showBanner = () => {
        if (!banner) {
            banner = document.createElement('div');
            banner.id = 'reconnect-banner';
            banner.setAttribute('role', 'status');
            banner.textContent = text;
            document.body.appendChild(banner);
        }
        banner.style.display = 'block';
    };
    const hideBanner = () => {
        if (banner) banner.style.display = 'none';
    };

    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const whenVisible = () => document.visibilityState === 'visible'
        ? Promise.resolve()
        : new Promise(resolve => {
            const onChange = () => {
                if (document.visibilityState === 'visible') {
                    document.removeEventListener('visibilitychange', onChange);
                    resolve();
                }
            };
            document.addEventListener('visibilitychange', onChange);
        });

    const startReconnecting = () => {
        let canceled = false;
        showBanner();
        (async () => {
            for (let attempt = 0; attempt < maxVisibleAttempts; attempt++) {
                await whenVisible();
                if (canceled) return;
                try {
                    // false = the server no longer has this circuit; only a reload helps.
                    if (!(await Blazor.reconnect())) {
                        location.reload();
                    }
                    return;
                } catch {
                    // Server unreachable (e.g. restarting during a deploy); try again shortly.
                }
                await sleep(retryDelayMs);
                if (canceled) return;
            }
            location.reload();
        })();
        return {
            cancel: () => {
                canceled = true;
                hideBanner();
            },
        };
    };

    let current = null;
    Blazor.start({
        reconnectionHandler: {
            onConnectionDown: () => {
                current ??= startReconnecting();
            },
            onConnectionUp: () => {
                current?.cancel();
                current = null;
            },
        },
    });
})();
