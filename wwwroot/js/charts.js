// Chart rendering functions
window.chartInstances = window.chartInstances || {};

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
// "€1.2k" / "€12k" so labels stay readable on narrow (mobile) charts; otherwise rounds to
// a whole-euro figure.
function fmtEuro(v, mode) {
    const abs = Math.abs(v);
    const sign = v < 0 ? '−' : '';
    if (mode === 'abbrev') {
        if (abs >= 10000) return sign + '€' + Math.round(abs / 1000) + 'k';
        if (abs >= 1000) return sign + '€' + (abs / 1000).toFixed(1) + 'k';
    }
    return sign + '€' + Math.round(abs);
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

    // Parse callback functions from strings
    if (chartData.options?.plugins?.tooltip?.callbacks) {
        if (chartData.options.plugins.tooltip.callbacks.label) {
            chartData.options.plugins.tooltip.callbacks.label = eval('(' + chartData.options.plugins.tooltip.callbacks.label + ')');
        }
    }
    if (typeof chartData.options?.plugins?.tooltip?.filter === 'string') {
        chartData.options.plugins.tooltip.filter = eval('(' + chartData.options.plugins.tooltip.filter + ')');
    }
    if (chartData.options?.scales?.y?.ticks?.callback) {
        chartData.options.scales.y.ticks.callback = eval('(' + chartData.options.scales.y.ticks.callback + ')');
    }

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

// Generic chart rendering function
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

    // Parse callback functions from strings
    if (chartData.options?.plugins?.tooltip?.callbacks) {
        if (chartData.options.plugins.tooltip.callbacks.label) {
            chartData.options.plugins.tooltip.callbacks.label = eval('(' + chartData.options.plugins.tooltip.callbacks.label + ')');
        }
        if (chartData.options.plugins.tooltip.callbacks.title) {
            chartData.options.plugins.tooltip.callbacks.title = eval('(' + chartData.options.plugins.tooltip.callbacks.title + ')');
        }
    }
    if (typeof chartData.options?.plugins?.tooltip?.filter === 'string') {
        chartData.options.plugins.tooltip.filter = eval('(' + chartData.options.plugins.tooltip.filter + ')');
    }
    if (chartData.options?.scales?.y?.ticks?.callback) {
        chartData.options.scales.y.ticks.callback = eval('(' + chartData.options.scales.y.ticks.callback + ')');
    }

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
