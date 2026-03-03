/** Configuration for virtual scroll rendering */
interface VirtualScrollConfig {
    rowHeight: number;
    overscan: number;
    scrollContainerId: string;
    tbodyId: string;
    columnCount: number;
}

/**
 * Generates inline JavaScript for virtual scrolling.
 * Uses spacer-row approach: top spacer + visible rows + bottom spacer.
 * Only re-renders when visible range changes (lastStart/lastEnd caching).
 * Uses requestAnimationFrame for smooth scroll handling.
 */
export function generateVirtualScrollScript(rowDataJson: string, config: VirtualScrollConfig): string {
    return `
    (function() {
        const allRows = ${rowDataJson};
        const ROW_HEIGHT = ${config.rowHeight};
        const OVERSCAN = ${config.overscan};
        const COL_COUNT = ${config.columnCount};
        const container = document.getElementById('${config.scrollContainerId}');
        const tbody = document.getElementById('${config.tbodyId}');
        if (!container || !tbody) return;

        let lastStart = -1, lastEnd = -1;
        let rafId = null;

        function render() {
            const scrollTop = container.scrollTop;
            const viewportHeight = container.clientHeight;
            const totalRows = allRows.length;

            let start = Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN;
            let end = Math.ceil((scrollTop + viewportHeight) / ROW_HEIGHT) + OVERSCAN;
            start = Math.max(0, start);
            end = Math.min(totalRows, end);

            if (start === lastStart && end === lastEnd) return;
            lastStart = start;
            lastEnd = end;

            const topSpacerHeight = start * ROW_HEIGHT;
            const bottomSpacerHeight = (totalRows - end) * ROW_HEIGHT;

            let html = '';
            if (topSpacerHeight > 0) {
                html += '<tr class="virtual-spacer"><td colspan="' + COL_COUNT + '" style="height:' + topSpacerHeight + 'px"></td></tr>';
            }
            for (let i = start; i < end; i++) {
                const rowClass = i % 2 === 0 ? 'row-even' : 'row-odd';
                html += '<tr class="data-row ' + rowClass + '">';
                for (let j = 0; j < allRows[i].length; j++) {
                    html += '<td class="data-cell">' + allRows[i][j] + '</td>';
                }
                html += '</tr>';
            }
            if (bottomSpacerHeight > 0) {
                html += '<tr class="virtual-spacer"><td colspan="' + COL_COUNT + '" style="height:' + bottomSpacerHeight + 'px"></td></tr>';
            }
            tbody.innerHTML = html;
        }

        container.addEventListener('scroll', function() {
            if (rafId) cancelAnimationFrame(rafId);
            rafId = requestAnimationFrame(render);
        });
        render();
    })();`;
}
