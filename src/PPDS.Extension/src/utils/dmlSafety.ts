import { CancellationTokenSource, ResponseError } from 'vscode-jsonrpc/node';
import * as vscode from 'vscode';

import { ExplainDocumentProvider } from '../providers/explainDocumentProvider.js';
import type { DmlSafetyErrorData, DmlWriteTarget, QueryResultResponse } from '../types.js';

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
 *
 * Dialog buttons: the VS Code API documents that a modal message is modal and resolves to
 * the chosen item, or undefined when dismissed; it does not document which button is the
 * default. "Show Full Statement" is listed first because current VS Code makes the first
 * action the primary one. The rules that do not depend on that: a statement longer than the
 * dialog shows cannot be executed before it has been opened, and Escape or close never executes.
 */

/** Destructive button of every DML confirmation (Data Explorer wording); never listed first. */
export const DML_CONFIRM_ACTION = 'Execute Anyway';
/** Listed first: opens the exact text in an editor, then asks again. */
export const DML_SHOW_ACTION = 'Show Full Statement';
/** Review step after the full statement is open; a quick pick that stays until answered. */
export const DML_CONTINUE_ACTION = 'Continue to Confirmation';
const DML_REVIEW_CANCEL = 'Cancel';

/** Safety options for a request the user has not confirmed. Sent on every first attempt and on every load-more. */
export const UNCONFIRMED_DML_SAFETY = Object.freeze({ isConfirmed: false });

const CONFIRMATION_REQUIRED_CODE = 'Query.DmlConfirmationRequired';
/**
 * Error codes the daemon uses when a safety guard refuses a statement. `Query.DmlRowCapExceeded`
 * is handled for daemons that raise it; the daemon on main stops at the cap without an error.
 */
const REFUSAL_CODES = new Set([
    'Query.DmlBlocked',
    'Query.DmlUnsupportedConstruct',
    'Query.DmlValueStoredDifferently',
    'Query.DmlMetadataUnavailable',
    'Query.DmlRowCapExceeded',
    'Safety.ShakedownActive',
]);

/** Statements within both limits are shown whole in the modal. */
const MAX_PREVIEW_LINES = 24;
const MAX_PREVIEW_CHARS = 1500;
/** Longer statements show this much of the start and of the end. */
const HEAD_LINES = 14;
const HEAD_CHARS = 900;
const TAIL_LINES = 8;
const TAIL_CHARS = 500;

/**
 * Control, zero-width, bidirectional formatting and line/paragraph separator characters, made
 * visible in dialogs. Written with escapes only so the class can be read.
 */
const HIDDEN_IN_TEXT = /[\u0000-\u0008\u000B-\u001F\u007F-\u009F\u061C\u200B-\u200F\u2028\u2029\u202A-\u202E\u2060-\u2069\uFEFF]/g;
const HIDDEN_IN_LINE = /[\u0000-\u001F\u007F-\u009F\u061C\u200B-\u200F\u2028\u2029\u202A-\u202E\u2060-\u2069\uFEFF]/g;

export interface DmlTarget {
    readonly environmentName?: string | null;
    readonly environmentUrl?: string | null;
    readonly environmentType?: string | null;
    readonly profileName?: string | null;
}

export interface DmlConfirmationOptions {
    /** The daemon's confirmation text; shown only when it is not the CLI-only "--confirm" wording. */
    readonly serverMessage?: string;
    /** Every environment the request writes to, from daemons that send `dmlTargets`; `unreadable` if sent but malformed. */
    readonly writeTargets?: readonly DmlWriteTarget[] | typeof DML_TARGETS_UNREADABLE;
    /** Shown first in the dialog, for example after an earlier attempt may have partly applied. */
    readonly notice?: string;
    /** Aborting (interrupt, cancel, close) resolves the confirmation as aborted and closes the review prompt. */
    readonly signal?: AbortSignal;
}

/** Result of one confirmation. Only `confirmed` may lead to a confirmed request. */
export type DmlConfirmation =
    | { readonly kind: 'confirmed' }
    | { readonly kind: 'declined'; readonly message: string }
    | { readonly kind: 'failed'; readonly message: string }
    | { readonly kind: 'aborted' };

export interface StatementPreview {
    readonly text: string;
    readonly truncated: boolean;
    /** Lines of the statement (CRLF, LF and CR each end one line). */
    readonly totalLines: number;
    /** Characters of the statement with each line break counted as one character. */
    readonly totalChars: number;
    /** Characters not shown, excluding the line breaks that separate them from the shown parts. */
    readonly hiddenChars: number;
    /** Lines with at least one character not shown. */
    readonly hiddenLines: number;
    /** True when U+2028/U+2029 ended a line; the display marks each one. */
    readonly hasSeparators: boolean;
}

function dmlErrorData(error: unknown): DmlSafetyErrorData | undefined {
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

const optionalString = (value: unknown): string | undefined => typeof value === 'string' && value.trim() ? value.trim() : undefined;

/** `dmlTargets` was sent but is empty or malformed: treated as not listed, and said so. */
export const DML_TARGETS_UNREADABLE = 'unreadable';

/**
 * Environments the request writes to, read defensively from `dmlTargets` (daemons that report
 * them). Undefined when the daemon does not send the field; `unreadable` when it is sent but
 * empty or any entry is malformed (no entry is then trusted).
 */
export function dmlWriteTargets(error: unknown): DmlWriteTarget[] | typeof DML_TARGETS_UNREADABLE | undefined {
    const data = dmlErrorData(error);
    if (!data || !('dmlTargets' in data) || data.dmlTargets === undefined || data.dmlTargets === null) return undefined;
    const raw: unknown = data.dmlTargets;
    if (!Array.isArray(raw) || raw.length === 0) return DML_TARGETS_UNREADABLE;
    const targets: DmlWriteTarget[] = [];
    for (const item of raw as unknown[]) {
        if (!item || typeof item !== 'object') return DML_TARGETS_UNREADABLE;
        const entry = item as Record<string, unknown>;
        const target = { label: optionalString(entry.label), url: optionalString(entry.url), type: optionalString(entry.type), protection: optionalString(entry.protection) };
        if (!target.label && !target.url) return DML_TARGETS_UNREADABLE;
        targets.push(target);
    }
    return targets;
}

/** Codes the daemon returns before it executes anything; a confirmed request answered with them changed nothing. */
const REJECTED_BEFORE_EXECUTION = new Set([
    CONFIRMATION_REQUIRED_CODE, 'Query.ParseError', 'Auth.ProfileNotFound', 'Auth.NoActiveProfile',
    'Connection.EnvironmentNotFound', 'Validation.RequiredField',
]);

/** False only when the daemon's answer shows the request was rejected before execution. */
export function dmlMayHaveApplied(error: unknown): boolean {
    const code = dmlErrorData(error)?.code;
    return !(isDmlConfirmationRequired(error) || (code !== undefined && REJECTED_BEFORE_EXECUTION.has(code)));
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

const escapeChar = (char: string): string => `<U+${char.codePointAt(0)!.toString(16).toUpperCase().padStart(4, '0')}>`;

/** Multi-line text for a dialog with hidden characters made visible; `changed` says whether any were. */
export function visibleText(text: string): { text: string; changed: boolean } {
    const escaped = text.replace(HIDDEN_IN_TEXT, escapeChar);
    return { text: escaped, changed: escaped !== text };
}

function visibleLine(text: string, changed: { value: boolean }): string {
    const escaped = text.replace(HIDDEN_IN_LINE, escapeChar);
    if (escaped !== text) changed.value = true;
    return escaped;
}

/** The modal text for a statement: whole, or its start and end with exactly what is hidden counted. */
export function previewStatement(statement: string): StatementPreview {
    // U+2028/U+2029 count as line breaks (dialogs may render them as one) and are marked where they were.
    const parts = statement.split(/(\r\n|\n|\r|\u2028|\u2029)/);
    const lines = parts.filter((_, i) => i % 2 === 0);
    const separatorAfter = new Set(parts.flatMap((part, i) => (i % 2 === 1 && /[\u2028\u2029]/.test(part) ? [(i - 1) / 2] : [])));
    /** Text of lines [first, …] joined by line breaks, with each separator-ended line marked. */
    const mark = (text: string, first: number): string => text.split('\n')
        .map((line, i, all) => (i < all.length - 1 && separatorAfter.has(first + i) ? `${line}${escapeChar(parts[2 * (first + i) + 1])}` : line))
        .join('\n');
    const normalised = lines.join('\n');
    const base = { totalLines: lines.length, totalChars: normalised.length, hasSeparators: separatorAfter.size > 0 };
    if (lines.length <= MAX_PREVIEW_LINES && normalised.length <= MAX_PREVIEW_CHARS) {
        return { ...base, text: mark(normalised, 0), truncated: false, hiddenChars: 0, hiddenLines: 0 };
    }
    const head = lines.slice(0, HEAD_LINES).join('\n').slice(0, HEAD_CHARS);
    const tailLines = lines.slice(-TAIL_LINES).join('\n');
    const tail = tailLines.slice(Math.max(0, tailLines.length - TAIL_CHARS));
    let hidden = normalised.slice(head.length, normalised.length - tail.length);
    // A cut on a line boundary leaves the separating line breaks at the ends; they are not hidden content.
    if (hidden.startsWith('\n')) hidden = hidden.slice(1);
    if (hidden.endsWith('\n')) hidden = hidden.slice(0, -1);
    const hiddenLines = hidden.length === 0 ? 0 : hidden.split('\n').length;
    return {
        ...base, truncated: true, hiddenChars: hidden.length, hiddenLines,
        text: `${mark(head, 0)}\n… ${hidden.length} character(s) on ${hiddenLines} line(s) not shown …\n${mark(tail, lines.length - tail.split('\n').length)}`,
    };
}

const describeError = (error: unknown): string => error instanceof Error ? error.message : String(error);

/** Resolves the dialog's answer, `aborted` on abort, or throws the dialog's own failure. */
function raceAbort<T>(promise: Thenable<T>, signal: AbortSignal | undefined): Promise<{ value?: T; aborted: boolean }> {
    if (signal?.aborted) return Promise.resolve({ aborted: true });
    return new Promise((resolve, reject) => {
        const onAbort = (): void => resolve({ aborted: true });
        signal?.addEventListener('abort', onAbort, { once: true });
        Promise.resolve(promise).then(value => {
            signal?.removeEventListener('abort', onAbort);
            resolve({ value, aborted: false });
        }, (error: unknown) => {
            signal?.removeEventListener('abort', onAbort);
            reject(error instanceof Error ? error : new Error(String(error)));
        });
    });
}

/**
 * Opens the exact text that will run. Read-only through the extension's virtual document
 * provider; if that provider is not available, an untitled copy that says edits are not executed.
 */
async function showFullStatement(statement: string): Promise<'read-only' | 'copy'> {
    if (ExplainDocumentProvider.instance) {
        await ExplainDocumentProvider.instance.show(statement, 'sql', 'DML to confirm');
        return 'read-only';
    }
    const doc = await vscode.workspace.openTextDocument({ content: statement, language: 'sql' });
    await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside, preview: true });
    return 'copy';
}

function targetLabel(target: DmlTarget, changed: { value: boolean }): { name: string; type: string } {
    return {
        name: visibleLine(target.environmentName || target.environmentUrl || 'the selected environment', changed),
        type: visibleLine(target.environmentType || 'type unknown', changed),
    };
}

const normalisedUrl = (url: string | null | undefined): string | undefined => url?.trim().replace(/\/+$/, '').toLowerCase() || undefined;

/** Protection mark: production is protected, and a missing or unknown type is never treated as ordinary. */
function protectionMark(target: DmlWriteTarget): string | undefined {
    const type = target.type?.toLowerCase();
    const protection = target.protection?.toLowerCase();
    // No saved type counts as Production (the daemon reports its protection as Production): say why it is protected.
    const typeUnknown = !type || type === 'unknown';
    if (typeUnknown && (!protection || protection === 'unknown' || protection === 'production')) return 'TYPE UNKNOWN, TREATED AS PROTECTED';
    if (protection === 'production' || type === 'production') return 'PRODUCTION / PROTECTED';
    return undefined;
}

/** A target is the bound environment only when both URLs are known and equal. */
function isBoundTarget(target: DmlTarget, item: DmlWriteTarget): boolean {
    const bound = normalisedUrl(target.environmentUrl);
    return bound !== undefined && normalisedUrl(item.url) === bound;
}

function writeTargetLines(target: DmlTarget, writeTargets: DmlConfirmationOptions['writeTargets'], changed: { value: boolean }): string[] {
    const sentence = 'Other environments: SQL that names another environment (for example [LABEL].account) also writes there. ';
    if (writeTargets === DML_TARGETS_UNREADABLE) {
        return [`${sentence}The daemon reported write targets, but they could not be read, so other named environments are not listed here.`];
    }
    if (!writeTargets) {
        return [`${sentence}This daemon does not report those targets before execution, so other named environments are not listed here.`];
    }
    return [`Writes to ${writeTargets.length} environment(s):`, ...writeTargets.map(item => {
        const protection = protectionMark(item);
        const marks = [isBoundTarget(target, item) ? 'this environment' : 'OTHER ENVIRONMENT', ...(protection ? [protection] : [])];
        const kind = [item.type, item.protection && item.protection !== item.type ? `protection ${item.protection}` : undefined].filter(Boolean).join(', ');
        return `  - ${visibleLine(item.label ?? item.url ?? '', changed)}${kind ? ` (${visibleLine(kind, changed)})` : ''}`
            + `${item.url ? ` ${visibleLine(item.url, changed)}` : ''} [${marks.join('; ')}]`;
    })];
}

function otherTargetCount(target: DmlTarget, writeTargets: DmlConfirmationOptions['writeTargets']): number {
    if (!writeTargets || writeTargets === DML_TARGETS_UNREADABLE) return 0;
    return writeTargets.filter(item => !isBoundTarget(target, item)).length;
}

interface DialogText {
    readonly title: string;
    readonly detail: string;
}

function confirmationDialog(preview: StatementPreview, target: DmlTarget, mustReview: boolean, options: DmlConfirmationOptions): DialogText {
    const changed = { value: false };
    const { name, type } = targetLabel(target, changed);
    const others = otherTargetCount(target, options.writeTargets);
    const where = `${name} (${type})${others > 0 ? ` and ${others} other environment(s)` : ''}`;
    const statement = visibleText(preview.text);
    if (statement.changed || preview.hasSeparators) changed.value = true;
    const serverMessage = options.serverMessage && !options.serverMessage.includes('--confirm') ? options.serverMessage : undefined;
    const lines = [
        ...(options.notice ? [visibleLine(options.notice, changed), ''] : []),
        `Environment: ${name} (${type})`,
        `URL: ${visibleLine(target.environmentUrl || 'not available', changed)}`,
        ...(target.profileName ? [`Profile: ${visibleLine(target.profileName, changed)}`] : []),
        ...writeTargetLines(target, options.writeTargets, changed),
        ...(serverMessage ? [`Daemon: ${visibleLine(serverMessage, changed)}`] : []),
        '',
        `Statement: ${preview.totalLines} line(s), ${preview.totalChars} character(s) (a line break counts as one). `
            + 'This confirmation covers all of it, every statement in the script.',
        statement.text,
        '',
    ];
    if (preview.truncated) {
        lines.push(`Only the start and the end are shown here: ${preview.hiddenChars} character(s) on ${preview.hiddenLines} line(s) are hidden.`);
    }
    if (changed.value) {
        lines.push('Some characters that are invisible or reorder text are shown as <U+XXXX>. The statement that runs is unchanged.');
    }
    lines.push(mustReview
        ? `${DML_CONFIRM_ACTION} is offered after you have opened the full statement with ${DML_SHOW_ACTION}.`
        : `Nothing runs unless you choose ${DML_CONFIRM_ACTION}. ${DML_SHOW_ACTION} opens the exact text that will run in an editor; edits there are not executed.`);
    return {
        title: mustReview ? `Review the full SQL before running it on ${where}` : `Run SQL that changes data on ${where}?`,
        detail: lines.join('\n'),
    };
}

/** Review prompt that stays until answered (focus may move to the opened document). */
async function reviewStep(name: string, opened: 'read-only' | 'copy', signal: AbortSignal | undefined): Promise<{ value?: string; aborted: boolean }> {
    // Closing the quick pick on abort keeps no abandoned prompt on screen.
    if (signal?.aborted) return { aborted: true };
    const cts = new CancellationTokenSource();
    const onAbort = (): void => cts.cancel();
    signal?.addEventListener('abort', onAbort, { once: true });
    try {
        const copyNote = opened === 'copy' ? ' The opened editor is a copy for reading only: edits there are not executed.' : '';
        const pick = await raceAbort(vscode.window.showQuickPick([
            { label: DML_CONTINUE_ACTION, description: 'Show the confirmation again. Nothing runs yet.' },
            { label: DML_REVIEW_CANCEL, description: 'Do not run the statement.' },
        ], {
            title: `Review the full SQL for ${name}`,
            placeHolder: `Read the statement in the editor, then continue to the confirmation.${copyNote}`,
            ignoreFocusOut: true,
        }, cts.token), signal);
        return { value: pick.value?.label, aborted: pick.aborted };
    } finally {
        signal?.removeEventListener('abort', onAbort);
        cts.dispose();
    }
}

const DECLINED = 'Not executed: DML confirmation was declined. No records were changed.';

/** Shown when a cancel arrives after the confirmed request was sent. */
export const DML_SUBMITTED_THEN_CANCELLED = 'Cancelled after the confirmed statement was submitted: records may already have been changed.';
/** Shown when a confirmed request failed after it was sent. */
export const DML_SUBMITTED_MAY_HAVE_APPLIED = 'The confirmed statement had been submitted, so it may have partly applied. Check the data before running it again.';

/** Opens the full statement and runs the review prompt; `continue` means show the modal again with Execute. */
async function openAndReview(statement: string, name: string, signal: AbortSignal | undefined): Promise<DmlConfirmation | 'continue'> {
    let opened: 'read-only' | 'copy';
    try {
        opened = await showFullStatement(statement);
    } catch (error) {
        return { kind: 'failed', message: `Not executed: the full statement could not be opened (${describeError(error)}). No records were changed.` };
    }
    let review: { value?: string; aborted: boolean };
    try {
        review = await reviewStep(name, opened, signal);
    } catch (error) {
        return { kind: 'failed', message: `Not executed: the review prompt could not be shown (${describeError(error)}). No records were changed.` };
    }
    if (review.aborted || signal?.aborted) return { kind: 'aborted' };
    if (review.value === DML_CONTINUE_ACTION) return 'continue';
    if (review.value === DML_REVIEW_CANCEL) return { kind: 'declined', message: 'Not executed: cancelled at the review step. No records were changed.' };
    return { kind: 'declined', message: 'Not executed: the review prompt was dismissed, which counts as declining. No records were changed.' };
}

/**
 * Confirmation for one DML execution of exactly `statement` on exactly `target`.
 *
 * "Show Full Statement" is listed first and opens the exact text, then a review prompt that stays
 * until answered, then the modal again. When the statement is longer than the modal shows,
 * "Execute Anyway" is offered only after the full text has been opened. Only an explicit
 * "Execute Anyway" yields `confirmed`; Escape, close, Cancel or a dismissed review yield
 * `declined`, a dialog or document failure yields `failed`, an abort yields `aborted`. Never throws.
 */
export async function confirmDmlExecution(statement: string, target: DmlTarget, options: DmlConfirmationOptions = {}): Promise<DmlConfirmation> {
    const preview = previewStatement(statement);
    const name = targetLabel(target, { value: false }).name;
    let reviewed = false;
    for (;;) {
        if (options.signal?.aborted) return { kind: 'aborted' };
        const mustReview = preview.truncated && !reviewed;
        const dialog = confirmationDialog(preview, target, mustReview, options);
        const buttons = mustReview ? [DML_SHOW_ACTION] : [DML_SHOW_ACTION, DML_CONFIRM_ACTION];
        let choice: { value?: string; aborted: boolean };
        try {
            choice = await raceAbort(vscode.window.showWarningMessage(dialog.title, { modal: true, detail: dialog.detail }, ...buttons), options.signal);
        } catch (error) {
            return { kind: 'failed', message: `Not executed: the DML confirmation could not be shown (${describeError(error)}). No records were changed.` };
        }
        if (choice.aborted || options.signal?.aborted) return { kind: 'aborted' };
        if (choice.value === DML_CONFIRM_ACTION) return mustReview ? { kind: 'declined', message: DECLINED } : { kind: 'confirmed' };
        if (choice.value !== DML_SHOW_ACTION) return { kind: 'declined', message: DECLINED };
        const review = await openAndReview(statement, name, options.signal);
        if (review !== 'continue') return review;
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
