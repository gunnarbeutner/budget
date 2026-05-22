// Chart rendering functions
window.chartInstances = window.chartInstances || {};

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

// Inline plugin: draws the monthly total of the "actual" stack just above each bar group.
const stackTotalLabelsPlugin = {
    id: 'stackTotalLabels',
    afterDatasetsDraw(chart) {
        const { ctx, data } = chart;
        if (!data || !data.labels || !data.datasets) return;

        // Find any dataset belonging to the "actual" stack so we can use its bar geometry.
        const actualDatasetIndex = data.datasets.findIndex(ds => ds.stack === 'actual');
        if (actualDatasetIndex < 0) return;

        const actualMeta = chart.getDatasetMeta(actualDatasetIndex);

        ctx.save();
        ctx.font = '600 11px system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';
        ctx.fillStyle = '#333';

        for (let i = 0; i < data.labels.length; i++) {
            let total = 0;
            for (const ds of data.datasets) {
                if (ds.stack !== 'actual') continue;
                const value = Number(ds.data[i]);
                if (!isNaN(value)) total += value;
            }

            if (total <= 0) continue;

            const bar = actualMeta.data[i];
            if (!bar) continue;

            // bar.y on the bottom dataset is the top of its own segment, not the top of the
            // whole stack — convert the running total through the y scale to land above the stack.
            const yTop = chart.scales.y.getPixelForValue(total);
            ctx.fillText('€' + total.toFixed(0), bar.x, yTop - 4);
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

// Cashflow plugin: draws the net (income − expense) for each month above the taller stack.
const monthlyNetLabelsPlugin = {
    id: 'monthlyNetLabels',
    afterDatasetsDraw(chart) {
        const { ctx, data } = chart;
        if (!data || !data.labels || !data.datasets) return;

        const hasIncome = data.datasets.some(ds => ds.stack === 'income');
        const hasExpense = data.datasets.some(ds => ds.stack === 'expenses');
        if (!hasIncome && !hasExpense) return;

        ctx.save();
        ctx.font = '600 11px system-ui, sans-serif';
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

            const net = income - expense;
            const yTop = chart.scales.y.getPixelForValue(Math.max(income, expense));
            const x = chart.scales.x.getPixelForValue(i);
            ctx.fillStyle = net >= 0 ? '#1c7a3a' : '#a02434';
            const sign = net >= 0 ? '+' : '−';
            ctx.fillText(sign + '€' + Math.abs(net).toFixed(0), x, yTop - 4);
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
