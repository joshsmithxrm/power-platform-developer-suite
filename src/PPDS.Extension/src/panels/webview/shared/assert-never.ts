/**
 * Exhaustive switch helper. If this function is reachable, TypeScript
 * will report a compile error — meaning a case was not handled.
 */
export function assertNever(value: never): never {
    throw new Error(`Unhandled discriminated union member: ${JSON.stringify(value)}`);
}
