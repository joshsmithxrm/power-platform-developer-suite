/**
 * Monaco Editor entry point for webview bundles.
 * This file is the esbuild entry for the browser-targeted Monaco bundle.
 * It configures the Monaco environment (workers) and re-exports monaco.
 */
import * as monaco from 'monaco-editor';

// Configure Monaco to use inline workers (blob URLs)
(self as any).MonacoEnvironment = {
    getWorker(_moduleId: string, _label: string) {
        const workerUrl = (self as any).__MONACO_WORKER_URL__;
        if (workerUrl) {
            return new Worker(workerUrl);
        }
        // Fallback: create a minimal no-op worker
        const blob = new Blob(
            ['self.onmessage = function() {}'],
            { type: 'application/javascript' },
        );
        return new Worker(URL.createObjectURL(blob));
    },
};

// Expose monaco globally for the webview IIFE to use
(window as any).monaco = monaco;
