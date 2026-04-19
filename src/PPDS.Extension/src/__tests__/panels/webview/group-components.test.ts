import { describe, it, expect } from 'vitest';

import { groupComponentsByType } from '../../../panels/webview/shared/group-components.js';

interface TestComponent {
    componentTypeName: string;
    objectId: string;
}

const makeComponent = (typeName: string, objectId: string): TestComponent => ({
    componentTypeName: typeName,
    objectId,
});

describe('groupComponentsByType', () => {
    it('returns an empty array for empty input', () => {
        expect(groupComponentsByType([])).toEqual([]);
    });

    it('returns a single group when all components share a type', () => {
        const components = [
            makeComponent('Entity', 'a'),
            makeComponent('Entity', 'b'),
        ];

        const result = groupComponentsByType(components);

        expect(result).toHaveLength(1);
        expect(result[0].typeName).toBe('Entity');
        expect(result[0].components).toHaveLength(2);
    });

    it('sorts groups alphabetically by type name', () => {
        const components = [
            makeComponent('Workflow', 'w'),
            makeComponent('Entity', 'e'),
            makeComponent('OptionSet', 'o'),
            makeComponent('Attribute', 'a'),
        ];

        const result = groupComponentsByType(components);

        expect(result.map(g => g.typeName)).toEqual([
            'Attribute',
            'Entity',
            'OptionSet',
            'Workflow',
        ]);
    });

    it('preserves original order within each group', () => {
        const first = makeComponent('Entity', 'account');
        const second = makeComponent('Entity', 'contact');
        const third = makeComponent('Entity', 'lead');

        const result = groupComponentsByType([first, second, third]);

        expect(result).toHaveLength(1);
        expect(result[0].components.map(c => c.objectId)).toEqual([
            'account',
            'contact',
            'lead',
        ]);
    });

    /**
     * Regression guard: shape must match the pre-extraction logic from
     * `SolutionsPanel.ts` (manual Map + sort).
     */
    it('matches pre-extraction shape', () => {
        const components = [
            makeComponent('Workflow', 'w1'),
            makeComponent('Entity', 'account'),
            makeComponent('Workflow', 'w2'),
            makeComponent('Entity', 'contact'),
            makeComponent('OptionSet', 'priority'),
        ];

        // Old shape — captured verbatim from SolutionsPanel.ts prior to extraction.
        const groupMap = new Map<string, TestComponent[]>();
        for (const component of components) {
            const typeName = component.componentTypeName;
            const group = groupMap.get(typeName);
            if (group) {
                group.push(component);
            } else {
                groupMap.set(typeName, [component]);
            }
        }
        const expected = Array.from(groupMap.entries())
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([typeName, comps]) => ({ typeName, components: comps }));

        const actual = groupComponentsByType(components);

        expect(actual.length).toBe(expected.length);
        for (let i = 0; i < expected.length; i++) {
            expect(actual[i].typeName).toBe(expected[i].typeName);
            expect(actual[i].components).toEqual(expected[i].components);
        }
    });
});
