import { generateVirtualScrollScript } from './virtualScrollScript.js';
import type { QueryResultResponse } from '../types.js';

const ROW_HEIGHT = 36;
const OVERSCAN = 5;
const CONTAINER_HEIGHT = 400;

/**
 * Structured cell data for virtual scroll rendering.
 * text is always plain text (NOT pre-escaped).
 * url, if present, makes the cell a clickable link (for lookup fields and primary keys).
 */
export interface CellData {
    text: string;
    url?: string;
}

/**
 * Renders query results as HTML with virtual scrolling for notebook cell output.
 *
 * @param containerId - Optional deterministic container ID for testing. When omitted,
 *   a unique ID is generated using Date.now() + Math.random().
 */
export function renderResultsHtml(result: QueryResultResponse, environmentUrl: string | undefined, containerId?: string): string {
    if (result.records.length === 0) {
        return renderEmptyResults();
    }

    const uniqueId = containerId ?? generateUniqueId();
    const scrollContainerId = `scrollContainer_${uniqueId}`;
    const tbodyId = `tableBody_${uniqueId}`;

    const headerCells = result.columns
        .map((col, idx) => {
            const isLast = idx === result.columns.length - 1;
            const label = col.alias ?? col.displayName ?? col.logicalName;
            return `<th class="header-cell${isLast ? ' last' : ''}">${escapeHtml(label)}</th>`;
        })
        .join('');

    const rowData = prepareRowData(result, environmentUrl);

    const summary = `<div class="results-summary">${result.count} row${result.count !== 1 ? 's' : ''} returned in ${result.executionTimeMs}ms${result.moreRecords ? ' (more available)' : ''}</div>`;

    return `
        <style>${getNotebookStyles()}</style>
        ${summary}
        <div class="results-container">
            <div class="virtual-scroll-container" id="${scrollContainerId}">
                <table class="results-table">
                    <thead><tr>${headerCells}</tr></thead>
                    <tbody id="${tbodyId}"></tbody>
                </table>
            </div>
        </div>
        ${generateVirtualScrollScript(JSON.stringify(rowData), {
            rowHeight: ROW_HEIGHT,
            overscan: OVERSCAN,
            scrollContainerId,
            tbodyId,
            columnCount: result.columns.length
        })}
    `;
}

function prepareRowData(result: QueryResultResponse, environmentUrl: string | undefined): CellData[][] {
    const primaryKeyColumn = result.entityName ? `${result.entityName}id` : null;

    return result.records.map(record => {
        return result.columns.map(col => {
            const key = col.alias ?? col.logicalName;
            const rawValue = record[key];

            if (rawValue === null || rawValue === undefined) {
                return { text: '' };
            }

            // Structured lookup value
            if (typeof rawValue === 'object' && rawValue !== null && 'entityId' in rawValue) {
                const lookup = rawValue as { value: unknown; formatted: string | null; entityType: string; entityId: string };
                const displayText = String(lookup.formatted ?? lookup.value ?? '');
                if (environmentUrl && lookup.entityType && lookup.entityId) {
                    const url = buildRecordUrl(environmentUrl, lookup.entityType, lookup.entityId);
                    return { text: displayText, url };
                }
                return { text: displayText };
            }

            // Structured formatted value
            if (typeof rawValue === 'object' && rawValue !== null && 'formatted' in rawValue) {
                const formatted = rawValue as { value: unknown; formatted: string | null };
                return { text: String(formatted.formatted ?? formatted.value ?? '') };
            }

            // Primary key column — make GUID clickable
            const stringValue = String(rawValue);
            if (primaryKeyColumn && environmentUrl && result.entityName
                && col.logicalName.toLowerCase() === primaryKeyColumn.toLowerCase()
                && isGuid(stringValue)) {
                const url = buildRecordUrl(environmentUrl, result.entityName, stringValue);
                return { text: stringValue, url };
            }

            return { text: stringValue };
        });
    });
}

function buildRecordUrl(dataverseUrl: string, entityLogicalName: string, recordId: string): string {
    const baseUrl = dataverseUrl.replace(/\/+$/, '');
    return `${baseUrl}/main.aspx?pagetype=entityrecord&etn=${encodeURIComponent(entityLogicalName)}&id=${encodeURIComponent(recordId)}`;
}

function isGuid(value: string): boolean {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function escapeHtml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}

function generateUniqueId(): string {
    return `${Date.now().toString(36)}_${Math.random().toString(36).substring(2, 8)}`;
}

function renderEmptyResults(): string {
    return `<style>.no-results { font-family: var(--vscode-font-family); padding: 20px; text-align: center; color: var(--vscode-descriptionForeground); font-style: italic; }</style><div class="no-results">No results returned</div>`;
}

function getNotebookStyles(): string {
    return `
        .results-summary {
            font-family: var(--vscode-font-family);
            color: var(--vscode-descriptionForeground);
            padding: 4px 0 8px 0;
            font-size: 12px;
        }
        .results-container {
            font-family: var(--vscode-font-family);
            color: var(--vscode-foreground);
            background: var(--vscode-editor-background);
            margin: 0; padding: 0;
        }
        .virtual-scroll-container {
            max-height: ${CONTAINER_HEIGHT}px;
            overflow-y: auto; overflow-x: auto;
            position: relative; margin: 0; padding: 0;
        }
        .results-table {
            width: max-content; min-width: 100%;
            border-collapse: collapse; margin: 0;
        }
        .header-cell {
            padding: 8px 12px; text-align: left; font-weight: 600;
            background: var(--vscode-button-background);
            color: var(--vscode-button-foreground);
            border-bottom: 2px solid var(--vscode-panel-border);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            white-space: nowrap; position: sticky; top: 0; z-index: 10;
        }
        .header-cell.last { border-right: none; }
        .data-row {
            height: ${ROW_HEIGHT}px;
            border-bottom: 1px solid var(--vscode-panel-border);
        }
        .data-row.row-even { background: var(--vscode-list-inactiveSelectionBackground); }
        .data-row.row-odd { background: transparent; }
        .data-row:hover { background: var(--vscode-list-hoverBackground); }
        .data-cell {
            padding: 8px 12px; white-space: nowrap;
            vertical-align: middle; text-align: left;
        }
        .data-cell a { color: var(--vscode-textLink-foreground); text-decoration: none; }
        .data-cell a:hover { color: var(--vscode-textLink-activeForeground); text-decoration: underline; }
        .virtual-spacer td { padding: 0 !important; border: none !important; }
    `;
}
