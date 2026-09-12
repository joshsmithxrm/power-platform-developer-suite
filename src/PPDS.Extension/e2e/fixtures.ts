import { _electron as electron, expect, test as base, type ElectronApplication, type Page } from '@playwright/test';
import { downloadAndUnzipVSCode } from '@vscode/test-electron';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
    buildIsolatedProcessEnvironment,
    buildVsCodeLaunchArgs,
    readDeclaredVsCodeEngine,
    resolveVsCodeVersion,
    startupAttempts,
} from './config.js';

const WORKBENCH_TIMEOUT_MS = 60_000;

interface VsCodeSession {
    app: ElectronApplication;
    page: Page;
    temporaryRoot: string;
}

interface TestFixtures {
    captureFailureScreenshot: void;
    page: Page;
    vscodeApp: ElectronApplication;
}

interface WorkerFixtures {
    vscodeSession: VsCodeSession;
}

class WorkbenchStartupError extends Error {
    public constructor(cause: unknown) {
        super('VS Code opened but its workbench did not become ready in time', { cause });
        this.name = 'WorkbenchStartupError';
    }
}

function writeIsolatedUserSettings(userDataDir: string): void {
    const userDirectory = path.join(userDataDir, 'User');
    fs.mkdirSync(userDirectory, { recursive: true });
    fs.writeFileSync(path.join(userDirectory, 'settings.json'), JSON.stringify({
        'chat.commandCenter.enabled': false,
        'github.copilot.enable': { '*': false },
        'ppds.autoStartDaemon': false,
        'security.workspace.trust.enabled': false,
        'telemetry.telemetryLevel': 'off',
        'workbench.secondarySideBar.defaultVisibility': 'hidden',
        'workbench.startupEditor': 'none',
    }, null, 2));
}

async function closeQuietly(app: ElectronApplication): Promise<void> {
    try {
        await app.close();
    } catch {
        // The process may already have exited after a failed startup.
    }
}

async function launchVsCodeSession(
    executablePath: string,
    extensionPath: string,
    attempt: number,
): Promise<VsCodeSession> {
    const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), `ppds-e2e-${attempt}-`));
    const userDataDir = path.join(temporaryRoot, 'user-data');
    const extensionsDir = path.join(temporaryRoot, 'extensions');
    const ppdsConfigDir = path.join(temporaryRoot, 'ppds-config');
    const workspaceDir = path.join(temporaryRoot, 'workspace');
    fs.mkdirSync(extensionsDir, { recursive: true });
    fs.mkdirSync(workspaceDir, { recursive: true });
    writeIsolatedUserSettings(userDataDir);

    const app = await electron.launch({
        executablePath,
        args: buildVsCodeLaunchArgs({ extensionPath, extensionsDir, userDataDir, workspaceDir }),
        env: buildIsolatedProcessEnvironment(process.env, ppdsConfigDir),
    });

    try {
        const page = await app.firstWindow({ timeout: WORKBENCH_TIMEOUT_MS });
        await page.locator('#workbench\\.parts\\.editor').waitFor({
            state: 'visible',
            timeout: WORKBENCH_TIMEOUT_MS,
        });
        return { app, page, temporaryRoot };
    } catch (error) {
        await closeQuietly(app);
        fs.rmSync(temporaryRoot, { recursive: true, force: true });
        throw new WorkbenchStartupError(error);
    }
}

export const test = base.extend<TestFixtures, WorkerFixtures>({
    vscodeSession: [async ({}, use) => {
        const extensionPath = path.resolve(__dirname, '..');
        const manifestPath = path.join(extensionPath, 'package.json');
        const engineRange = readDeclaredVsCodeEngine(manifestPath);
        const vscodeVersion = resolveVsCodeVersion(process.env.VSCODE_E2E_VERSION, engineRange);

        console.log(`Launching VS Code ${vscodeVersion} for Extension E2E tests`);
        const executablePath = await downloadAndUnzipVSCode(vscodeVersion);
        const attempts = startupAttempts(process.env.CI);
        let session: VsCodeSession | undefined;

        for (let attempt = 1; attempt <= attempts; attempt += 1) {
            try {
                session = await launchVsCodeSession(executablePath, extensionPath, attempt);
                break;
            } catch (error) {
                if (!(error instanceof WorkbenchStartupError) || attempt === attempts) {
                    throw error;
                }
                console.warn(`VS Code workbench startup attempt ${attempt} failed; retrying once.`);
            }
        }

        if (!session) {
            throw new Error('VS Code E2E session was not created');
        }

        try {
            await use(session);
        } finally {
            await closeQuietly(session.app);
            try {
                fs.rmSync(session.temporaryRoot, { recursive: true, force: true });
            } catch {
                // VS Code may briefly retain file handles on Windows.
            }
        }
    }, { scope: 'worker', timeout: 180_000 }],

    vscodeApp: async ({ vscodeSession }, use) => {
        await use(vscodeSession.app);
    },

    page: async ({ vscodeSession }, use) => {
        await use(vscodeSession.page);
    },

    captureFailureScreenshot: [async ({ page }, use, testInfo) => {
        await use();
        if (testInfo.status === testInfo.expectedStatus) {
            return;
        }

        try {
            const screenshotPath = testInfo.outputPath('failure.png');
            await page.screenshot({ path: screenshotPath, fullPage: true });
            await testInfo.attach('failure screenshot', {
                path: screenshotPath,
                contentType: 'image/png',
            });
        } catch {
            // The window may be unavailable when VS Code itself crashes.
        }
    }, { auto: true }],
});

export { expect };
