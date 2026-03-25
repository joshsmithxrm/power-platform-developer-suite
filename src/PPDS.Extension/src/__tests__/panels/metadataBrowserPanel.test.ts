import { describe, it, expect } from 'vitest';

import type {
    MetadataBrowserPanelWebviewToHost,
    MetadataBrowserPanelHostToWebview,
    MetadataEntityViewDto,
} from '../../panels/webview/shared/message-types.js';

import type {
    MetadataEntityDetailDto,
    MetadataGlobalChoiceSummaryDto,
    MetadataOptionSetDto,
} from '../../types.js';

describe('MetadataBrowserPanel message types', () => {
    describe('WebviewToHost', () => {
        it('covers all commands', () => {
            const messages: MetadataBrowserPanelWebviewToHost[] = [
                { command: 'ready' },
                { command: 'refresh' },
                { command: 'setIncludeIntersect', includeIntersect: true },
                { command: 'selectEntity', logicalName: 'account' },
                { command: 'selectGlobalChoice', name: 'statuscode' },
                { command: 'requestEnvironmentList' },
                { command: 'openInMaker' },
                { command: 'openInMaker', entityLogicalName: 'account' },
                { command: 'copyToClipboard', text: 'copied' },
                { command: 'webviewError', error: 'test error', stack: 'Error\n  at ...' },
            ];
            expect(messages).toHaveLength(10);
        });

        it('webviewError stack is optional', () => {
            const msg: MetadataBrowserPanelWebviewToHost = {
                command: 'webviewError',
                error: 'test',
            };
            expect(msg.command).toBe('webviewError');
        });

        it('openInMaker entityLogicalName is optional', () => {
            const withEntity: MetadataBrowserPanelWebviewToHost = {
                command: 'openInMaker',
                entityLogicalName: 'contact',
            };
            const withoutEntity: MetadataBrowserPanelWebviewToHost = {
                command: 'openInMaker',
            };
            expect(withEntity.command).toBe('openInMaker');
            expect(withoutEntity.command).toBe('openInMaker');
        });

        it('setIncludeIntersect toggles intersect entity visibility', () => {
            const show: MetadataBrowserPanelWebviewToHost = {
                command: 'setIncludeIntersect',
                includeIntersect: true,
            };
            const hide: MetadataBrowserPanelWebviewToHost = {
                command: 'setIncludeIntersect',
                includeIntersect: false,
            };
            expect(show.command).toBe('setIncludeIntersect');
            expect(hide.command).toBe('setIncludeIntersect');
        });
    });

    describe('HostToWebview', () => {
        it('covers all commands', () => {
            const messages: MetadataBrowserPanelHostToWebview[] = [
                { command: 'updateEnvironment', name: 'dev', envType: 'Sandbox', envColor: '#00ff00' },
                { command: 'entitiesLoaded', entities: [], intersectHiddenCount: 0 },
                { command: 'globalChoicesLoaded', choices: [] },
                { command: 'globalChoiceDetailLoaded', choice: { name: 'statuscode', displayName: 'Status', optionSetType: 'Picklist', isGlobal: true, options: [] } },
                { command: 'entityDetailLoaded', entity: {
                    logicalName: 'account', schemaName: 'Account', displayName: 'Account',
                    isCustomEntity: false, isManaged: false, ownershipType: 'UserOwned',
                    objectTypeCode: 1, description: null,
                    primaryIdAttribute: 'accountid', primaryNameAttribute: 'name',
                    entitySetName: 'accounts', isActivity: false,
                    attributes: [], oneToManyRelationships: [], manyToOneRelationships: [],
                    manyToManyRelationships: [], keys: [], privileges: [], globalOptionSets: [],
                } },
                { command: 'entityDetailLoading', logicalName: 'account' },
                { command: 'globalChoiceDetailLoading', name: 'statuscode' },
                { command: 'loading' },
                { command: 'error', message: 'test error' },
                { command: 'daemonReconnected' },
            ];
            expect(messages).toHaveLength(10);
        });

        it('updateEnvironment accepts null envType and envColor', () => {
            const msg: MetadataBrowserPanelHostToWebview = {
                command: 'updateEnvironment',
                name: 'test',
                envType: null,
                envColor: null,
            };
            expect(msg.envType).toBeNull();
            expect(msg.envColor).toBeNull();
        });

        it('entitiesLoaded reports intersect hidden count', () => {
            const msg: MetadataBrowserPanelHostToWebview = {
                command: 'entitiesLoaded',
                entities: [
                    {
                        logicalName: 'account',
                        schemaName: 'Account',
                        displayName: 'Account',
                        isCustomEntity: false,
                        isManaged: false,
                        ownershipType: 'UserOwned',
                        description: null,
                    },
                ],
                intersectHiddenCount: 42,
            };
            if (msg.command === 'entitiesLoaded') {
                expect(msg.entities).toHaveLength(1);
                expect(msg.intersectHiddenCount).toBe(42);
            }
        });
    });

    describe('MetadataEntityViewDto', () => {
        it('has all required fields', () => {
            const dto: MetadataEntityViewDto = {
                logicalName: 'account',
                schemaName: 'Account',
                displayName: 'Account',
                isCustomEntity: false,
                isManaged: false,
                ownershipType: 'UserOwned',
                description: 'Business entity for companies',
            };
            expect(dto.logicalName).toBe('account');
            expect(dto.isCustomEntity).toBe(false);
            expect(dto.ownershipType).toBe('UserOwned');
        });

        it('handles null optional fields', () => {
            const dto: MetadataEntityViewDto = {
                logicalName: 'cr_custom',
                schemaName: 'cr_custom',
                displayName: 'Custom Entity',
                isCustomEntity: true,
                isManaged: true,
                ownershipType: null,
                description: null,
            };
            expect(dto.ownershipType).toBeNull();
            expect(dto.description).toBeNull();
        });
    });

    describe('MetadataGlobalChoiceSummaryDto', () => {
        it('has all required fields', () => {
            const dto: MetadataGlobalChoiceSummaryDto = {
                name: 'socialprofilenetworktype',
                displayName: 'Social Profile Network Type',
                optionSetType: 'Picklist',
                isCustomOptionSet: false,
                isManaged: true,
                optionCount: 5,
                description: 'Social network type',
            };
            expect(dto.name).toBe('socialprofilenetworktype');
            expect(dto.optionCount).toBe(5);
            expect(dto.isCustomOptionSet).toBe(false);
        });

        it('handles null description', () => {
            const dto: MetadataGlobalChoiceSummaryDto = {
                name: 'cr_customchoice',
                displayName: 'Custom Choice',
                optionSetType: 'Picklist',
                isCustomOptionSet: true,
                isManaged: false,
                optionCount: 3,
                description: null,
            };
            expect(dto.description).toBeNull();
        });
    });

    describe('MetadataOptionSetDto', () => {
        it('has all required fields', () => {
            const dto: MetadataOptionSetDto = {
                name: 'statuscode',
                displayName: 'Status Reason',
                optionSetType: 'Status',
                isGlobal: true,
                options: [
                    { value: 1, label: 'Active', color: '#00ff00', description: null },
                    { value: 2, label: 'Inactive', color: null, description: 'Record is inactive' },
                ],
            };
            expect(dto.name).toBe('statuscode');
            expect(dto.isGlobal).toBe(true);
            expect(dto.options).toHaveLength(2);
            expect(dto.options[0].label).toBe('Active');
        });

        it('handles empty options array', () => {
            const dto: MetadataOptionSetDto = {
                name: 'emptychoice',
                displayName: null,
                optionSetType: 'Picklist',
                isGlobal: false,
                options: [],
            };
            expect(dto.options).toHaveLength(0);
        });
    });

    describe('MetadataEntityDetailDto', () => {
        it('extends summary with detail fields', () => {
            const dto: MetadataEntityDetailDto = {
                logicalName: 'account',
                schemaName: 'Account',
                displayName: 'Account',
                isCustomEntity: false,
                isManaged: false,
                ownershipType: 'UserOwned',
                objectTypeCode: 1,
                description: null,
                primaryIdAttribute: 'accountid',
                primaryNameAttribute: 'name',
                entitySetName: 'accounts',
                isActivity: false,
                attributes: [
                    {
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
                        format: null,
                        dateTimeBehavior: null,
                        sourceType: null,
                        isSecured: false,
                        description: null,
                        autoNumberFormat: null,
                    },
                ],
                oneToManyRelationships: [],
                manyToOneRelationships: [],
                manyToManyRelationships: [],
                keys: [],
                privileges: [],
                globalOptionSets: [],
            };
            expect(dto.primaryIdAttribute).toBe('accountid');
            expect(dto.primaryNameAttribute).toBe('name');
            expect(dto.entitySetName).toBe('accounts');
            expect(dto.isActivity).toBe(false);
            expect(dto.attributes).toHaveLength(1);
            expect(dto.attributes[0].isPrimaryName).toBe(true);
        });
    });
});
