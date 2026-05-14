import type { TraceFilterDto } from '../types.js';

import type { AdvancedQueryViewDto } from './webview/shared/message-types.js';

/**
 * Convert advanced query builder state to a TraceFilterDto for the daemon.
 * Pure function — extracted so unit tests can import without the vscode module.
 */
export function convertAdvancedQuery(query: AdvancedQueryViewDto): TraceFilterDto {
    const filter: TraceFilterDto = {};

    for (const qfId of query.quickFilterIds) {
        switch (qfId) {
            case 'exceptions': filter.hasException = true; break;
            case 'success': filter.hasException = false; break;
            case 'last-hour': filter.startDate = new Date(Date.now() - 3600000).toISOString(); break;
            case 'last-24h': filter.startDate = new Date(Date.now() - 86400000).toISOString(); break;
            case 'today': {
                const today = new Date();
                today.setHours(0, 0, 0, 0);
                filter.startDate = today.toISOString();
                break;
            }
            case 'async': filter.mode = 'Async'; break;
            case 'sync': filter.mode = 'Sync'; break;
            case 'recursive': break;
        }
    }

    for (const cond of query.conditions) {
        if (!cond.enabled) continue;
        const val = cond.value;
        switch (cond.field) {
            case 'Plugin Name': filter.typeName = val; break;
            case 'Entity': filter.primaryEntity = val; break;
            case 'Message': filter.messageName = val; break;
            case 'Duration': if (val) filter.minDurationMs = parseInt(val, 10) || undefined; break;
            case 'Created On': {
                if (val) {
                    const date = new Date(val);
                    if (!isNaN(date.getTime())) {
                        filter.startDate = date.toISOString();
                    }
                }
                break;
            }
            case 'Mode': filter.mode = val; break;
            case 'Status': {
                if (val !== 'Exception' && val !== 'Success') break;
                const matches = val === 'Exception';
                filter.hasException = cond.operator === 'Not Equals' ? !matches : matches;
                break;
            }
        }
    }

    return filter;
}
