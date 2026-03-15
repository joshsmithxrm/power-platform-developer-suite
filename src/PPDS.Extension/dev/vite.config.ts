import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig({
    root: __dirname,
    server: {
        port: 5173,
        open: false,
        fs: {
            // Allow serving files from the parent extension/ directory (for node_modules)
            allow: [path.resolve(__dirname, '..')],
        },
    },
});
