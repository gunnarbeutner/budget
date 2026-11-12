// The chunked upload of the Get started page's MoneyMoney import: sends the picked ZIP in 8 MB pieces (each well
// below reverse-proxy body limits) to /api/moneymoney/uploads/{id}, which the page opened through the circuit
// (dotnet.BeginUpload). After a failed piece it asks the server where it stands and goes on from there, a few
// times with a growing pause. Reports progress to dotnet.UploadProgress, the end to UploadDone or UploadFailed.
window.obMoneyMoneyUpload = (() => {
    const chunkSize = 8 * 1024 * 1024;
    const maxAttempts = 6;
    const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

    async function received(url) {
        const res = await fetch(url, { credentials: 'same-origin', cache: 'no-store' });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return (await res.json()).received;
    }

    async function start(input, dotnet) {
        const file = input && input.files && input.files[0];
        if (!file) { await dotnet.invokeMethodAsync('UploadFailed', 'nofile'); return; }
        let id;
        try {
            id = await dotnet.invokeMethodAsync('BeginUpload', file.size);
        } catch (e) {
            await dotnet.invokeMethodAsync('UploadFailed', 'size');
            return;
        }
        const url = `api/moneymoney/uploads/${encodeURIComponent(id)}`;
        let offset = 0;
        let failures = 0;
        let lastReport = 0;
        while (offset < file.size) {
            try {
                const piece = file.slice(offset, Math.min(offset + chunkSize, file.size));
                const res = await fetch(`${url}?offset=${offset}`, {
                    method: 'PUT', body: piece, credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/octet-stream' },
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                offset = (await res.json()).received;
                failures = 0;
            } catch (e) {
                if (++failures >= maxAttempts) { await dotnet.invokeMethodAsync('UploadFailed', 'network'); return; }
                await pause(1000 * failures);
                try { offset = await received(url); } catch { /* retried with the next attempt */ }
                continue;
            }
            const now = Date.now();
            if (now - lastReport > 250 || offset >= file.size) {
                lastReport = now;
                await dotnet.invokeMethodAsync('UploadProgress', offset, file.size);
            }
        }
        await dotnet.invokeMethodAsync('UploadDone', id);
    }

    return { start };
})();
