/**
 * Monaco Editor entry point — selective language bundling.
 * Only SQL and XML languages are included to reduce bundle size.
 */

// Core editor API
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';

// Language contributions — only SQL and XML
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/xml/xml.contribution.js';

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
