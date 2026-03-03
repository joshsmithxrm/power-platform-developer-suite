/** Configuration for virtual scroll rendering */
interface VirtualScrollConfig {
    rowHeight: number;
    overscan: number;
    scrollContainerId: string;
    tbodyId: string;
    columnCount: number;
}

/**
 * Generates inline HTML containing two script elements for virtual scrolling:
 *   1. A <script type="application/json"> element holding the row data — prevents
 *      </script> injection attacks from user-controlled strings inside JSON.
 *   2. A <script> element with the virtual scroll logic that reads the JSON data
 *      element and renders cells safely via escapeHtml at render time.
 *
 * Row data is CellData[][]  — each cell is { text: string; url?: string }.
 * All HTML escaping happens inside the generated script, never before serialisation.
 * Uses spacer-row approach: top spacer + visible rows + bottom spacer.
 * Only re-renders when visible range changes (lastStart/lastEnd caching).
 * Uses requestAnimationFrame for smooth scroll handling.
 */
export function generateVirtualScrollScript(rowDataJson: string, config: VirtualScrollConfig): string {
    const dataElementId = `${config.scrollContainerId}-data`;
    return `
<script type="application/json" id="${dataElementId}">${rowDataJson}</script>
<script>
(function() {
    var dataEl = document.getElementById('${dataElementId}');
    if (!dataEl) return;
    var allRows = JSON.parse(dataEl.textContent);
    var ROW_HEIGHT = ${config.rowHeight};
    var OVERSCAN = ${config.overscan};
    var COL_COUNT = ${config.columnCount};
    var container = document.getElementById('${config.scrollContainerId}');
    var tbody = document.getElementById('${config.tbodyId}');
    if (!container || !tbody) return;

    function escapeHtml(str) {
        if (str === null || str === undefined) return '';
        return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    var lastStart = -1, lastEnd = -1;
    var rafId = null;

    function render() {
        var scrollTop = container.scrollTop;
        var viewportHeight = container.clientHeight;
        var totalRows = allRows.length;

        var start = Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN;
        var end = Math.ceil((scrollTop + viewportHeight) / ROW_HEIGHT) + OVERSCAN;
        start = Math.max(0, start);
        end = Math.min(totalRows, end);

        if (start === lastStart && end === lastEnd) return;
        lastStart = start;
        lastEnd = end;

        var topSpacerHeight = start * ROW_HEIGHT;
        var bottomSpacerHeight = (totalRows - end) * ROW_HEIGHT;

        var html = '';
        if (topSpacerHeight > 0) {
            html += '<tr class="virtual-spacer"><td colspan="' + COL_COUNT + '" style="height:' + topSpacerHeight + 'px"></td></tr>';
        }
        for (var i = start; i < end; i++) {
            var rowClass = i % 2 === 0 ? 'row-even' : 'row-odd';
            html += '<tr class="data-row ' + rowClass + '">';
            for (var j = 0; j < allRows[i].length; j++) {
                var cell = allRows[i][j];
                if (cell.url) {
                    html += '<td class="data-cell"><a href="' + escapeHtml(cell.url) + '" target="_blank">' + escapeHtml(cell.text) + '</a></td>';
                } else {
                    html += '<td class="data-cell">' + escapeHtml(cell.text) + '</td>';
                }
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
})();
</script>`;
}
