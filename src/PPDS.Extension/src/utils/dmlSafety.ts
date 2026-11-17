import { ResponseError } from 'vscode-jsonrpc/node';
import * as vscode from 'vscode';

import { ExplainDocumentProvider } from '../providers/explainDocumentProvider.js';
import type { QueryResultResponse } from '../types.js';

/**
 * Shared DML safety flow for every surface that sends user SQL to `query/sql`
 * (Data Explorer panel and notebook cells).
 *
 * The extension never decides by itself whether a statement is DML. It always sends
 * `dmlSafety: { isConfirmed: false }` first; the daemon's DmlSafetyGuard parses the
 * statement and either runs it (read-only), refuses it (`Query.DmlBlocked`), or answers
 * `Query.DmlConfirmationRequired`. Only that answer leads to the modal below, and only
 * an explicit "Execute Anyway" leads to one request with `isConfirmed: true`, for the
 * same statement and the same captured target. A confirmation is never reused: any
 * retry (re-authentication, refresh, re-run) starts again from the unconfirmed attempt.
 */

/** Destructive, never-default button of every DML confirmation (Data Explorer wording). */
export const DML_CONFIRM_ACTION = 'Execute Anyway';
/** First (and therefore default) button: opens the exact text read-only, then asks again. */
export const DML_SHOW_ACTION = 'Show Full Statement';
/** Non-modal step after the full statement is open, so it can be read before the modal returns. */
export const DML_CONTINUE_ACTION = 'Continue to Confirmation';

/** Safety options for a request the user has not confirmed. Sent on every first attempt and on every load-more. */
export const UNCONFIRMED_DML_SAFETY = Object.freeze({ isConfirmed: false });

const CONFIRMATION_REQUIRED_CODE = 'Query.DmlConfirmationRequired';
/**
 * Error codes the daemon uses when a safety guard refuses a statement. `Query.DmlRowCapExceeded`
 * is handled for daemons that raise it; the daemon on main stops at the cap without an error.
 */
const REFUSAL_CODES = new Set(['Query.DmlBlocked', 'Query.DmlRowCapExceeded', 'Safety.ShakedownActive']);

/** Statements within both limits are shown whole in the modal. */
const MAX_PREVIEW_LINES = 24;
const MAX_PREVIEW_CHARS = 1500;
/** Longer statements show this much of the start and of the end. */
const HEAD_LINES = 14;
const HEAD_CHARS = 900;
const TAIL_LINES = 8;
const TAIL_CHARS = 500;

interface DmlErrorData {
    code?: string;
    message?: string;
    dmlBlocked?: boolean;
    dmlConfirmationRequired?: boolean;
}

export interface DmlTarget {
    readonly environmentName?: string | null;
    readonly environmentUrl?: string | null;
    readonly environmentType?: string | null;
    readonly profileName?: string | null;
}

export interface DmlConfirmationOptions {
    /** The daemon's confirmation text; shown only when it is not the CLI-only "--confirm" wording. */
    readonly serverMessage?: string;
    /** Aborting (interrupt, cancel, close) resolves the confirmation as declined. */
    readonly signal?: AbortSignal;
}

export interface StatementPreview {
    readonly text: string;
    readonly truncated: boolean;
    readonly totalLines: number;
    readonly totalChars: number;
    readonly hiddenChars: number;
    readonly hiddenLines: number;
}

function dmlErrorData(error: unknown): DmlErrorData | undefined {
    if (!(error instanceof ResponseError)) return undefined;
    const data: unknown = error.data;
    if (!data || typeof data !== 'object') return undefined;
    return data;
}

/** True when the daemon refused to run the statement until the user confirms it. */
export function isDmlConfirmationRequired(error: unknown): boolean {
    const data = dmlErrorData(error);
    return data?.dmlConfirmationRequired === true || data?.code === CONFIRMATION_REQUIRED_CODE;
}

/** The daemon's message on a DML safety error, if any. */
export function dmlServerMessage(error: unknown): string | undefined {
    const message = dmlErrorData(error)?.message;
    return typeof message === 'string' && message.trim() ? message.trim() : undefined;
}

/**
 * When the daemon's safety guard refused the statement (no WHERE, protected or
 * cross-environment target, shakedown, row cap), returns a user-facing message; otherwise undefined.
 */
export function dmlRefusalMessage(error: unknown): string | undefined {
    const data = dmlErrorData(error);
    if (!data || !(data.dmlBlocked === true || (data.code !== undefined && REFUSAL_CODES.has(data.code)))) return undefined;
    const reason = data.message || (error instanceof Error ? error.message : '') || 'DML operation blocked.';
    return `Not executed: the DML safety guard refused this statement.\n${reason}`;
}

/** The modal text for a statement: whole, or its start and end with what is hidden counted. */
export function previewStatement(statement: string): StatementPreview {
    const lines = statement.split(/\r?\n/);
    const totalChars = statement.length;
    const base = { totalLines: lines.length, totalChars };
    if (lines.length <= MAX_PREVIEW_LINES && totalChars <= MAX_PREVIEW_CHARS) {
        return { ...base, text: statement, truncated: false, hiddenChars: 0, hiddenLines: 0 };
    }
    const head = lines.slice(0, HEAD_LINES).join('\n').slice(0, HEAD_CHARS);
    const tailLines = lines.slice(-TAIL_LINES).join('\n');
    const tail = tailLines.slice(Math.max(0, tailLines.length - TAIL_CHARS));
    // Offsets in the normalised text, so the hidden part is exactly what lies between head and tail.
    const normalised = lines.join('\n');
    const hidden = normalised.slice(head.length, normalised.length - tail.length);
    const hiddenLines = hidden.split('\n').length;
    return {
        ...base, truncated: true, hiddenChars: hidden.length, hiddenLines,
        text: `${head}\n… ${hidden.length} character(s) on ${hiddenLines} line(s) not shown …\n${tail}`,
    };
}

function raceAbort<T>(promise: Thenable<T>, signal: AbortSignal | undefined): Promise<T | undefined> {
    if (!signal) return Promise.resolve(promise);
    if (signal.aborted) return Promise.resolve(undefined);
    return new Promise<T | undefined>(resolve => {
        const onAbort = (): void => resolve(undefined);
        signal.addEventListener('abort', onAbort, { once: true });
        Promise.resolve(promise).then(value => {
            signal.removeEventListener('abort', onAbort);
            resolve(value);
        }, () => {
            signal.removeEventListener('abort', onAbort);
            resolve(undefined);
        });
    });
}

/** Opens the exact text that will run in a read-only editor. */
async function showFullStatement(statement: string): Promise<void> {
    if (ExplainDocumentProvider.instance) {
        await ExplainDocumentProvider.instance.show(statement, 'sql', 'DML to confirm');
        return;
    }
    const doc = await vscode.workspace.openTextDocument({ content: statement, language: 'sql' });
    await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside, preview: true });
}

function targetLabel(target: DmlTarget): { name: string; type: string } {
    return {
        name: target.environmentName || target.environmentUrl || 'the selected environment',
        type: target.environmentType || 'type unknown',
    };
}

function confirmationDetail(preview: StatementPreview, target: DmlTarget, mustReview: boolean, serverMessage?: string): string {
    const { name, type } = targetLabel(target);
    const lines = [
        `Environment: ${name} (${type})`,
        `URL: ${target.environmentUrl || 'not available'}`,
        ...(target.profileName ? [`Profile: ${target.profileName}`] : []),
        'Other environments: SQL that names another environment (for example [LABEL].account) also writes there. '
            + 'The daemon does not report those targets before execution, so they are not listed here.',
        ...(serverMessage && !serverMessage.includes('--confirm') ? [`Daemon: ${serverMessage}`] : []),
        '',
        `Statement: ${preview.totalLines} line(s), ${preview.totalChars} character(s). This confirmation covers all of it, every statement in the script.`,
        preview.text,
        '',
    ];
    if (preview.truncated) {
        lines.push(`Only the start and the end are shown here: ${preview.hiddenChars} character(s) on ${preview.hiddenLines} line(s) are hidden.`);
    }
    lines.push(mustReview
        ? `${DML_CONFIRM_ACTION} is offered after you have opened the full statement with ${DML_SHOW_ACTION}.`
        : `Nothing runs unless you choose ${DML_CONFIRM_ACTION}. ${DML_SHOW_ACTION} (the default) opens the exact text read-only.`);
    return lines.join('\n');
}

/**
 * Modal confirmation for one DML execution of exactly `statement` on exactly `target`.
 *
 * The default (first) button is the non-destructive "Show Full Statement". When the statement
 * is longer than the modal shows, "Execute Anyway" is offered only after the full text has been
 * opened. Returns true only for an explicit "Execute Anyway"; Escape, close, Cancel, a failed
 * dialog or an abort return false. Never remembered.
 */
export async function confirmDmlExecution(statement: string, target: DmlTarget, options: DmlConfirmationOptions = {}): Promise<boolean> {
    const preview = previewStatement(statement);
    const { name, type } = targetLabel(target);
    let reviewed = false;
    for (;;) {
        if (options.signal?.aborted) return false;
        const mustReview = preview.truncated && !reviewed;
        const title = mustReview
            ? `Review the full SQL before running it on ${name} (${type})`
            : `Run SQL that changes data on ${name} (${type})?`;
        const buttons = mustReview ? [DML_SHOW_ACTION] : [DML_SHOW_ACTION, DML_CONFIRM_ACTION];
        const choice = await raceAbort(vscode.window.showWarningMessage(
            title, { modal: true, detail: confirmationDetail(preview, target, mustReview, options.serverMessage) }, ...buttons,
        ), options.signal);
        if (options.signal?.aborted) return false;
        if (choice === DML_CONFIRM_ACTION) return !mustReview;
        if (choice !== DML_SHOW_ACTION) return false;
        try {
            await showFullStatement(statement);
        } catch {
            return false;
        }
        const next = await raceAbort(vscode.window.showWarningMessage(
            `Review the full SQL for ${name} in the editor, then choose ${DML_CONTINUE_ACTION}. Nothing has run.`,
            DML_CONTINUE_ACTION, 'Cancel',
        ), options.signal);
        if (next !== DML_CONTINUE_ACTION) return false;
        reviewed = true;
    }
}

function numeric(value: unknown): number | undefined {
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (value && typeof value === 'object' && 'value' in value) return numeric(value.value);
    if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value);
    return undefined;
}

export interface DmlFailureContext {
    /**
     * True only for the result of a request this code sent with `isConfirmed: true`. Only such a
     * request can have run DML, so only its result may be read as the legacy one-row DML summary.
     */
    readonly confirmedDml: boolean;
}

/**
 * Describes a DML execution in which rows failed, or returns undefined.
 *
 * Reads the additive response fields (`success: false`, `affectedRows`, `failedRows`,
 * `dmlError`), sent only by daemons that report DML outcomes. For a confirmed DML request it
 * also reads the one-row summary (`affected_rows`, `failed_rows`) every daemon returns; that
 * form carries no reason.
 */
export function describeDmlFailure(result: QueryResultResponse, context: DmlFailureContext): string | undefined {
    const counts = dmlCounts(result, context.confirmedDml);
    const reason = typeof result.dmlError === 'string' && result.dmlError.trim() ? result.dmlError.trim() : undefined;
    const reportedFailure = result.success === false && (reason !== undefined || counts.failed !== undefined);
    const rowsFailed = counts.failed !== undefined && counts.failed > 0;
    if (!reportedFailure && !rowsFailed) return undefined;
    const parts = [
        counts.failed !== undefined ? `${counts.failed} row(s) failed` : undefined,
        counts.affected !== undefined ? `${counts.affected} row(s) succeeded` : undefined,
    ].filter(Boolean).join(', ');
    return `DML failed${parts ? `: ${parts}` : ''}.\n${reason ?? 'The daemon did not report a failure reason.'}`;
}

/** Counts from the additive fields, else (confirmed DML only) from the one-row DML summary. */
function dmlCounts(result: QueryResultResponse, confirmedDml: boolean): { affected?: number; failed?: number } {
    const failed = numeric(result.failedRows);
    if (failed !== undefined) return { affected: numeric(result.affectedRows), failed };
    if (!confirmedDml || result.records.length !== 1) return { affected: numeric(result.affectedRows) };
    const row = result.records[0] ?? {};
    const keys = Object.keys(row);
    if (keys.length !== 2 || !keys.includes('affected_rows') || !keys.includes('failed_rows')) {
        return { affected: numeric(result.affectedRows) };
    }
    return { affected: numeric(row.affected_rows), failed: numeric(row.failed_rows) };
}
