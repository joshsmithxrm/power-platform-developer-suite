export function extractInformationalVersion(assemblyInfo) {
    const match = assemblyInfo.match(/AssemblyInformationalVersionAttribute\("([^"]+)"\)/);
    if (!match) {
        throw new Error('Generated CLI assembly metadata does not contain an informational version');
    }
    return match[1];
}

export function verifyBundledCliVersion(assemblyInfo, expectedVersion) {
    const actualVersion = extractInformationalVersion(assemblyInfo);
    if (actualVersion !== expectedVersion) {
        throw new Error(
            `Bundled CLI version mismatch: expected ${expectedVersion}, MinVer produced ${actualVersion}`
        );
    }
    return actualVersion;
}
