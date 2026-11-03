/** Translate visible form edits into explicit partial-update intent. */
export function pluginFormFields(
    kind: 'step' | 'image',
    fields: Record<string, unknown>,
    changed: ReadonlySet<string>,
    isUpdate: boolean,
): Record<string, unknown> {
    const result = { ...fields };
    const resets: string[] = [];
    const optional = kind === 'step'
        ? ['description', 'filteringAttributes', 'unsecureConfiguration']
        : ['attributes', 'description'];
    for (const key of optional) {
        if (isUpdate && !changed.has(key)) {
            delete result[key];
        } else if (result[key] === '') {
            delete result[key];
            if (changed.has(key)) resets.push(key);
        }
    }
    if (isUpdate && kind === 'step') {
        for (const key of ['asyncAutoDelete', 'canBeBypassed', 'canUseReadOnlyConnection']) {
            if (!changed.has(key)) delete result[key];
        }
    }
    if (isUpdate && kind === 'image') {
        for (const key of ['entityAlias', 'messagePropertyName']) {
            if (!changed.has(key)) delete result[key];
        }
    }
    // A blank override requests no override; it never authorizes secret removal.
    if (result['messagePropertyName'] === '') delete result['messagePropertyName'];
    if (result['secureConfiguration'] === '') delete result['secureConfiguration'];
    if (resets.length > 0) result['resetFields'] = resets;
    return result;
}

/** Adapt webview field names to the daemon's named RPC parameters. */
export function pluginRpcFields(
    kind: 'step' | 'image',
    fields: Record<string, unknown>,
    parentId?: string,
    isUpdate = false,
): Record<string, unknown> {
    const result = { ...fields };
    if (!isUpdate && parentId) {
        const id = parentId.replace(/^(?:type|step):/, '');
        result[kind === 'step' ? 'eventHandlerId' : 'stepId'] = id;
    }
    if (kind === 'step') {
        if ('primaryEntity' in result) {
            result['entity'] = result['primaryEntity'];
            delete result['primaryEntity'];
        }
        if (isUpdate && 'executionOrder' in result) {
            result['rank'] = result['executionOrder'];
            delete result['executionOrder'];
        }
        if (isUpdate) {
            // Identity changes and secure values are not accepted by updateStep.
            for (const key of ['message', 'entity', 'secondaryEntity', 'secureConfiguration']) delete result[key];
        }
        if (!isUpdate && result['name'] === '') delete result['name'];
    } else {
        if ('imageTypePre' in result || 'imageTypePost' in result) {
            result['imageType'] = result['imageTypePre'] && result['imageTypePost']
                ? 'Both' : result['imageTypePre'] ? 'PreImage' : 'PostImage';
            delete result['imageTypePre'];
            delete result['imageTypePost'];
        }
        if (isUpdate && 'attributes' in result) {
            result['imageAttributes'] = result['attributes'];
            delete result['attributes'];
        }
    }
    return result;
}

/** The get-image DTO reports one canonical imageType, not checkbox booleans. */
export function pluginImageChecks(imageType: unknown): { pre: boolean; post: boolean } {
    const type = String(imageType ?? '').toLowerCase();
    return { pre: type === 'preimage' || type === 'both', post: type === 'postimage' || type === 'both' };
}

/** Step detail responses use configuration; retain the older form key as fallback. */
export function pluginStepConfiguration(step?: Record<string, unknown>): string {
    const value = step?.['configuration'] ?? step?.['unsecureConfiguration'];
    return typeof value === 'string' ? value : '';
}
