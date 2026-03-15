/**
 * Type-safe wrapper around the VS Code webview API.
 */

interface VsCodeApi<TOutgoing> {
    postMessage(message: TOutgoing): void;
    getState(): unknown;
    setState(state: unknown): void;
}

declare function acquireVsCodeApi<T>(): VsCodeApi<T>;

/** Acquire the VS Code webview API with typed message sending. */
export function getVsCodeApi<TOutgoing extends { command: string }>(): VsCodeApi<TOutgoing> {
    return acquireVsCodeApi<TOutgoing>();
}
