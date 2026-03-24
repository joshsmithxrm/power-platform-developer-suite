import { describe, it, expect } from 'vitest';

import type {
    PluginsPanelWebviewToHost,
    PluginsPanelHostToWebview,
    PluginTreeData,
    PluginTreeNode,
    AttributeViewDto,
} from '../../panels/webview/shared/message-types.js';

describe('PluginsPanel message types', () => {
    describe('WebviewToHost', () => {
        it('covers all commands', () => {
            const messages: PluginsPanelWebviewToHost[] = [
                { command: 'ready' },
                { command: 'setViewMode', mode: 'assembly' },
                { command: 'setViewMode', mode: 'message' },
                { command: 'setViewMode', mode: 'entity' },
                { command: 'expandNode', nodeId: 'assembly:MyAssembly', nodeType: 'assembly' },
                { command: 'selectNode', nodeId: 'step:step-1', nodeType: 'step' },
                { command: 'search', text: 'account' },
                { command: 'applyFilter', hideHidden: true, hideMicrosoft: false },
                { command: 'registerEntity', entityType: 'assembly', fields: { content: 'base64==' } },
                { command: 'registerEntity', entityType: 'step', parentId: 'type:MyPlugin', fields: { name: 'step1' } },
                { command: 'updateEntity', entityType: 'step', id: 'step-guid', fields: { name: 'updated' } },
                { command: 'toggleStep', id: 'step-guid', enabled: true },
                { command: 'toggleStep', id: 'step-guid', enabled: false },
                { command: 'unregister', entityType: 'assembly', id: 'asm-guid', force: false },
                { command: 'unregister', entityType: 'step', id: 'step-guid', force: true },
                { command: 'downloadBinary', entityType: 'assembly', id: 'asm-guid' },
                { command: 'requestEnvironmentList' },
                { command: 'copyToClipboard', text: 'copied text' },
                { command: 'webviewError', error: 'something broke', stack: 'Error\n  at ...' },
            ];
            expect(messages).toHaveLength(19);
        });

        it('webviewError stack is optional', () => {
            const msg: PluginsPanelWebviewToHost = {
                command: 'webviewError',
                error: 'test error',
            };
            expect(msg.command).toBe('webviewError');
        });

        it('applyFilter accepts both boolean combinations', () => {
            const hideAll: PluginsPanelWebviewToHost = {
                command: 'applyFilter',
                hideHidden: true,
                hideMicrosoft: true,
            };
            const showAll: PluginsPanelWebviewToHost = {
                command: 'applyFilter',
                hideHidden: false,
                hideMicrosoft: false,
            };
            expect(hideAll.command).toBe('applyFilter');
            expect(showAll.command).toBe('applyFilter');
        });

        it('registerEntity accepts all entity types', () => {
            const types = ['assembly', 'package', 'step', 'image', 'serviceEndpoint', 'customApi'];
            const messages: PluginsPanelWebviewToHost[] = types.map(entityType => ({
                command: 'registerEntity' as const,
                entityType,
                fields: {},
            }));
            expect(messages).toHaveLength(types.length);
        });
    });

    describe('HostToWebview', () => {
        it('covers all commands', () => {
            const sampleNode: PluginTreeNode = {
                id: 'step:step-1',
                name: 'Create Account',
                nodeType: 'step',
            };
            const sampleTreeData: PluginTreeData = {
                assemblies: [],
                packages: [],
                serviceEndpoints: [],
                customApis: [],
                dataSources: [],
            };
            const messages: PluginsPanelHostToWebview[] = [
                { command: 'updateEnvironment', name: 'dev', envType: 'Sandbox', envColor: '#00ff00' },
                { command: 'treeLoaded', data: sampleTreeData },
                { command: 'childrenLoaded', parentId: 'assembly:MyAssembly', children: [] },
                { command: 'nodeUpdated', node: sampleNode },
                { command: 'nodeRemoved', nodeId: 'step:step-1' },
                { command: 'detailLoaded', detail: { id: 'step-1', name: 'Create Account' } },
                { command: 'messagesLoaded', messages: ['Create', 'Update', 'Delete'] },
                { command: 'entitiesLoaded', entities: ['account', 'contact'] },
                { command: 'attributesLoaded', attributes: [] },
                { command: 'showRegisterForm', formType: 'step' },
                { command: 'showRegisterForm', formType: 'assembly', parentId: 'pkg:MyPackage' },
                { command: 'showRegisterForm', formType: 'serviceendpoint', contract: 'WebhookHttpEndpoint' },
                { command: 'showUpdateForm', formType: 'step', id: 'step-1', data: {} },
                { command: 'showUpdateForm', formType: 'customapi', id: 'api-1', data: {}, contract: 'POST' },
                { command: 'loading' },
                { command: 'error', message: 'something went wrong' },
                { command: 'daemonReconnected' },
            ];
            expect(messages).toHaveLength(17);
        });

        it('updateEnvironment accepts null envType and envColor', () => {
            const msg: PluginsPanelHostToWebview = {
                command: 'updateEnvironment',
                name: 'test',
                envType: null,
                envColor: null,
            };
            expect(msg.command).toBe('updateEnvironment');
        });

        it('showRegisterForm covers all form types', () => {
            const formTypes: Array<Extract<PluginsPanelHostToWebview, { command: 'showRegisterForm' }>['formType']> = [
                'step', 'image', 'assembly', 'package', 'webhook', 'serviceendpoint', 'customapi', 'dataprovider',
            ];
            const messages: PluginsPanelHostToWebview[] = formTypes.map(formType => ({
                command: 'showRegisterForm' as const,
                formType,
            }));
            expect(messages).toHaveLength(8);
        });

        it('showUpdateForm covers all form types', () => {
            const formTypes: Array<Extract<PluginsPanelHostToWebview, { command: 'showUpdateForm' }>['formType']> = [
                'step', 'image', 'assembly', 'package', 'webhook', 'serviceendpoint', 'customapi', 'dataprovider',
            ];
            const messages: PluginsPanelHostToWebview[] = formTypes.map(formType => ({
                command: 'showUpdateForm' as const,
                formType,
                id: 'entity-id',
                data: { name: 'Entity Name' },
            }));
            expect(messages).toHaveLength(8);
        });
    });

    describe('PluginTreeData', () => {
        it('has all required sections', () => {
            const data: PluginTreeData = {
                assemblies: [],
                packages: [],
                serviceEndpoints: [],
                customApis: [],
                dataSources: [],
            };
            expect(data.assemblies).toHaveLength(0);
            expect(data.packages).toHaveLength(0);
            expect(data.serviceEndpoints).toHaveLength(0);
            expect(data.customApis).toHaveLength(0);
            expect(data.dataSources).toHaveLength(0);
        });

        it('accepts populated tree data', () => {
            const data: PluginTreeData = {
                assemblies: [
                    {
                        id: 'assembly:MyAssembly',
                        name: 'MyAssembly (1.0.0.0)',
                        nodeType: 'assembly',
                        hasChildren: true,
                        children: [
                            {
                                id: 'type:MyPlugin',
                                name: 'MyPlugin',
                                nodeType: 'type',
                                hasChildren: true,
                                children: [
                                    {
                                        id: 'step:MyStep',
                                        name: 'Create Account',
                                        nodeType: 'step',
                                        isEnabled: true,
                                    },
                                    {
                                        id: 'step:DisabledStep',
                                        name: 'Update Account',
                                        nodeType: 'step',
                                        isEnabled: false,
                                        badge: 'Disabled',
                                    },
                                ],
                            },
                        ],
                    },
                ],
                packages: [],
                serviceEndpoints: [
                    {
                        id: 'serviceendpoint:ep-guid',
                        name: 'My Webhook',
                        nodeType: 'serviceEndpoint',
                        isManaged: false,
                        badge: 'Webhook',
                    },
                ],
                customApis: [
                    {
                        id: 'customapi:api-guid',
                        name: 'My Custom API',
                        nodeType: 'customApi',
                        isManaged: true,
                        badge: 'Function',
                        hasChildren: true,
                    },
                ],
                dataSources: [
                    {
                        id: 'datasource:ds-guid',
                        name: 'My Data Source',
                        nodeType: 'dataSource',
                        isManaged: false,
                        hasChildren: true,
                        children: [
                            {
                                id: 'dataprovider:dp-guid',
                                name: 'My Data Provider',
                                nodeType: 'dataProvider',
                                isManaged: false,
                            },
                        ],
                    },
                ],
            };
            expect(data.assemblies).toHaveLength(1);
            expect(data.assemblies[0].children![0].children).toHaveLength(2);
            expect(data.serviceEndpoints).toHaveLength(1);
            expect(data.customApis).toHaveLength(1);
            expect(data.dataSources).toHaveLength(1);
            expect(data.dataSources[0].children).toHaveLength(1);
        });
    });

    describe('PluginTreeNode', () => {
        it('has all required fields', () => {
            const node: PluginTreeNode = {
                id: 'assembly:MyAssembly',
                name: 'MyAssembly (1.0.0.0)',
                nodeType: 'assembly',
            };
            expect(node.id).toBe('assembly:MyAssembly');
            expect(node.name).toBe('MyAssembly (1.0.0.0)');
            expect(node.nodeType).toBe('assembly');
        });

        it('accepts all optional fields', () => {
            const node: PluginTreeNode = {
                id: 'step:step-guid',
                name: 'Create Account (Pre-Operation)',
                nodeType: 'step',
                icon: 'sync',
                badge: 'Disabled',
                isEnabled: false,
                isManaged: true,
                isHidden: false,
                hasChildren: false,
                children: [],
            };
            expect(node.badge).toBe('Disabled');
            expect(node.isEnabled).toBe(false);
            expect(node.isManaged).toBe(true);
            expect(node.isHidden).toBe(false);
            expect(node.hasChildren).toBe(false);
            expect(node.children).toHaveLength(0);
        });

        it('supports tree with nested assembly > type > step hierarchy', () => {
            const step: PluginTreeNode = {
                id: 'step:create-step',
                name: 'Create (Pre-Operation, depth 1)',
                nodeType: 'step',
                isEnabled: true,
            };
            const type: PluginTreeNode = {
                id: 'type:Contoso.Plugins.AccountCreate',
                name: 'Contoso.Plugins.AccountCreate',
                nodeType: 'type',
                hasChildren: true,
                children: [step],
            };
            const assembly: PluginTreeNode = {
                id: 'assembly:Contoso.Plugins',
                name: 'Contoso.Plugins (1.0.0.0)',
                nodeType: 'assembly',
                hasChildren: true,
                children: [type],
            };
            expect(assembly.children).toHaveLength(1);
            expect(assembly.children![0].children).toHaveLength(1);
            expect(assembly.children![0].children![0].isEnabled).toBe(true);
        });

        it('supports package > assembly > type hierarchy', () => {
            const pkg: PluginTreeNode = {
                id: 'package:MyPackage',
                name: 'MyPackage (1.0.0)',
                nodeType: 'package',
                hasChildren: true,
                children: [
                    {
                        id: 'assembly:MyAssembly',
                        name: 'MyAssembly',
                        nodeType: 'assembly',
                        hasChildren: false,
                        children: [],
                    },
                ],
            };
            expect(pkg.children).toHaveLength(1);
            expect(pkg.children![0].nodeType).toBe('assembly');
        });
    });

    describe('AttributeViewDto', () => {
        it('has all required fields', () => {
            const dto: AttributeViewDto = {
                logicalName: 'accountid',
                displayName: 'Account',
                attributeType: 'Uniqueidentifier',
            };
            expect(dto.logicalName).toBe('accountid');
            expect(dto.displayName).toBe('Account');
            expect(dto.attributeType).toBe('Uniqueidentifier');
        });

        it('accepts a list of attributes', () => {
            const attrs: AttributeViewDto[] = [
                { logicalName: 'name', displayName: 'Name', attributeType: 'String' },
                { logicalName: 'accountid', displayName: 'Account', attributeType: 'Uniqueidentifier' },
                { logicalName: 'revenue', displayName: 'Annual Revenue', attributeType: 'Money' },
                { logicalName: 'statecode', displayName: 'Status', attributeType: 'State' },
            ];
            expect(attrs).toHaveLength(4);
            expect(attrs[0].attributeType).toBe('String');
            expect(attrs[3].logicalName).toBe('statecode');
        });
    });
});
