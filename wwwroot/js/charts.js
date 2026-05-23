// Chart rendering functions
window.chartInstances = window.chartInstances || {};

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
    tt.callbacks.labelTextColor = function (context) {
        const hovered = context.chart._activeHover;
        if (hovered && context.datasetIndex !== hovered.d) {
            return 'rgba(255, 255, 255, 0.45)';
        }
        if (typeof userLabelTextColor === 'function') {
            return userLabelTextColor.call(this, context);
        }
        return '#fff';
    };
    tt.callbacks.labelColor = function (context) {
        const hovered = context.chart._activeHover;
        const ds = context.dataset || {};
        const bg = Array.isArray(ds.backgroundColor)
            ? ds.backgroundColor[context.dataIndex] ?? ds.backgroundColor[0]
            : ds.backgroundColor;
        const bd = ds.borderColor || bg;
        const dim = hovered && context.datasetIndex !== hovered.d;
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

// Custom tooltip positioner: anchors to whichever outer edge of the hovered bar group has
// room to grow into. Cursor in the left half of the chart → anchor on the right edge of the
// bars so the tooltip extends rightward. Cursor in the right half → anchor on the left edge
// so it extends leftward. Without this flip, Chart.js's auto-alignment would render the
// tooltip body back across the bars you're hovering when you reach the chart's edge.
if (window.Chart && Chart.Tooltip && Chart.Tooltip.positioners) {
    Chart.Tooltip.positioners.outerEdge = function (elements, eventPosition) {
        if (!elements || !elements.length) return false;
        let minLeft = Infinity;
        let maxRight = -Infinity;
        let ySum = 0;
        let count = 0;
        for (const el of elements) {
            const bar = el.element;
            if (!bar || typeof bar.x !== 'number') continue;
            const half = (bar.width || 0) / 2;
            if (bar.x - half < minLeft) minLeft = bar.x - half;
            if (bar.x + half > maxRight) maxRight = bar.x + half;
            ySum += bar.y;
            count++;
        }
        if (!count) return false;

        const area = this.chart.chartArea;
        const center = (area.left + area.right) / 2;
        const cursorX = eventPosition?.x ?? (minLeft + maxRight) / 2;
        const anchorX = cursorX < center ? maxRight : minLeft;

        return { x: anchorX, y: ySum / count };
    };
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

        ctx.save();
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';

        for (let i = 0; i < data.labels.length; i++) {
            let actualSum = 0;
            let budgetSum = 0;
            for (const ds of data.datasets) {
                const v = Number(ds.data[i]);
                if (isNaN(v)) continue;
                if (ds.stack === 'actual') actualSum += v;
                else if (ds.stack === 'budget') budgetSum += v;
            }

            // Budget stack label
            if (budgetMeta && budgetSum > 0) {
                const bar = budgetMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(budgetSum);
                    ctx.font = '600 10px system-ui, sans-serif';
                    ctx.fillStyle = '#777';
                    ctx.fillText('Budget', bar.x, yTop - 14);
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText('€' + budgetSum.toFixed(0), bar.x, yTop - 4);
                }
            }

            // Actual stack label
            if (actualMeta && actualSum > 0) {
                const bar = actualMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(actualSum);
                    if (both) {
                        ctx.font = '600 10px system-ui, sans-serif';
                        ctx.fillStyle = '#777';
                        ctx.fillText('Actual', bar.x, yTop - 14);
                    }
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText('€' + actualSum.toFixed(0), bar.x, yTop - 4);
                }
            }
        }
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
    if (chartData.options?.scales?.y?.ticks?.callback) {
        chartData.options.scales.y.ticks.callback = eval('(' + chartData.options.scales.y.ticks.callback + ')');
    }

    attachTooltipHoverHighlight(chartData);

    try {
        window.chartInstances[elementId] = new Chart(ctx, {
            ...chartData,
            plugins: [stackTotalLabelsPlugin]
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
const forecastShadingPlugin = {
    id: 'forecastShading',
    beforeDatasetsDraw(chart, _args, options) {
        const startMonth = options?.startMonth;
        if (!startMonth || startMonth > 12) return;

        const xScale = chart.scales?.x;
        const chartArea = chart.chartArea;
        if (!xScale || !chartArea) return;

        const startIdx = startMonth - 1;
        if (startIdx > 11) return;

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

        ctx.save();
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';

        for (let i = 0; i < data.labels.length; i++) {
            let income = 0;
            let expense = 0;
            for (const ds of data.datasets) {
                const v = Number(ds.data[i]);
                if (isNaN(v)) continue;
                if (ds.stack === 'income') income += v;
                else if (ds.stack === 'expenses') expense += v;
            }
            if (income === 0 && expense === 0) continue;

            // Income stack: small "Income" caption + total
            if (incomeMeta && income > 0) {
                const bar = incomeMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(income);
                    ctx.font = '600 10px system-ui, sans-serif';
                    ctx.fillStyle = '#777';
                    ctx.fillText('Income', bar.x, yTop - 14);
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText('€' + income.toFixed(0), bar.x, yTop - 4);
                }
            }

            // Expense stack: small "Expenses" caption + total
            if (expenseMeta && expense > 0) {
                const bar = expenseMeta.data[i];
                if (bar) {
                    const yTop = chart.scales.y.getPixelForValue(expense);
                    ctx.font = '600 10px system-ui, sans-serif';
                    ctx.fillStyle = '#777';
                    ctx.fillText('Expenses', bar.x, yTop - 14);
                    ctx.font = '600 11px system-ui, sans-serif';
                    ctx.fillStyle = '#333';
                    ctx.fillText('€' + expense.toFixed(0), bar.x, yTop - 4);
                }
            }

            // Net sits above the taller stack with enough offset to clear its two-line label.
            const net = income - expense;
            const yTallerTop = chart.scales.y.getPixelForValue(Math.max(income, expense));
            const x = chart.scales.x.getPixelForValue(i);
            ctx.font = '600 11px system-ui, sans-serif';
            ctx.fillStyle = net >= 0 ? '#1c7a3a' : '#a02434';
            const sign = net >= 0 ? '+' : '−';
            ctx.fillText(sign + '€' + Math.abs(net).toFixed(0), x, yTallerTop - 28);
        }
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
    }
    if (chartData.options?.scales?.y?.ticks?.callback) {
        chartData.options.scales.y.ticks.callback = eval('(' + chartData.options.scales.y.ticks.callback + ')');
    }

    attachTooltipHoverHighlight(chartData);

    try {
        window.chartInstances[elementId] = new Chart(ctx, {
            ...chartData,
            plugins: [forecastShadingPlugin, monthlyNetLabelsPlugin]
        });
        return true;
    } catch (error) {
        console.error('Error creating chart:', error);
        return false;
    }
};
