import { describe, expect, it } from 'vitest';
import { pluginFormFields, pluginRpcFields, pluginImageChecks, pluginStepConfiguration } from '../../panels/webview/shared/plugin-form-fields.js';

describe('Plugin form partial intent (AC-86)', () => {
    it('omits untouched optional values without resetting existing settings', () => {
        expect(pluginFormFields('step', {
            description: '', filteringAttributes: 'name', unsecureConfiguration: '',
            asyncAutoDelete: false, canBeBypassed: true, canUseReadOnlyConnection: false,
        }, new Set(), true)).toEqual({});
    });

    it('expresses deliberate text clears separately from supplied values', () => {
        expect(pluginFormFields('step', {
            description: '', filteringAttributes: '', unsecureConfiguration: '  exact payload  ',
        }, new Set(['description', 'filteringAttributes', 'unsecureConfiguration']), true)).toEqual({
            unsecureConfiguration: '  exact payload  ', resetFields: ['description', 'filteringAttributes'],
        });
    });

    it('retains an explicitly unchecked boolean and preserves secure omission', () => {
        expect(pluginFormFields('step', { asyncAutoDelete: false, secureConfiguration: '' },
            new Set(['asyncAutoDelete']), true)).toEqual({ asyncAutoDelete: false });
    });

    it('allows intentional all-attributes reset without a conflicting value', () => {
        expect(pluginFormFields('image', { attributes: '', description: '' },
            new Set(['attributes']), true)).toEqual({ resetFields: ['attributes'] });
    });

    it('preserves untouched image overrides on partial updates', () => {
        expect(pluginFormFields('image', { entityAlias: 'Pre', messagePropertyName: 'Target' },
            new Set(), true)).toEqual({});
    });

    it('hydrates unsecure configuration from the actual detail DTO without trimming', () => {
        expect(pluginStepConfiguration({ configuration: '  exact config  ' })).toBe('  exact config  ');
        expect(pluginStepConfiguration({ unsecureConfiguration: 'legacy' })).toBe('legacy');
        expect(pluginStepConfiguration({ configuration: '' })).toBe('');
    });

    it('does not turn blank new-form placeholders into resets', () => {
        expect(pluginFormFields('image', { attributes: '', description: '', messagePropertyName: '' },
            new Set(), false)).toEqual({});
    });

    it('maps actual step registration parent and entity parameters', () => {
        expect(pluginRpcFields('step', { primaryEntity: 'account', name: '', executionOrder: 2 },
            'type:11111111-1111-1111-1111-111111111111')).toEqual({
            eventHandlerId: '11111111-1111-1111-1111-111111111111', entity: 'account', executionOrder: 2,
        });
    });

    it('maps actual update parameter names and preserves reset intent', () => {
        expect(pluginRpcFields('step', { executionOrder: 2, resetFields: ['description'] }, undefined, true))
            .toEqual({ rank: 2, resetFields: ['description'] });
        expect(pluginRpcFields('image', { attributes: 'name', imageTypePre: true, imageTypePost: true }, undefined, true))
            .toEqual({ imageAttributes: 'name', imageType: 'Both' });
    });

    it('does not send unsupported step identity or secret fields to strict named RPC matching', () => {
        expect(pluginRpcFields('step', { message: 'Update', primaryEntity: 'account', secondaryEntity: '',
            secureConfiguration: 'do-not-send', name: 'new name' }, undefined, true)).toEqual({ name: 'new name' });
    });

    it.each([
        ['PreImage', { pre: true, post: false }],
        ['PostImage', { pre: false, post: true }],
        ['Both', { pre: true, post: true }],
    ])('hydrates image checkboxes from the actual %s DTO', (type, expected) => {
        expect(pluginImageChecks(type)).toEqual(expected);
    });

    it('maps image parent and type and never invents a secure reset', () => {
        expect(pluginRpcFields('image', { name: 'Pre', imageTypePre: true, imageTypePost: false,
            resetFields: ['attributes'] }, 'step:step-guid')).toEqual({
            name: 'Pre', stepId: 'step-guid', imageType: 'PreImage', resetFields: ['attributes'],
        });
    });
});
