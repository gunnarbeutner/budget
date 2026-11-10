// Documents view: PDF thumbnails for the grid and the viewer's pages, rendered with the vendored pdf.js
// (wwwroot/lib/pdfjs, legacy build). Thumbnails are rendered lazily when their card scrolls into view and the
// PNG is cached per statement in Cache Storage, so a statement is rasterised once per browser.

const pdfjsUrl = new URL("../lib/pdfjs/pdf.min.mjs", import.meta.url).href;
const workerUrl = new URL("../lib/pdfjs/pdf.worker.min.mjs", import.meta.url).href;
const CACHE = "ob-statement-thumbs-v1";
const THUMB_WIDTH = 360; // CSS pixels * 2 for sharp cards on HiDPI screens

let pdfjsPromise = null;
function pdfjs() {
    if (!pdfjsPromise) {
        pdfjsPromise = import(pdfjsUrl).then(lib => {
            lib.GlobalWorkerOptions.workerSrc = workerUrl;
            return lib;
        });
    }
    return pdfjsPromise;
}

async function cacheGet(key) {
    try {
        if (!("caches" in window)) return null;
        const cache = await caches.open(CACHE);
        const hit = await cache.match(key);
        return hit ? await hit.blob() : null;
    } catch {
        return null;
    }
}

async function cachePut(key, blob) {
    try {
        if (!("caches" in window)) return;
        const cache = await caches.open(CACHE);
        await cache.put(key, new Response(blob, { headers: { "Content-Type": "image/png" } }));
    } catch {
        /* storage unavailable (private window, quota): render again next time */
    }
}

async function renderThumb(url) {
    const lib = await pdfjs();
    const doc = await lib.getDocument({ url: new URL(url, document.baseURI).href, isEvalSupported: false }).promise;
    try {
        const page = await doc.getPage(1);
        const base = page.getViewport({ scale: 1 });
        const viewport = page.getViewport({ scale: THUMB_WIDTH / base.width });
        const canvas = document.createElement("canvas");
        canvas.width = Math.ceil(viewport.width);
        canvas.height = Math.ceil(viewport.height);
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "#fff";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        await page.render({ canvasContext: ctx, viewport }).promise;
        return await new Promise((resolve, reject) => canvas.toBlob(b => (b ? resolve(b) : reject(new Error("toBlob"))), "image/png"));
    } finally {
        doc.destroy();
    }
}

async function fill(img) {
    const id = img.dataset.statement;
    const url = img.dataset.src;
    const key = new URL(`statement-thumb/${id}`, document.baseURI).href;
    const card = img.closest(".doc-thumb");
    try {
        let blob = await cacheGet(key);
        if (!blob) {
            blob = await renderThumb(url);
            await cachePut(key, blob);
        }
        const objectUrl = URL.createObjectURL(blob);
        img.onload = () => {
            URL.revokeObjectURL(objectUrl);
            // Landscape pages would be cropped to their middle by the portrait card: show them whole.
            card?.classList.toggle("wide", img.naturalWidth > img.naturalHeight);
        };
        img.src = objectUrl;
        card?.classList.add("loaded");
    } catch (e) {
        console.warn("Statement thumbnail failed", e);
        card?.classList.add("failed");
    }
}

let observer = null;

/** Starts (or refreshes) lazy thumbnail rendering for every not yet handled image in the container. */
export function thumbs(container) {
    if (!container || typeof container.querySelectorAll !== "function") return;
    if (!observer) {
        observer = "IntersectionObserver" in window
            ? new IntersectionObserver(entries => {
                for (const e of entries) {
                    if (!e.isIntersecting) continue;
                    observer.unobserve(e.target);
                    fill(e.target);
                }
            }, { rootMargin: "200px" })
            : null;
    }
    for (const img of container.querySelectorAll("img[data-statement]:not([data-queued])")) {
        img.dataset.queued = "1";
        if (observer) observer.observe(img);
        else fill(img);
    }
}

let viewToken = 0;

/** Renders all pages of a PDF into the container at its width; resolves with the page count (0 on failure). */
export async function view(container, url) {
    const token = ++viewToken;
    if (!container) return 0;
    container.replaceChildren();
    try {
        const lib = await pdfjs();
        const doc = await lib.getDocument({ url: new URL(url, document.baseURI).href, isEvalSupported: false }).promise;
        const width = Math.max(320, container.clientWidth - 32);
        const ratio = window.devicePixelRatio || 1;
        for (let n = 1; n <= doc.numPages; n++) {
            if (token !== viewToken) break;
            const page = await doc.getPage(n);
            const base = page.getViewport({ scale: 1 });
            const scale = Math.min(width / base.width, 2.5);
            const viewport = page.getViewport({ scale: scale * ratio });
            const canvas = document.createElement("canvas");
            canvas.className = "doc-page";
            canvas.width = Math.ceil(viewport.width);
            canvas.height = Math.ceil(viewport.height);
            canvas.style.width = `${Math.floor(base.width * scale)}px`;
            canvas.setAttribute("role", "img");
            canvas.setAttribute("aria-label", container.dataset.pageLabel?.replace("{0}", n).replace("{1}", doc.numPages) ?? `${n}`);
            container.appendChild(canvas);
            const ctx = canvas.getContext("2d");
            ctx.fillStyle = "#fff";
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            await page.render({ canvasContext: ctx, viewport }).promise;
        }
        const pages = doc.numPages;
        doc.destroy();
        container.dataset.rendered = String(pages);
        return pages;
    } catch {
        if (token === viewToken) container.dataset.rendered = "0";
        return 0;
    }
}

/** Puts focus on a card of the grid (when the viewer closes). */
export function focusCard(id) {
    document.querySelector(`[data-doc-card="${id}"]`)?.focus();
}

/**
 * Downloads the selected statements: one as the PDF itself, several as a ZIP streamed by POST statements/zip
 * (a plain form post, so the browser streams it to disk and the page stays).
 */
export function download(ids) {
    if (!ids || ids.length === 0) return;
    if (ids.length === 1) {
        const a = document.createElement("a");
        a.href = new URL(`statements/${ids[0]}?download=true`, document.baseURI).href;
        a.download = "";
        document.body.appendChild(a);
        a.click();
        a.remove();
        return;
    }
    const form = document.createElement("form");
    form.method = "post";
    form.action = new URL("statements/zip", document.baseURI).href;
    form.style.display = "none";
    const input = document.createElement("input");
    input.type = "hidden";
    input.name = "ids";
    input.value = ids.join(",");
    form.appendChild(input);
    document.body.appendChild(form);
    form.submit();
    form.remove();
}

let keyHandler = null;

/** Cmd/Ctrl+A selects all visible cards, Escape clears — unless focus is in a field or a dialog is open. */
export function bindSelectionKeys(dotnet) {
    unbindSelectionKeys();
    keyHandler = e => {
        const t = e.target;
        if (t instanceof Element && t.closest("input, textarea, select, [contenteditable], .modal")) return;
        if (document.querySelector(".modal.show")) return;
        if (!document.querySelector(".doc-grid")) return;
        if ((e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === "a") {
            e.preventDefault();
            dotnet.invokeMethodAsync("SelectAllFromKeys").catch(() => { });
        } else if (e.key === "Escape" && document.querySelector(".doc-grid.selecting")) {
            dotnet.invokeMethodAsync("ClearFromKeys").catch(() => { });
        }
    };
    document.addEventListener("keydown", keyHandler);
}

export function unbindSelectionKeys() {
    if (keyHandler) document.removeEventListener("keydown", keyHandler);
    keyHandler = null;
}
