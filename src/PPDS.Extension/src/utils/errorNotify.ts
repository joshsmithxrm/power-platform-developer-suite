import * as vscode from 'vscode';

/**
 * URL opened when the user clicks the "Report Issue" action on an error notification.
 * Points at the new-issue form so the user lands on the pre-filled template.
 */
export const REPORT_ISSUE_URL = 'https://github.com/joshsmithxrm/power-platform-developer-suite/issues/new';

/**
 * Label for the "Report Issue" action button on error notifications.
 * Matches the PPDS v1 bug-reporting copy standard (see v1-release-plan.md Workstream I).
 */
export const REPORT_ISSUE_ACTION = 'Report Issue';

/**
 * Show an error notification with a built-in "Report Issue" action.
 *
 * When the user clicks the action, the GitHub new-issue page is opened in the
 * default browser via `vscode.env.openExternal`. When the user dismisses the
 * notification or picks any other action, the promise resolves to that action.
 *
 * Centralising the pattern keeps the action label and issue URL consistent
 * across every `showErrorMessage` call site in the extension. Call sites that
 * need additional custom actions (e.g., Re-authenticate prompts with multiple
 * choices) should NOT use this helper — those flows have their own semantics.
 *
 * @param message The error message to display.
 * @returns The label of the action the user picked, or `undefined` if they
 *   dismissed the notification. Callers that branch on user choice can await
 *   this promise.
 */
export async function showErrorWithReport(message: string): Promise<string | undefined> {
    const choice = await vscode.window.showErrorMessage(
        message,
        { title: REPORT_ISSUE_ACTION },
    );

    if (choice?.title === REPORT_ISSUE_ACTION) {
        await vscode.env.openExternal(vscode.Uri.parse(REPORT_ISSUE_URL));
        return REPORT_ISSUE_ACTION;
    }

    return choice?.title;
}
