// Chart rendering functions
window.chartInstances = window.chartInstances || {};

// Destroy a chart created by any of the render* helpers and drop it from the registry.
// Components call this from Dispose so navigating away doesn't leak the Chart.js instance
// (and its canvas/event listeners) in window.chartInstances until the same element id is
// reused. Safe to call for an unknown / already-destroyed id.
window.destroyChart = function (elementId) {
    const inst = window.chartInstances[elementId];
    if (inst) {
        try { inst.destroy(); } catch { /* canvas already torn down */ }
        delete window.chartInstances[elementId];
    }
};

// Chart.js callbacks (tick/label/title formatters, tooltip filters) used to be shipped from C#
// as JS function-body strings and eval'd here. Instead, C# now passes a small descriptor
// { kind, ...params } and we look the builder up by name. No eval, and a stray/unknown kind
// degrades to "no custom formatter" rather than executing arbitrary text. Add a new kind here
// and reference it by name from C# rather than passing code.
// German money format used app-wide ("−1.234,56 €": real minus sign, no-break space before the symbol), matching
// Formatting.FormatMoneyDe on the server, independent of the browser language.
window.obMoney = function (v, decimals) {
    const d = typeof decimals === 'number' ? decimals : 2;
    const n = Number(v);
    const abs = Math.abs(n).toLocaleString('de-DE', { minimumFractionDigits: d, maximumFractionDigits: d });
    return (n < 0 ? '\u2212' : '') + abs + '\u00A0€';
};

const chartCallbackFactories = {
    // y-axis tick / value formatter: German money with p.decimals places (default 0).
    euroTicks: function (p) {
        const d = (p && typeof p.decimals === 'number') ? p.decimals : 0;
        return function (value) { return window.obMoney(value, d); };
    },
    // tooltip label: "<dataset label>: <money>".
    datasetEuroLabel: function () {
        return function (context) { return context.dataset.label + ': ' + window.obMoney(context.parsed.y); };
    },
    // balance-chart tooltip title: reconstruct the calendar date from the day-of-year index
    // (labels[] only carries month names on day 1). p.year is the chart's year.
    dayOfYearDate: function (p) {
        const year = p.year;
        return function (items) {
            if (!items.length) return '';
            const i = items[0].dataIndex;
            const dt = new Date(year, 0, 1 + i);
            return dt.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
        };
    },
    // balance-chart tooltip label: "<p.label>: <money>".
    localizedEuroLabel: function (p) {
        const label = p.label;
        return function (context) {
            return label + ': ' + window.obMoney(context.parsed.y);
        };
    },
    // budget stacked-bar tooltip label: actual value, inlining the matching budget value.
    budgetActualLabel: function () {
        return function (context) {
            const ds = context.dataset;
            const key = ds.highlightGroup || ds.label;
            const isBudget = ds.stack === 'budget';
            if (isBudget) { return key + ': — / ' + window.obMoney(context.parsed.y); }
            const budgetDs = context.chart.data.datasets.find(d => d.stack === 'budget' && d.highlightGroup === key);
            const budgetVal = budgetDs ? budgetDs.data[context.dataIndex] : null;
            const actual = window.obMoney(context.parsed.y);
            return key + ': ' + actual + (budgetVal != null ? ' / ' + window.obMoney(budgetVal) : '');
        };
    },
    // budget stacked-bar tooltip filter: drop a standalone budget row when a matching actual row
    // is already in the tooltip (that actual row inlines the budget value).
    budgetActualFilter: function () {
        return function (item, idx, array) {
            if (item.dataset.stack !== 'budget') return true;
            const key = item.dataset.highlightGroup;
            return !array.some(o => o.dataset.stack !== 'budget' && o.dataset.highlightGroup === key);
        };
    }
};

// Resolve a { kind, ...params } descriptor (or undefined) to the actual Chart.js callback.
function resolveChartCallback(spec) {
    if (!spec || typeof spec !== 'object' || !spec.kind) return undefined;
    const factory = chartCallbackFactories[spec.kind];
    if (!factory) { console.warn('Unknown chart callback kind:', spec.kind); return undefined; }
    return factory(spec);
}

// Replace the descriptor objects on a chart config's tooltip/tick callbacks with real functions.
function resolveChartCallbacks(chartData) {
    const cbs = chartData.options?.plugins?.tooltip?.callbacks;
    if (cbs) {
        if (cbs.label) cbs.label = resolveChartCallback(cbs.label);
        if (cbs.title) cbs.title = resolveChartCallback(cbs.title);
    }
    const tt = chartData.options?.plugins?.tooltip;
    if (tt && tt.filter) tt.filter = resolveChartCallback(tt.filter);
    const yticks = chartData.options?.scales?.y?.ticks;
    if (yticks && yticks.callback) yticks.callback = resolveChartCallback(yticks.callback);
}

// Open the native picker on a date/month input that's visually hidden — used to make
// the dashboard heading double as the month-picker trigger. Falls back to focus+click
// for browsers that don't expose showPicker() yet (pre-Safari 16.4).
window.showPickerOn = function (el) {
    if (!el) return;
    if (typeof el.showPicker === 'function') {
        try { el.showPicker(); return; } catch { /* fall through */ }
    }
    el.focus();
    el.click();
};

// Tooltip hover-highlight: with mode:'index' the tooltip lists every dataset stacked at that
// x-index, but Chart.js doesn't mark which segment the cursor is actually intersecting. We
// track the intersected segment in chart._activeHover and dim the other tooltip rows so the
// hovered one stands out. Force a redraw when the hovered segment changes, since active
// index doesn't change as the cursor moves vertically inside a stack.
function attachTooltipHoverHighlight(chartConfig) {
    chartConfig.options = chartConfig.options || {};
    const userOnHover = chartConfig.options.onHover;
    chartConfig.options.onHover = function (event, active, chart) {
        const els = chart.getElementsAtEventForMode(event, 'point', { intersect: true }, false);
        const next = els.length ? { d: els[0].datasetIndex, i: els[0].index } : null;
        const prev = chart._activeHover;
        const changed = (prev?.d ?? -1) !== (next?.d ?? -1) || (prev?.i ?? -1) !== (next?.i ?? -1);
        if (changed) {
            chart._activeHover = next;
            // Chart.js caches resolved labelColors/labelTextColors on the tooltip during
            // update(). When the cursor moves between segments in the same x-index column,
            // active items don't change, so the tooltip never re-runs the callbacks and the
            // cache stays stale. Recompute the cached arrays directly, then repaint.
            const tt = chart.tooltip;
            const items = tt && tt.dataPoints;
            if (items && items.length) {
                const cb = tt.options.callbacks;
                tt.labelColors = items.map(item => cb.labelColor.call(tt, item));
                tt.labelTextColors = items.map(item => cb.labelTextColor.call(tt, item));
            }
            chart.draw();
        }
        if (typeof userOnHover === 'function') userOnHover.call(this, event, active, chart);
    };

    const tt = chartConfig.options.plugins?.tooltip;
    if (!tt) return;
    tt.callbacks = tt.callbacks || {};
    const userLabelTextColor = tt.callbacks.labelTextColor;
    const userLabelColor = tt.callbacks.labelColor;
    // Match on dataset.highlightGroup when set, otherwise fall back to datasetIndex. This
    // lets the SpendingAnalysis chart pair its "actual" and "budget" datasets so hovering
    // either one keeps the merged tooltip row bright.
    function hoverKey(ds, datasetIndex) {
        return ds && ds.highlightGroup != null ? ds.highlightGroup : datasetIndex;
    }
    function isDimmed(context) {
        const hovered = context.chart._activeHover;
        if (!hovered) return false;
        const hoveredDs = context.chart.data.datasets[hovered.d];
        return hoverKey(context.dataset, context.datasetIndex) !== hoverKey(hoveredDs, hovered.d);
    }
    tt.callbacks.labelTextColor = function (context) {
        if (isDimmed(context)) {
            return 'rgba(255, 255, 255, 0.45)';
        }
        if (typeof userLabelTextColor === 'function') {
            return userLabelTextColor.call(this, context);
        }
        return '#fff';
    };
    tt.callbacks.labelColor = function (context) {
        const ds = context.dataset || {};
        const bg = Array.isArray(ds.backgroundColor)
            ? ds.backgroundColor[context.dataIndex] ?? ds.backgroundColor[0]
            : ds.backgroundColor;
        const bd = ds.borderColor || bg;
        const dim = isDimmed(context);
        if (!dim && typeof userLabelColor === 'function') {
            return userLabelColor.call(this, context);
        }
        return {
            backgroundColor: dim ? fadeColor(bg, 0.3) : bg,
            borderColor: dim ? fadeColor(bd, 0.3) : bd,
            borderWidth: 1,
        };
    };
}

// Multiply a CSS color's alpha by `factor`. Handles "rgba(r,g,b,a)" and "rgb(r,g,b)".
// Falls back to the original string if it doesn't recognize the format — the swatch just
// won't dim, which is acceptable.
function fadeColor(color, factor) {
    if (typeof color !== 'string') return color;
    const rgba = color.match(/^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i);
    if (rgba) {
        const a = (rgba[4] !== undefined ? parseFloat(rgba[4]) : 1) * factor;
        return `rgba(${rgba[1]}, ${rgba[2]}, ${rgba[3]}, ${a})`;
    }
    return color;
}

// Custom tooltip positioner: returns the outer edge of the hovered bar group as the caret
// X (right edge if cursor in left half of chart, left edge if cursor in right half) plus
// cursor Y. The external HTML tooltip handler reads caretX/Y from the model and decides
// final placement in viewport coords (escaping canvas bounds so a tall tooltip doesn't
// have to overlap the bars).
if (window.Chart && Chart.Tooltip && Chart.Tooltip.positioners) {
    Chart.Tooltip.positioners.outerEdge = function (elements, eventPosition) {
        if (!elements || !elements.length) return false;
        let minLeft = Infinity;
        let maxRight = -Infinity;
        for (const el of elements) {
            const bar = el.element;
            if (!bar || typeof bar.x !== 'number') continue;
            const half = (bar.width || 0) / 2;
            if (bar.x - half < minLeft) minLeft = bar.x - half;
            if (bar.x + half > maxRight) maxRight = bar.x + half;
        }
        if (minLeft === Infinity) return false;

        const area = this.chart.chartArea;
        const center = (area.left + area.right) / 2;
        const cursorX = eventPosition?.x ?? (minLeft + maxRight) / 2;
        const anchorX = cursorX < center ? maxRight : minLeft;
        const cursorY = eventPosition?.y ?? (area.top + area.bottom) / 2;

        return { x: anchorX, y: cursorY };
    };
}

// External HTML tooltip handler: renders the Chart.js tooltip as an absolutely-positioned
// <div> attached to <body> so it can escape the canvas bounds. Chart.js's built-in tooltip
// is drawn on the canvas and gets clipped/centered-on-cursor when it's tall, ending up
// overlapping the bars. The external version sits in viewport space, opens horizontally
// from the bar-group anchor based on cursor side, and caps height with a scroll fallback.
function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function externalTooltipHandler(context) {
    const { chart, tooltip } = context;
    const elId = '__chartTooltip_' + chart.canvas.id;
    let el = document.getElementById(elId);
    if (!el) {
        el = document.createElement('div');
        el.id = elId;
        Object.assign(el.style, {
            position: 'fixed',
            background: 'rgba(0, 0, 0, 0.85)',
            color: 'white',
            borderRadius: '4px',
            padding: '8px 10px',
            font: '600 11px system-ui, -apple-system, sans-serif',
            lineHeight: '1.45',
            pointerEvents: 'none',
            zIndex: '10000',
            opacity: '0',
            transition: 'opacity 0.08s linear',
            maxHeight: '70vh',
            maxWidth: '340px',
            overflowY: 'auto',
            overflowX: 'hidden',
            boxShadow: '0 2px 10px rgba(0,0,0,0.35)',
            left: '0px',
            top: '0px',
        });
        document.body.appendChild(el);
    }

    if (tooltip.opacity === 0) {
        el.style.opacity = '0';
        return;
    }

    // Build content from the tooltip model (Chart.js has already run filter+callbacks).
    const titleLines = tooltip.title || [];
    const bodyEntries = tooltip.body || [];
    let html = '';
    if (titleLines.length) {
        html += '<div style="font-weight:700;font-size:12px;margin-bottom:6px;opacity:0.9;">'
              + titleLines.map(escapeHtml).join('<br>')
              + '</div>';
    }
    bodyEntries.forEach((entry, i) => {
        const colors = (tooltip.labelColors && tooltip.labelColors[i]) || {};
        const txtColor = (tooltip.labelTextColors && tooltip.labelTextColors[i]) || '#fff';
        const bg = colors.backgroundColor || '#888';
        const bd = colors.borderColor || bg;
        const swatch = '<span style="display:inline-block;width:10px;height:10px;margin-right:6px;'
                     + 'flex-shrink:0;vertical-align:middle;border-radius:2px;'
                     + 'background:' + bg + ';border:1px solid ' + bd + ';"></span>';
        (entry.lines || []).forEach(line => {
            html += '<div style="color:' + txtColor + ';display:flex;align-items:center;'
                  + 'white-space:nowrap;margin-bottom:2px;">'
                  + swatch + '<span>' + escapeHtml(line) + '</span></div>';
        });
    });
    el.innerHTML = html;
    el.style.opacity = '1';

    // Position: viewport coords. Anchor X is the bar group's outer edge (the outerEdge
    // positioner picks the side AWAY from the cursor — right edge if cursor in left half,
    // left edge if cursor in right half). Tooltip extends in that same direction so it
    // ends up in the empty space beside the bars, never on top of them. Y is centered on
    // the chart area's midpoint (stable — doesn't drift with cursor) and clamped to the
    // viewport; if taller than viewport, internal scroll takes over.
    const canvasRect = chart.canvas.getBoundingClientRect();
    const anchorX = canvasRect.left + tooltip.caretX;
    const chartArea = chart.chartArea;
    const chartMidY = canvasRect.top + (chartArea.top + chartArea.bottom) / 2;
    const margin = 8;

    const ttWidth = el.offsetWidth;
    const ttHeight = el.offsetHeight;
    const vpWidth = window.innerWidth;
    const vpHeight = window.innerHeight;

    // Cursor side detection: use _eventPosition (Chart.js attaches the original mouse event
    // coords during tooltip update) with a fallback to caretX (close enough — the anchor is
    // a few px offset from the cursor inside the bar group).
    const cursorXInCanvas = tooltip._eventPosition?.x ?? tooltip.caretX;
    const chartCenter = (chartArea.left + chartArea.right) / 2;
    const extendRight = cursorXInCanvas < chartCenter;
    let leftPx = extendRight ? anchorX + 6 : anchorX - ttWidth - 6;

    let topPx = chartMidY - ttHeight / 2;
    if (topPx + ttHeight > vpHeight - margin) topPx = vpHeight - margin - ttHeight;
    if (topPx < margin) topPx = margin;
    if (leftPx + ttWidth > vpWidth - margin) leftPx = vpWidth - margin - ttWidth;
    if (leftPx < margin) leftPx = margin;

    el.style.left = leftPx + 'px';
    el.style.top = topPx + 'px';
}

// Set the external handler on a chart's tooltip config (overrides the built-in canvas
// tooltip with the HTML version). Called from renderChart + renderStackedBarChart.
function enableExternalTooltip(chartConfig) {
    chartConfig.options = chartConfig.options || {};
    chartConfig.options.plugins = chartConfig.options.plugins || {};
    const tt = chartConfig.options.plugins.tooltip = chartConfig.options.plugins.tooltip || {};
    tt.enabled = false;
    tt.external = externalTooltipHandler;
}

window.renderPieChart = function(elementId, chartData) {
    const ctx = document.getElementById(elementId);
    if (!ctx) {
        console.error('Canvas element not found:', elementId);
        return false;
    }

    // Destroy existing chart if it exists
    if (window.chartInstances[elementId]) {
        window.chartInstances[elementId].destroy();
    }

    try {
        window.chartInstances[elementId] = new Chart(ctx, chartData);
        return true;
    } catch (error) {
        console.error('Error creating chart:', error);
        return false;
    }
};

window.renderLineChart = function(elementId, chartData) {
    const ctx = document.getElementById(elementId);
    if (!ctx) {
        console.error('Canvas element not found:', elementId);
        return false;
    }

    // Destroy existing chart if it exists
    if (window.chartInstances[elementId]) {
        window.chartInstances[elementId].destroy();
    }

    try {
        window.chartInstances[elementId] = new Chart(ctx, chartData);
        return true;
    } catch (error) {
        console.error('Error creating chart:', error);
        return false;
    }
};

// Euro formatter for the above-bar labels. In 'abbrev' mode, values ≥ 1000 collapse to
// "1,2k €" / "12k €" so labels stay readable on narrow (mobile) charts; otherwise rounds to
// a whole-euro figure.
function fmtEuro(v, mode) {
    const abs = Math.abs(v);
    const sign = v < 0 ? '−' : '';
    if (mode === 'abbrev') {
        if (abs >= 10000) return sign + Math.round(abs / 1000) + 'k\u00A0€';
        if (abs >= 1000) return sign + (abs / 1000).toFixed(1).replace('.', ',') + 'k\u00A0€';
    }
    return window.obMoney(v, 0);
}

// Pick a label rendering strategy given the available horizontal width per label.
// Degrades full → totalOnly (drop caption) → abbrev (€1.2k) → hidden as labels start to
// overflow their available slot.
function pickLabelMode(ctx, maxAbsValue, captions, available) {
    if (available <= 14) return 'hidden';
    ctx.font = '600 11px system-ui, sans-serif';
    const fullW = ctx.measureText(fmtEuro(maxAbsValue, 'full')).width;
    if (fullW <= available) {
        if (captions.length === 0) return 'full';
        ctx.font = '600 10px system-ui, sans-serif';
        const widestCap = Math.max(...captions.map(c => ctx.measureText(c).width));
        return widestCap <= available ? 'full' : 'totalOnly';
    }
    const abbrW = ctx.measureText(fmtEuro(maxAbsValue, 'abbrev')).width;
    return abbrW <= available ? 'abbrev' : 'hidden';
}

// Minimum center-to-center distance between adjacent label anchor points (bar centers)
// across the given stacks — including cross-stack pairs within the same month, which are
// typically the tightest gap on a side-by-side stacked-bar chart.
function minBarCenterSpacing(chart, stackKeys) {
    const metas = stackKeys
        .map(key => {
            const idx = chart.data.datasets.findIndex(ds => ds.stack === key);
            return idx >= 0 ? chart.getDatasetMeta(idx) : null;
        })
        .filter(Boolean);
    if (metas.length === 0) return Infinity;
    const labelCount = chart.data.labels.length;
    let min = Infinity;
    for (const meta of metas) {
        for (let i = 1; i < labelCount; i++) {
            const a = meta.data[i - 1], b = meta.data[i];
            if (a && b && typeof a.x === 'number' && typeof b.x === 'number') {
                min = Math.min(min, Math.abs(b.x - a.x));
            }
        }
    }
    for (let i = 0; i < labelCount; i++) {
        for (let p = 0; p < metas.length; p++) {
            for (let q = p + 1; q < metas.length; q++) {
                const a = metas[p].data[i], b = metas[q].data[i];
                if (a && b && typeof a.x === 'number' && typeof b.x === 'number') {
                    min = Math.min(min, Math.abs(b.x - a.x));
                }
            }
        }
    }
    return min;
}

// Inline plugin: draws the monthly total above each stack. When both "actual" and "budget"
// stacks are present, the labels are prefixed with "Actual" / "Budget" so the user can tell
// the two bar groups apart at a glance instead of relying on the legend's opacity cue.
const stackTotalLabelsPlugin = {
    id: 'stackTotalLabels',
    afterDatasetsDraw(chart) {
        const { ctx, data } = chart;
        if (!data || !data.labels || !data.datasets) return;

        const actualIdx = data.datasets.findIndex(ds => ds.stack === 'actual');
        const budgetIdx = data.datasets.findIndex(ds => ds.stack === 'budget');
        if (actualIdx < 0 && budgetIdx < 0) return;

        const actualMeta = actualIdx >= 0 ? chart.getDatasetMeta(actualIdx) : null;
        const budgetMeta = budgetIdx >= 0 ? chart.getDatasetMeta(budgetIdx) : null;
        const both = actualIdx >= 0 && budgetIdx >= 0;

        // Pre-aggregate per-month sums so the mode picker sees the widest label we'd draw.
        let maxAbs = 0;
        const sums = [];
        for (let i = 0; i < data.labels.length; i++) {
            let actualSum = 0, budgetSum = 0;
            for (const ds of data.datasets) {
                const v = Number(ds.data[i]);
                if (isNaN(v)) continue;
                if (ds.stack === 'actual') actualSum += v;
                else if (ds.stack === 'budget') budgetSum += v;
            }
            sums[i] = { actualSum, budgetSum };
            maxAbs = Math.max(maxAbs, Math.abs(actualSum), Math.abs(budgetSum));
            if (both) maxAbs = Math.max(maxAbs, Math.abs(budgetSum - actualSum));
        }

        const captions = [];
        if (budgetIdx >= 0) captions.push('Budget');
        if (actualIdx >= 0 && both) captions.push('Actual');
        const spacing = minBarCenterSpacing(chart, ['actual', 'budget']);
        const mode = pickLabelMode(ctx, maxAbs, captions, spacing * 0.95);
        if (mode === 'hidden') return;

        ctx.save();
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';

        for (let i = 0; i < data.labels.length; i++) {
            const { actualSum, budgetSum } = sums[i];

            // Budget stack label
            if (budgetMeta && budgetSum > 0) {
                const bar = budgetMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(budgetSum);
                    if (mode === 'full') {
                        ctx.font = '600 10px system-ui, sans-serif';
                        ctx.fillStyle = '#777';
                        ctx.fillText('Budget', bar.x, yTop - 14);
                    }
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText(fmtEuro(budgetSum, mode), bar.x, yTop - 4);
                }
            }

            // Actual stack label
            if (actualMeta && actualSum > 0) {
                const bar = actualMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(actualSum);
                    if (both && mode === 'full') {
                        ctx.font = '600 10px system-ui, sans-serif';
                        ctx.fillStyle = '#777';
                        ctx.fillText('Actual', bar.x, yTop - 14);
                    }
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText(fmtEuro(actualSum, mode), bar.x, yTop - 4);
                }
            }

            // Net (budget − actual): positive = under budget (green), negative = over (red).
            // Mirrors the Cashflow chart so users can read over/under at a glance. Skip when
            // only one stack is present, or when there's no actual yet — comparing budget
            // to a still-empty month would read as a huge under-spend every time.
            if (both && actualSum > 0) {
                const net = budgetSum - actualSum;
                const yTallerTop = chart.scales.y.getPixelForValue(Math.max(actualSum, budgetSum));
                const x = chart.scales.x.getPixelForValue(i);
                ctx.font = '600 11px system-ui, sans-serif';
                ctx.fillStyle = net >= 0 ? '#1c7a3a' : '#a02434';
                const formatted = fmtEuro(net, mode);
                ctx.fillText((net >= 0 ? '+' : '') + formatted, x, yTallerTop - 28);
            }
        }
        ctx.restore();
    }
};

// Inline plugin: when a stacked-bar segment is hovered and a sibling segment with the same
// highlightGroup exists in another stack (the SpendingAnalysis "actual" vs "budget" pair),
// draw two dashed lines connecting the top and bottom edges of the two segments so the
// reader can eyeball the over/under at a glance.
const hoverPairConnectorPlugin = {
    id: 'hoverPairConnector',
    afterDatasetsDraw(chart) {
        const hovered = chart._activeHover;
        if (!hovered) return;
        const datasets = chart.data.datasets;
        const hoveredDs = datasets[hovered.d];
        if (!hoveredDs || hoveredDs.highlightGroup == null) return;

        const pairedIdx = datasets.findIndex((d, i) =>
            i !== hovered.d && d.highlightGroup === hoveredDs.highlightGroup && d.stack !== hoveredDs.stack
        );
        if (pairedIdx < 0) return;

        const hBar = chart.getDatasetMeta(hovered.d).data[hovered.i];
        const pBar = chart.getDatasetMeta(pairedIdx).data[hovered.i];
        if (!hBar || !pBar) return;

        // y can be above or below base depending on sign; normalize to top/bottom.
        const hTop = Math.min(hBar.y, hBar.base);
        const hBot = Math.max(hBar.y, hBar.base);
        const pTop = Math.min(pBar.y, pBar.base);
        const pBot = Math.max(pBar.y, pBar.base);

        // If either side has no value for this month, both edges collapse onto the baseline.
        // Drawing the connector then makes the lines plunge to 0, which misreads as "budget
        // dropped to 0" — skip it. The tooltip's "— / €N" still conveys the missing side.
        if (hBot - hTop < 1 || pBot - pTop < 1) return;

        // Whichever bar sits to the left provides the line's left endpoint (right edge of
        // that bar) and the other provides the right endpoint (left edge).
        const leftIsHovered = hBar.x < pBar.x;
        const leftBar = leftIsHovered ? hBar : pBar;
        const rightBar = leftIsHovered ? pBar : hBar;
        const leftTop = leftIsHovered ? hTop : pTop;
        const leftBot = leftIsHovered ? hBot : pBot;
        const rightTop = leftIsHovered ? pTop : hTop;
        const rightBot = leftIsHovered ? pBot : hBot;

        const xL = leftBar.x + (leftBar.width || 0) / 2;
        const xR = rightBar.x - (rightBar.width || 0) / 2;

        const ctx = chart.ctx;
        ctx.save();
        ctx.strokeStyle = 'rgba(60, 60, 60, 0.85)';
        ctx.lineWidth = 1.25;
        ctx.setLineDash([4, 3]);

        ctx.beginPath();
        ctx.moveTo(xL, leftTop);
        ctx.lineTo(xR, rightTop);
        ctx.stroke();

        ctx.beginPath();
        ctx.moveTo(xL, leftBot);
        ctx.lineTo(xR, rightBot);
        ctx.stroke();

        ctx.restore();
    }
};

window.renderStackedBarChart = function(elementId, chartData) {
    const ctx = document.getElementById(elementId);
    if (!ctx) {
        console.error('Canvas element not found:', elementId);
        return false;
    }

    // Destroy existing chart if it exists
    if (window.chartInstances[elementId]) {
        window.chartInstances[elementId].destroy();
    }

    // Turn the { kind, ...params } callback descriptors from C# into real functions.
    resolveChartCallbacks(chartData);

    attachTooltipHoverHighlight(chartData);
    enableExternalTooltip(chartData);

    try {
        window.chartInstances[elementId] = new Chart(ctx, {
            ...chartData,
            plugins: [stackTotalLabelsPlugin, hoverPairConnectorPlugin]
        });
        return true;
    } catch (error) {
        console.error('Error creating chart:', error);
        return false;
    }
};

// Cashflow plugin: paints a tinted band across forecast months so the user can see at a
// glance which bars are projected vs actual. The Razor side passes
// options.plugins.forecastShading.startMonth — 1-based, with 13 meaning "no forecast".
// For non-month charts (e.g. the daily balance chart), pass startIndex instead — same 1-based
// convention but interpreted against the x-axis labels' length, not 12.
const forecastShadingPlugin = {
    id: 'forecastShading',
    beforeDatasetsDraw(chart, _args, options) {
        const raw = options?.startIndex ?? options?.startMonth;
        if (!raw) return;

        const xScale = chart.scales?.x;
        const chartArea = chart.chartArea;
        if (!xScale || !chartArea) return;

        const labelCount = chart.data?.labels?.length ?? 12;
        const startIdx = raw - 1;
        if (startIdx < 0 || startIdx >= labelCount) return;

        // Left edge: the boundary halfway between (startIdx-1) and startIdx — or the chart's
        // left edge if startIdx is 0 (i.e. the whole year is forecast).
        let leftPx;
        if (startIdx === 0) {
            leftPx = chartArea.left;
        } else {
            const prevX = xScale.getPixelForValue(startIdx - 1);
            const thisX = xScale.getPixelForValue(startIdx);
            leftPx = (prevX + thisX) / 2;
        }
        const rightPx = chartArea.right;

        chart.ctx.save();
        chart.ctx.fillStyle = 'rgba(100, 100, 100, 0.08)';
        chart.ctx.fillRect(leftPx, chartArea.top, rightPx - leftPx, chartArea.bottom - chartArea.top);
        chart.ctx.restore();
    }
};

// Cashflow plugin: draws stack identity labels ("Income" / "Expenses") and totals above each
// stack, plus the net (income − expense) above the taller of the two. Without the stack
// captions the side-by-side income vs expense pair could be confused at a glance.
const monthlyNetLabelsPlugin = {
    id: 'monthlyNetLabels',
    afterDatasetsDraw(chart) {
        const { ctx, data } = chart;
        if (!data || !data.labels || !data.datasets) return;

        const incomeIdx = data.datasets.findIndex(ds => ds.stack === 'income');
        const expenseIdx = data.datasets.findIndex(ds => ds.stack === 'expenses');
        if (incomeIdx < 0 && expenseIdx < 0) return;

        const incomeMeta = incomeIdx >= 0 ? chart.getDatasetMeta(incomeIdx) : null;
        const expenseMeta = expenseIdx >= 0 ? chart.getDatasetMeta(expenseIdx) : null;

        // Pre-aggregate per-month sums so the mode picker sees the widest label we'd draw.
        let maxAbs = 0;
        const sums = [];
        for (let i = 0; i < data.labels.length; i++) {
            let income = 0, expense = 0;
            for (const ds of data.datasets) {
                const v = Number(ds.data[i]);
                if (isNaN(v)) continue;
                if (ds.stack === 'income') income += v;
                else if (ds.stack === 'expenses') expense += v;
            }
            sums[i] = { income, expense };
            maxAbs = Math.max(maxAbs, Math.abs(income), Math.abs(expense), Math.abs(income - expense));
        }

        const captions = [];
        if (incomeIdx >= 0) captions.push('Income');
        if (expenseIdx >= 0) captions.push('Expenses');
        const spacing = minBarCenterSpacing(chart, ['income', 'expenses']);
        const mode = pickLabelMode(ctx, maxAbs, captions, spacing * 0.95);
        if (mode === 'hidden') return;

        ctx.save();
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';

        for (let i = 0; i < data.labels.length; i++) {
            const { income, expense } = sums[i];
            if (income === 0 && expense === 0) continue;

            // Income stack: small "Income" caption + total
            if (incomeMeta && income > 0) {
                const bar = incomeMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(income);
                    if (mode === 'full') {
                        ctx.font = '600 10px system-ui, sans-serif';
                        ctx.fillStyle = '#777';
                        ctx.fillText('Income', bar.x, yTop - 14);
                    }
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText(fmtEuro(income, mode), bar.x, yTop - 4);
                }
            }

            // Expense stack: small "Expenses" caption + total
            if (expenseMeta && expense > 0) {
                const bar = expenseMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(expense);
                    if (mode === 'full') {
                        ctx.font = '600 10px system-ui, sans-serif';
                        ctx.fillStyle = '#777';
                        ctx.fillText('Expenses', bar.x, yTop - 14);
                    }
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText(fmtEuro(expense, mode), bar.x, yTop - 4);
                }
            }

            // Net sits above the taller stack with enough offset to clear its two-line label.
            const net = income - expense;
            const yTallerTop = chart.scales.y.getPixelForValue(Math.max(income, expense));
            const x = chart.scales.x.getPixelForValue(i);
            ctx.font = '600 11px system-ui, sans-serif';
            ctx.fillStyle = net >= 0 ? '#1c7a3a' : '#a02434';
            const formatted = fmtEuro(net, mode);
            ctx.fillText((net >= 0 ? '+' : '') + formatted, x, yTallerTop - 28);
        }
        ctx.restore();
    }
};

// Draws a dashed red horizontal line at y=0 across the plot area when 0 is in the y range.
// Opt-in via options.plugins.zeroLine.enabled — other charts (the stacked-bar cashflow chart,
// the analysis chart) bypass it because they don't set the option.
const zeroLinePlugin = {
    id: 'zeroLine',
    afterDatasetsDraw(chart, _args, options) {
        if (!options?.enabled) return;
        const yScale = chart.scales?.y;
        const chartArea = chart.chartArea;
        if (!yScale || !chartArea) return;
        if (yScale.min > 0 || yScale.max < 0) return;

        const y = yScale.getPixelForValue(0);
        const ctx = chart.ctx;
        ctx.save();
        ctx.strokeStyle = options.color || 'rgba(220, 53, 69, 0.7)';
        ctx.lineWidth = 1.5;
        ctx.setLineDash([5, 4]);
        ctx.beginPath();
        ctx.moveTo(chartArea.left, y);
        ctx.lineTo(chartArea.right, y);
        ctx.stroke();
        ctx.restore();
    }
};

// Transactions chart: one thin bar per bucket (end-of-day / end-of-month balance for accounts,
// the bucket's sum for categories) from the zero line, month labels along the top with thin
// separators, a sparse money scale at the right edge, and on hover/touch a crosshair snapped to
// the bar with a date + value label pinned to the bottom edge. Each bar gets a fixed width
// (a few px per day, more per month), so a long history is wider than the frame and scrolls
// horizontally inside #browseChartScroll; the money scale is drawn on #browseChartAxis beside it
// and stays in place. The canvas only covers the visible window: .browse-chart-canvas spans the
// full history to give the scroll width, and the canvas inside it sits at the scrolled-to bar and
// holds just the bars in view (Chart.js data is that slice), redrawn once per frame while
// scrolling. browseChartData returns the full series. The chart opens scrolled to the selected period's bars (accent colour),
// which for the current period is the right end. Colours come from the --chart-* tokens on
// .browse-chart-frame so light and dark theme each get their own steps.
// A click on a bar asks Blazor (OnChartBarClicked) to move the list to that bar's month.
// The browse chart's y range: from min(0, lowest value) to max(0, highest value), budget line
// included, with a little headroom, so a side without data takes no space and 0 stays the
// baseline. Ticks step by a nice amount (1/2/2.5/5 x 10^k) counted from 0, about three of them.
window.browseChartAxis = function(values) {
    let lo = 0, hi = 0;
    for (const v of values) {
        if (v === null || v === undefined || !isFinite(v)) continue;
        if (v < lo) lo = v;
        if (v > hi) hi = v;
    }
    const span = hi - lo;
    if (span === 0) return { min: 0, max: 1, step: 1 };
    const raw = span / 2, mag = Math.pow(10, Math.floor(Math.log10(raw)));
    const step = [1, 2, 2.5, 5, 10].map(f => f * mag).find(s => s >= raw);
    const head = span * 0.06;
    return { min: lo < 0 ? lo - head : 0, max: hi > 0 ? hi + head : 0, step };
};

// The browse chart's full series (labels, values, bar colours, budget) and the index of the first
// bar in its drawn window.
window.browseChartData = function(canvasId) {
    const f = Chart.getChart(canvasId)?.$obFull;
    return f ? { labels: f.labels, values: f.values, colors: f.colors, budget: f.budget, first: f.first() } : null;
};

window.renderBrowseChart = function(canvasId, d, dotnetRef) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return false;
    if (window.chartInstances[canvasId]) window.chartInstances[canvasId].destroy();
    const css = getComputedStyle(canvas.closest('.browse-chart-frame') || document.documentElement);
    const tok = (name, fallback) => (css.getPropertyValue(name) || '').trim() || fallback;
    const c = {
        bar: tok('--chart-bar', '#7b8494'), neg: tok('--chart-bar-neg', '#c96161'),
        accent: tok('--chart-bar-accent', '#3b82c4'), budget: tok('--chart-budget', '#c2417f'),
        grid: tok('--chart-grid', 'rgba(0,0,0,0.08)'), text: tok('--chart-text', '#6b7280'),
        labelBg: tok('--chart-label-bg', '#1f2937'), labelFg: tok('--chart-label-fg', '#fff')
    };
    const font = '11px ' + (getComputedStyle(document.body).fontFamily || 'sans-serif');
    const g = d.granularity;
    const n = d.values.length;
    const starts = d.starts.map(s => { const [y, m, day] = s.split('-').map(Number); return { y, m, day }; });
    // Future buckets of a balance aren't known yet and stay empty.
    const isFuture = i => d.balance && d.starts[i] > d.today;
    const values = d.values.map((v, i) => isFuture(i) ? null : v);
    // The selected period's bars carry the accent, the rest stay neutral.
    const colors = values.map((v, i) => {
        if (d.anchors[i]) return c.accent;
        // On an expense chart (reversed axis) spending is the normal case; only a balance or
        // income below zero reads as negative.
        return v !== null && v < 0 && !d.invertY ? c.neg : c.bar;
    });
    const pad2 = x => String(x).padStart(2, '0');
    const quarter = s => 'Q' + (Math.floor((s.m - 1) / 3) + 1);
    const titleOf = i => {
        const s = starts[i];
        if (g === 'D') return pad2(s.day) + '.' + pad2(s.m) + '.' + s.y;
        if (g === 'Q') return quarter(s) + ' ' + s.y;
        if (g === 'Y') return String(s.y);
        return d.monthNames[s.m - 1] + ' ' + s.y;
    };
    // Compact scale labels: "15k €", "1,5k €", "−300 €".
    const compact = v => {
        const a = Math.abs(v), sign = v < 0 ? '−' : '';
        if (a >= 1e6) return sign + (a / 1e6).toLocaleString('de-DE', { maximumFractionDigits: 1 }) + ' Mio €';
        if (a >= 1e4) return sign + Math.round(a / 1e3).toLocaleString('de-DE') + 'k €';
        if (a >= 1e3) return sign + (a / 1e3).toLocaleString('de-DE', { maximumFractionDigits: 1 }) + 'k €';
        return sign + Math.round(a).toLocaleString('de-DE') + ' €';
    };
    const hasBudget = !d.balance && Array.isArray(d.budget) && d.budget.length === n;

    // Month (or year) labels along the top with a thin separator where each one starts. Major
    // labels (first bar, January) are placed first, then the others wherever they fit.
    const topAxis = {
        id: 'obTopAxis',
        afterDraw(chart) {
            const x = chart.scales.x, area = chart.chartArea, ctx = chart.ctx;
            if (!x || !area) return;
            const half = n > 1 ? (x.getPixelForValue(1) - x.getPixelForValue(0)) / 2 : (area.right - area.left) / 2;
            const marks = [];
            const from = win.first, to = Math.min(n, win.first + win.count) - 1;
            for (let i = from; i <= to; i++) {
                const s = starts[i], first = i === from;
                let text = null, major = first || s.m === 1;
                if (g === 'D') {
                    if (first || s.day === 1) text = d.monthNames[s.m - 1] + (major ? ' ' + s.y : '');
                } else if (g === 'M') {
                    text = major ? d.monthNames[s.m - 1] + ' ' + s.y : d.monthNames[s.m - 1];
                } else if (g === 'Q') {
                    text = major ? quarter(s) + ' ' + s.y : quarter(s);
                } else {
                    major = true; text = String(s.y);
                }
                if (text) marks.push({ i, text, major, px: x.getPixelForValue(i - from) - half });
            }
            ctx.save();
            ctx.font = font;
            ctx.textBaseline = 'top';
            const placed = [];
            const fits = (l, r) => r <= area.right && placed.every(p => r + 8 <= p.l || l >= p.r + 8);
            for (const pass of [true, false]) {
                for (const m of marks) {
                    if (m.major !== pass) continue;
                    const l = Math.max(m.px, area.left) + 3, r = l + ctx.measureText(m.text).width;
                    if (fits(l, r)) placed.push({ l, r, m });
                }
            }
            ctx.strokeStyle = c.grid;
            ctx.lineWidth = 1;
            // Separators at every month start (daily) or year start (coarser).
            for (const m of marks) {
                if (m.i === from || (g !== 'D' && !m.major)) continue;
                const sx = Math.round(m.px) + 0.5;
                ctx.beginPath(); ctx.moveTo(sx, area.top - 16); ctx.lineTo(sx, area.bottom); ctx.stroke();
            }
            ctx.fillStyle = c.text;
            for (const p of placed) ctx.fillText(p.m.text, p.l, area.top - 15);
            ctx.restore();
        }
    };

    // Crosshair snapped to the hovered bar and a compact label pinned to the bottom edge.
    const hover = {
        id: 'obHover',
        afterEvent(chart, args) {
            const e = args.event, area = chart.chartArea;
            let idx = null;
            if (e.type !== 'mouseout' && e.x >= area.left && e.x <= area.right) {
                idx = Math.round(chart.scales.x.getValueForPixel(e.x)) + win.first;
                if (idx < 0 || idx >= n || values[idx] === null) idx = null;
            }
            if (idx !== chart.$obIdx) { chart.$obIdx = idx; args.changed = true; }
        },
        afterDraw(chart) {
            const i = chart.$obIdx;
            if (i === null || i === undefined) return;
            const ctx = chart.ctx, area = chart.chartArea;
            const x = Math.round(chart.scales.x.getPixelForValue(i - win.first)) + 0.5;
            ctx.save();
            ctx.strokeStyle = c.text; ctx.lineWidth = 1;
            ctx.beginPath(); ctx.moveTo(x, area.top); ctx.lineTo(x, area.bottom); ctx.stroke();
            let text = titleOf(i) + '   ' + window.obMoney(values[i]);
            if (hasBudget && d.budget[i] !== null) text += '   ' + d.budgetLabel + ' ' + window.obMoney(d.budget[i]);
            ctx.font = font;
            const w = ctx.measureText(text).width + 12, h = 18;
            // Keep the label inside the visible part of a scrolled chart.
            const off = scroller ? scroller.scrollLeft - win.left : 0;
            const visL = scroller ? Math.max(area.left, off) : area.left;
            const visR = scroller ? Math.min(area.right, off + scroller.clientWidth) : area.right;
            const lx = Math.max(visL, Math.min(x - w / 2, visR - w));
            const ly = chart.height - h;
            ctx.fillStyle = c.labelBg;
            ctx.beginPath();
            if (ctx.roundRect) ctx.roundRect(lx, ly, w, h, 4); else ctx.rect(lx, ly, w, h);
            ctx.fill();
            ctx.fillStyle = c.labelFg; ctx.textBaseline = 'middle';
            ctx.fillText(text, lx + 6, ly + h / 2 + 0.5);
            ctx.restore();
        }
    };

    // Fixed slot per bar; the history is at least as wide as the frame and otherwise scrolls.
    // When it scrolls, the canvas (in .browse-chart-window) covers the bars in view plus one, and
    // is moved and redrawn when the first visible bar changes.
    const scroller = canvas.closest('.browse-chart-scroll');
    const winEl = canvas.parentElement;
    const holder = scroller && winEl ? winEl.parentElement : null;
    const slot = { D: 5, M: 14, Q: 28, Y: 48 }[g] || 14;
    const win = { left: 0, first: 0, count: n, fullW: 0, scrolls: false };
    const layoutWindow = () => {
        const viewW = scroller ? scroller.clientWidth : canvas.clientWidth;
        win.fullW = Math.max(viewW, n * slot);
        win.scrolls = !!holder && n * slot > viewW;
        win.count = win.scrolls ? Math.min(n, Math.ceil(viewW / slot) + 1) : n;
        if (holder) holder.style.width = win.fullW + 'px';
        if (winEl && holder) winEl.style.width = (win.scrolls ? win.count * slot : win.fullW) + 'px';
    };
    // Picks the first bar in view and places the window there; true when it moved.
    const placeWindow = () => {
        const first = win.scrolls ? Math.max(0, Math.min(n - win.count, Math.floor(scroller.scrollLeft / slot))) : 0;
        if (first === win.first && win.placed) return false;
        win.first = first; win.placed = true;
        win.left = first * slot;
        if (winEl && holder) winEl.style.transform = 'translateX(' + win.left + 'px)';
        return true;
    };
    layoutWindow();
    const axisCanvas = document.getElementById(canvasId + 'Axis');

    // The y scale's labels, drawn on the fixed canvas beside the scroll area.
    const sideAxis = {
        id: 'obSideAxis',
        afterDraw(chart) {
            if (!axisCanvas) return;
            const y = chart.scales.y;
            const w = axisCanvas.clientWidth, h = chart.height, r = window.devicePixelRatio || 1;
            axisCanvas.width = Math.round(w * r); axisCanvas.height = Math.round(h * r);
            axisCanvas.style.height = h + 'px';
            const ctx = axisCanvas.getContext('2d');
            ctx.setTransform(r, 0, 0, r, 0, 0);
            ctx.clearRect(0, 0, w, h);
            ctx.font = font;
            ctx.fillStyle = c.text; ctx.textBaseline = 'middle';
            for (const t of y.ticks) ctx.fillText(compact(t.value), 4, y.getPixelForValue(t.value));
        }
    };

    const axis = window.browseChartAxis(values.concat(hasBudget ? d.budget : []));

    const datasets = [{
        label: d.balance ? 'Balance' : 'Amount', data: values.slice(win.first, win.first + win.count),
        backgroundColor: colors.slice(win.first, win.first + win.count), borderWidth: 0,
        borderRadius: g === 'D' || n > 60 ? 0 : 2, borderSkipped: 'start',
        categoryPercentage: 1, barPercentage: g === 'D' ? 0.8 : 0.7, maxBarThickness: 24, order: 1
    }];
    // The selected category's budget per bar: a dotted line in the bars' sign, stepping between
    // bars where the budget changes. Nulls (years without a budget) leave gaps.
    if (hasBudget) {
        datasets.push({
            type: 'line', label: d.budgetLabel || 'Budget', data: d.budget.slice(win.first, win.first + win.count),
            borderColor: c.budget, backgroundColor: c.budget, borderWidth: 2, borderDash: [3, 3],
            pointRadius: 0, fill: false, stepped: 'middle', spanGaps: false, order: 0
        });
    }
    if (scroller) scrollBrowseChartToPeriod(scroller, d.anchors, n, win.fullW);
    placeWindow();
    try {
        window.chartInstances[canvasId] = new Chart(canvas, {
            type: 'bar',
            data: { labels: d.labels.slice(win.first, win.first + win.count), datasets },
            plugins: [topAxis, hover, sideAxis],
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: d.noAnimate || n > 400 ? false : { duration: 250 },
                events: ['mousemove', 'mouseout', 'click', 'touchstart', 'touchmove'],
                layout: { padding: { top: 18, bottom: 22, left: 0, right: 0 } },
                plugins: { legend: { display: false }, tooltip: { enabled: false } },
                scales: {
                    x: { display: false, grid: { display: false } },
                    y: {
                        position: 'right',
                        // Expense values are negative; the reversed axis lets spending grow upward
                        // while the labels still read "−300 €".
                        reverse: !!d.invertY,
                        min: axis.min,
                        max: axis.max,
                        border: { display: false },
                        grid: { color: c.grid, drawTicks: false },
                        ticks: { display: !axisCanvas, color: c.text, font: { size: 10 }, stepSize: axis.step, includeBounds: false, padding: 4, callback: v => compact(v) }
                    }
                },
                onClick: function(_evt, _els, chart) {
                    const i = chart.$obIdx;
                    if (i === null || i === undefined || !dotnetRef) return;
                    dotnetRef.invokeMethodAsync('OnChartBarClicked', i);
                }
            }
        });
        const chart = window.chartInstances[canvasId];
        chart.$obFull = { labels: d.labels, values, colors, budget: hasBudget ? d.budget : null, first: () => win.first };
        // Moves the window to the scroll position and redraws it without animation.
        const syncWindow = () => {
            if (!placeWindow()) return;
            const a = win.first, b = a + win.count;
            chart.$obIdx = null;
            chart.data.labels = d.labels.slice(a, b);
            chart.data.datasets[0].data = values.slice(a, b);
            chart.data.datasets[0].backgroundColor = colors.slice(a, b);
            if (hasBudget) chart.data.datasets[1].data = d.budget.slice(a, b);
            chart.update('none');
        };
        let frame = 0;
        const onScroll = () => { if (!frame) frame = requestAnimationFrame(() => { frame = 0; syncWindow(); }); };
        const resizer = scroller && typeof ResizeObserver === 'function'
            ? new ResizeObserver(() => { const c = win.count, w = win.fullW; layoutWindow(); if (c !== win.count || w !== win.fullW) { win.placed = false; onScroll(); } })
            : null;
        if (scroller) {
            scroller.addEventListener('scroll', onScroll, { passive: true });
            resizer?.observe(scroller);
        }
        const destroy = chart.destroy.bind(chart);
        chart.destroy = () => {
            if (frame) cancelAnimationFrame(frame);
            scroller?.removeEventListener('scroll', onScroll);
            resizer?.disconnect();
            destroy();
        };
        return true;
    } catch (error) {
        console.error('Error creating browse chart:', error);
        return false;
    }
};

// Scrolls the chart so the highlighted bars are in view: left where it is when they already are,
// otherwise centred on them (clamped, so the current period lands at the right end); with no
// highlighted bar, to the right end.
function scrollBrowseChartToPeriod(scroller, anchors, n, fullW) {
    let first = -1, last = -1;
    for (let i = 0; i < n; i++) if (anchors[i]) { if (first < 0) first = i; last = i; }
    const view = scroller.clientWidth, max = Math.max(0, fullW - view);
    if (first < 0) { scroller.scrollLeft = max; return; }
    const slot = fullW / n, l = first * slot, r = (last + 1) * slot;
    if (scroller.dataset.obScrolled === String(n) && l >= scroller.scrollLeft && r <= scroller.scrollLeft + view) return;
    scroller.dataset.obScrolled = String(n);
    const target = r - l >= view ? r - view : (l + r) / 2 - view / 2;
    scroller.scrollLeft = Math.max(0, Math.min(max, Math.round(target)));
}

// Bar-click / anchor-change scroll for the virtualized tx list. Blazor passes an approximate
// pixel offset (newerCount * itemSize); we set the scrolling ancestor's scrollTop to that
// position so Virtualize fetches the items around it. Once the matching .in-period row
// renders, we snap it precisely to the top — polling briefly because Virtualize loads its
// chunk asynchronously after the scroll fires.
window.scrollListToTxOffset = function(approxOffset) {
    const list = document.querySelector('.tx-list');
    if (!list) return;
    const txScroll = list.closest('.tx-scroll');
    const overflowY = txScroll ? getComputedStyle(txScroll).overflowY : 'visible';
    const usesContainerScroll = txScroll && (overflowY === 'auto' || overflowY === 'scroll');
    // Below lg the list shares the page scroll with the chart + header — auto-scrolling
    // the whole document on a bar click jumps the chart out from under the tap and feels
    // jarring. The .in-period highlight still updates so the rows are findable manually.
    if (!usesContainerScroll) return;
    txScroll.scrollTop = approxOffset;
    let attempts = 0;
    const fineTune = () => {
        const target = document.querySelector('.tx-row.in-period');
        if (target) {
            // Scroll ONLY the txScroll container — `target.scrollIntoView()` here would
            // scroll every ancestor that needs to (including the page), jumping the
            // whole layout down to align the row under the sticky period bar.
            const targetRect = target.getBoundingClientRect();
            const scrollRect = txScroll.getBoundingClientRect();
            const offset = targetRect.top - scrollRect.top + txScroll.scrollTop;
            txScroll.scrollTo({ top: offset, behavior: 'smooth' });
        } else if (attempts++ < 12) {
            setTimeout(fineTune, 50);
        }
    };
    setTimeout(fineTune, 60);
};

window.renderChart = function(elementId, chartData) {
    const ctx = document.getElementById(elementId);
    if (!ctx) {
        console.error('Canvas element not found:', elementId);
        return false;
    }

    // Destroy existing chart if it exists
    if (window.chartInstances[elementId]) {
        window.chartInstances[elementId].destroy();
    }

    // Turn the { kind, ...params } callback descriptors from C# into real functions.
    resolveChartCallbacks(chartData);

    attachTooltipHoverHighlight(chartData);
    enableExternalTooltip(chartData);

    try {
        window.chartInstances[elementId] = new Chart(ctx, {
            ...chartData,
            plugins: [forecastShadingPlugin, monthlyNetLabelsPlugin, zeroLinePlugin]
        });
        return true;
    } catch (error) {
        console.error('Error creating chart:', error);
        return false;
    }
};
