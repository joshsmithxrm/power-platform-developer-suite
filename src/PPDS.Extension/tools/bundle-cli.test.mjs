import { describe, expect, it } from 'vitest';

import {
    extractInformationalVersion,
    verifyBundledCliVersion,
} from './bundle-cli-version.mjs';

const assemblyInfo = version =>
    `[assembly: System.Reflection.AssemblyInformationalVersionAttribute("${version}")]`;

describe('bundled CLI release version verification', () => {
    it('reads the MinVer version compiled into the CLI assembly', () => {
        expect(extractInformationalVersion(assemblyInfo('1.4.0'))).toBe('1.4.0');
    });

    it('accepts the intended Cli-v release version', () => {
        expect(verifyBundledCliVersion(assemblyInfo('1.4.0'), '1.4.0')).toBe('1.4.0');
    });

    it('accepts a stable version with generated build metadata', () => {
        expect(verifyBundledCliVersion(assemblyInfo('1.4.0+abc1234'), '1.4.0'))
            .toBe('1.4.0+abc1234');
    });

    it('accepts a matching prerelease version with generated build metadata', () => {
        expect(verifyBundledCliVersion(
            assemblyInfo('1.5.0-beta.2+abc1234'),
            '1.5.0-beta.2'
        )).toBe('1.5.0-beta.2+abc1234');
    });

    it('rejects a fallback version before the VSIX is packaged', () => {
        expect(() =>
            verifyBundledCliVersion(assemblyInfo('0.0.0-alpha.0.0'), '1.4.0')
        ).toThrow('Bundled CLI version mismatch: expected 1.4.0, MinVer produced 0.0.0-alpha.0.0');
    });

    it.each([
        ['1.4.1+abc1234', '1.4.0'],
        ['1.5.0-beta.2+abc1234', '1.5.0-beta.1'],
        ['1.5.0+abc1234', '1.5.0-beta.1'],
    ])('rejects release or prerelease mismatches (%s vs %s)', (actual, expected) => {
        expect(() => verifyBundledCliVersion(assemblyInfo(actual), expected))
            .toThrow(`Bundled CLI version mismatch: expected ${expected}, MinVer produced ${actual}`);
    });
});
