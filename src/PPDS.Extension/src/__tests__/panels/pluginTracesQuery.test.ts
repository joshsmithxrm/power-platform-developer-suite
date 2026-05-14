import { describe, it, expect } from 'vitest';

import { convertAdvancedQuery } from '../../panels/pluginTracesQuery.js';
import type {
    AdvancedQueryViewDto,
    QueryConditionViewDto,
} from '../../panels/webview/shared/message-types.js';

function cond(overrides: Partial<QueryConditionViewDto>): QueryConditionViewDto {
    return {
        id: 'c1',
        enabled: true,
        field: '',
        operator: 'Equals',
        value: '',
        logicalOperator: 'and',
        ...overrides,
    };
}

function query(overrides: Partial<AdvancedQueryViewDto>): AdvancedQueryViewDto {
    return { quickFilterIds: [], conditions: [], ...overrides };
}

describe('convertAdvancedQuery', () => {
    describe('quick filters', () => {
        it('exceptions quick filter sets hasException=true', () => {
            expect(convertAdvancedQuery(query({ quickFilterIds: ['exceptions'] })).hasException).toBe(true);
        });

        it('success quick filter sets hasException=false', () => {
            expect(convertAdvancedQuery(query({ quickFilterIds: ['success'] })).hasException).toBe(false);
        });

        it('async quick filter sets mode=Async', () => {
            expect(convertAdvancedQuery(query({ quickFilterIds: ['async'] })).mode).toBe('Async');
        });

        it('sync quick filter sets mode=Sync', () => {
            expect(convertAdvancedQuery(query({ quickFilterIds: ['sync'] })).mode).toBe('Sync');
        });

        it('last-hour quick filter sets startDate roughly one hour ago', () => {
            const before = Date.now();
            const filter = convertAdvancedQuery(query({ quickFilterIds: ['last-hour'] }));
            const after = Date.now();
            const startMs = new Date(filter.startDate!).getTime();
            expect(startMs).toBeGreaterThanOrEqual(before - 3600000 - 1000);
            expect(startMs).toBeLessThanOrEqual(after - 3600000 + 1000);
        });

        it('recursive quick filter is a no-op', () => {
            expect(convertAdvancedQuery(query({ quickFilterIds: ['recursive'] }))).toEqual({});
        });
    });

    describe('Status condition (regression for #1006)', () => {
        it('Status Equals Exception sets hasException=true', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Status', operator: 'Equals', value: 'Exception' })],
            }));
            expect(filter.hasException).toBe(true);
        });

        it('Status Equals Success sets hasException=false', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Status', operator: 'Equals', value: 'Success' })],
            }));
            expect(filter.hasException).toBe(false);
        });

        it('Status Not Equals Exception sets hasException=false', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Status', operator: 'Not Equals', value: 'Exception' })],
            }));
            expect(filter.hasException).toBe(false);
        });

        it('Status Not Equals Success sets hasException=true', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Status', operator: 'Not Equals', value: 'Success' })],
            }));
            expect(filter.hasException).toBe(true);
        });

        it('Status with unknown value is ignored', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Status', operator: 'Equals', value: 'Whatever' })],
            }));
            expect(filter.hasException).toBeUndefined();
        });

        it('disabled Status condition is ignored', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ enabled: false, field: 'Status', operator: 'Equals', value: 'Exception' })],
            }));
            expect(filter.hasException).toBeUndefined();
        });
    });

    describe('other conditions', () => {
        it('maps Plugin Name to typeName', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Plugin Name', value: 'MyPlugin' })],
            }));
            expect(filter.typeName).toBe('MyPlugin');
        });

        it('maps Entity to primaryEntity', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Entity', value: 'account' })],
            }));
            expect(filter.primaryEntity).toBe('account');
        });

        it('maps Message to messageName', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Message', value: 'Update' })],
            }));
            expect(filter.messageName).toBe('Update');
        });

        it('maps Duration to minDurationMs as integer', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Duration', value: '500' })],
            }));
            expect(filter.minDurationMs).toBe(500);
        });

        it('maps Mode to mode', () => {
            const filter = convertAdvancedQuery(query({
                conditions: [cond({ field: 'Mode', value: 'Async' })],
            }));
            expect(filter.mode).toBe('Async');
        });
    });

    it('combines quick filter and Status condition (last write wins)', () => {
        const filter = convertAdvancedQuery(query({
            quickFilterIds: ['exceptions'],
            conditions: [cond({ field: 'Status', operator: 'Equals', value: 'Success' })],
        }));
        expect(filter.hasException).toBe(false);
    });
});
