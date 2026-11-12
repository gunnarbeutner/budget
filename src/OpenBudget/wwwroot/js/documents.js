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

let marquee = null;

/** The nearest scrolling ancestor of an element (the document when none scrolls). */
function scrollerOf(el) {
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
        const oy = getComputedStyle(p).overflowY;
        if ((oy === "auto" || oy === "scroll") && p.scrollHeight > p.clientHeight) return p;
    }
    return document.scrollingElement || document.documentElement;
}

/**
 * Rectangle selection on the document grids (every .doc-grid[data-marquee] inside the [data-marquee-area]) with
 * mouse or pen: dragging from empty space in the main area (not on a card,
 * control or toolbar) draws a rectangle and selects the cards it touches; Shift or Cmd/Ctrl adds to the current
 * selection, otherwise the rectangle replaces it. A plain click on empty space clears the selection. Near the top
 * or bottom edge the view scrolls. The cards hit are highlighted locally while dragging and reported to .NET once
 * on release (SetSelectionFromMarquee with the full selected id set).
 */
export function bindMarquee(dotnet) {
    unbindMarquee();
    const THRESHOLD = 4, EDGE = 40;
    let drag = null;

    const cardsOf = grids => grids.flatMap(grid => [...grid.querySelectorAll(":scope > li")]);
    const idOf = li => li.querySelector("[data-doc-card]")?.dataset.docCard;
    const isDoc = s => s === document.scrollingElement || s === document.documentElement;

    const update = () => {
        const d = drag;
        const sx = d.scroller.scrollLeft, sy = d.scroller.scrollTop;
        const x1 = Math.min(d.x0, d.x + sx) - sx, x2 = Math.max(d.x0, d.x + sx) - sx;
        const y1 = Math.min(d.y0, d.y + sy) - sy, y2 = Math.max(d.y0, d.y + sy) - sy;
        Object.assign(d.box.style, { left: `${x1}px`, top: `${y1}px`, width: `${x2 - x1}px`, height: `${y2 - y1}px` });
        for (const li of cardsOf(d.grids)) {
            const r = li.getBoundingClientRect();
            li.classList.toggle("marquee-hit", r.right >= x1 && r.left <= x2 && r.bottom >= y1 && r.top <= y2);
        }
    };

    const tick = () => {
        if (!drag) return;
        const rect = isDoc(drag.scroller) ? { top: 0, bottom: window.innerHeight } : drag.scroller.getBoundingClientRect();
        const top = Math.max(0, rect.top), bottom = Math.min(window.innerHeight, rect.bottom);
        let dy = 0;
        if (drag.y < top + EDGE) dy = -Math.ceil((top + EDGE - drag.y) / 3);
        else if (drag.y > bottom - EDGE) dy = Math.ceil((drag.y - bottom + EDGE) / 3);
        if (dy !== 0 && drag.box) {
            drag.scroller.scrollTop += dy;
            update();
        }
        drag.raf = requestAnimationFrame(tick);
    };

    const down = e => {
        if (e.button !== 0 || e.pointerType === "touch") return;
        const t = e.target;
        if (!(t instanceof Element)) return;
        if (document.querySelector(".modal.show")) return;
        const grids = [...document.querySelectorAll(".doc-grid[data-marquee]")];
        const grid = grids[0];
        const area = grid?.closest("[data-marquee-area]");
        if (!grid || !area || !area.contains(t)) return;
        if (t.closest(".doc-grid > li, a, button, input, select, textarea, label, [contenteditable], .page-toolbar, .docs-selbar, .modal")) return;
        const scroller = scrollerOf(grid);
        e.preventDefault(); // no text selection while dragging
        drag = {
            grids, scroller, pointerId: e.pointerId, box: null,
            additive: e.shiftKey || e.metaKey || e.ctrlKey,
            x0: e.clientX + scroller.scrollLeft, y0: e.clientY + scroller.scrollTop,
            sx: e.clientX, sy: e.clientY, x: e.clientX, y: e.clientY,
        };
        drag.raf = requestAnimationFrame(tick);
    };

    const move = e => {
        if (!drag || e.pointerId !== drag.pointerId) return;
        drag.x = e.clientX;
        drag.y = e.clientY;
        if (!drag.box) {
            if (Math.abs(e.clientX - drag.sx) < THRESHOLD && Math.abs(e.clientY - drag.sy) < THRESHOLD) return;
            drag.box = document.createElement("div");
            drag.box.className = "doc-marquee";
            drag.box.dataset.testid = "doc-marquee";
            document.body.appendChild(drag.box);
            document.body.classList.add("doc-marquee-active");
            for (const g of drag.grids) g.classList.toggle("marquee-replace", !drag.additive);
        }
        e.preventDefault();
        update();
    };

    const end = e => {
        if (!drag || e.pointerId !== drag.pointerId) return;
        const d = drag;
        drag = null;
        cancelAnimationFrame(d.raf);
        if (!d.box) {
            if (e.type === "pointerup" && !d.additive) dotnet.invokeMethodAsync("SetSelectionFromMarquee", []).catch(() => { });
            return;
        }
        const lis = cardsOf(d.grids);
        const ids = new Set(d.additive ? lis.filter(li => li.classList.contains("selected")).map(idOf) : []);
        for (const li of lis) if (li.classList.contains("marquee-hit")) ids.add(idOf(li));
        d.box.remove();
        document.body.classList.remove("doc-marquee-active");
        for (const g of d.grids) g.classList.remove("marquee-replace");
        for (const li of lis) li.classList.remove("marquee-hit");
        dotnet.invokeMethodAsync("SetSelectionFromMarquee", [...ids].filter(Boolean).map(Number)).catch(() => { });
    };

    document.addEventListener("pointerdown", down);
    document.addEventListener("pointermove", move);
    document.addEventListener("pointerup", end);
    document.addEventListener("pointercancel", end);
    marquee = { down, move, end };
}

export function unbindMarquee() {
    if (!marquee) return;
    document.removeEventListener("pointerdown", marquee.down);
    document.removeEventListener("pointermove", marquee.move);
    document.removeEventListener("pointerup", marquee.end);
    document.removeEventListener("pointercancel", marquee.end);
    marquee = null;
}
