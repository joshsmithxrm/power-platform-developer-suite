export function extractInformationalVersion(assemblyInfo) {
    const match = assemblyInfo.match(/AssemblyInformationalVersionAttribute\("([^"]+)"\)/);
    if (!match) {
        throw new Error('Generated CLI assembly metadata does not contain an informational version');
    }
    return match[1];
}

export function verifyBundledCliVersion(assemblyInfo, expectedVersion) {
    const actualVersion = extractInformationalVersion(assemblyInfo);
    const actualReleaseVersion = actualVersion.split('+', 1)[0];
    const expectedReleaseVersion = expectedVersion.split('+', 1)[0];
    if (actualReleaseVersion !== expectedReleaseVersion) {
        throw new Error(
            `Bundled CLI version mismatch: expected ${expectedVersion}, MinVer produced ${actualVersion}`
        );
    }
    return actualVersion;
}
