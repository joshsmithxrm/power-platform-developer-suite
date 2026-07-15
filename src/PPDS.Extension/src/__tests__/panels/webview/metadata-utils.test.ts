import { describe, it, expect } from 'vitest';

import { isAuxiliaryAttribute } from '../../../panels/webview/shared/metadata-utils.js';
import type { MetadataAttributeDto } from '../../../types.js';

/**
 * #1368/#1369 contract tests.
 *
 * - isAuxiliaryAttribute: AttributeOf is the deterministic marker for auxiliary
 *   attributes (lookup name/yomi companions), used to MARK rows (and power the
 *   user-initiated hide toggle) per "mark, don't mask". Missing field (older
 *   bundled CLI) must degrade to "real attribute" — pre-widening behavior.
 * - The fully-populated literal locks the widened DTO shape at compile time: if a
 *   full-fidelity field is dropped from types.ts, this file stops compiling.
 */

function makeAttr(overrides: Partial<MetadataAttributeDto> = {}): MetadataAttributeDto {
    return {
        logicalName: 'name',
        displayName: 'Account Name',
        schemaName: 'Name',
        attributeType: 'String',
        attributeTypeName: 'StringType',
        isPrimaryId: false,
        isPrimaryName: true,
        isCustomAttribute: false,
        requiredLevel: 'ApplicationRequired',
        maxLength: 160,
        minValue: null,
        maxValue: null,
        precision: null,
        targets: null,
        optionSetName: null,
        isGlobalOptionSet: false,
        options: null,
        format: 'Text',
        dateTimeBehavior: null,
        sourceType: 0,
        isSecured: false,
        description: 'Type the company or business name.',
        autoNumberFormat: null,
        ...overrides,
    };
}

describe('isAuxiliaryAttribute (#1368)', () => {
    it('flags attributes carrying attributeOf', () => {
        expect(isAuxiliaryAttribute(makeAttr({
            logicalName: 'primarycontactidname',
            displayName: null,
            attributeOf: 'primarycontactid',
        }))).toBe(true);
    });

    it('treats null attributeOf as a real attribute', () => {
        expect(isAuxiliaryAttribute(makeAttr({ attributeOf: null }))).toBe(false);
    });

    it('treats a missing attributeOf field (older bundled CLI) as a real attribute', () => {
        expect(isAuxiliaryAttribute(makeAttr())).toBe(false);
    });

    it('treats an empty-string attributeOf as a real attribute', () => {
        expect(isAuxiliaryAttribute(makeAttr({ attributeOf: '' }))).toBe(false);
    });
});

describe('MetadataAttributeDto full-fidelity shape (#1369)', () => {
    it('accepts every widened wire field', () => {
        // Required<> forces every optional field to be present — a compile-time
        // exhaustiveness lock on the widened contract.
        const full: Required<MetadataAttributeDto> = {
            ...makeAttr(),
            metadataId: '11111111-2222-3333-4444-555555555555',
            isManaged: true,
            attributeOf: 'primarycontactid',
            isLogical: true,
            isValidForCreate: true,
            isValidForUpdate: false,
            isValidForRead: true,
            isValidForForm: true,
            isValidForGrid: false,
            isValidForAdvancedFind: true,
            isSearchable: true,
            isFilterable: true,
            isSortable: true,
            isRetrievable: true,
            canBeSecuredForRead: true,
            canBeSecuredForCreate: false,
            canBeSecuredForUpdate: true,
            isAuditEnabled: true,
            isCustomizable: false,
            isRenameable: true,
            formulaDefinition: '<formula/>',
            introducedVersion: '5.0.0.0',
            deprecatedVersion: '9.0.0.0',
            externalName: 'ext_name',
            columnNumber: 42,
            createdOn: '2024-01-02T03:04:05Z',
            modifiedOn: '2025-06-07T08:09:10Z',
        };
        expect(full.attributeOf).toBe('primarycontactid');
        expect(full.isAuditEnabled).toBe(true);
    });
});
