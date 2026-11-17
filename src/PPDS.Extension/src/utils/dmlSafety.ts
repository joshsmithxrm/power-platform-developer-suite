import { ResponseError } from 'vscode-jsonrpc/node';
import * as vscode from 'vscode';

import type { QueryResultResponse } from '../types.js';

/**
 * Shared DML safety flow for every surface that sends user SQL to `query/sql`
 * (Data Explorer panel and notebook cells).
 *
 * The extension never decides by itself whether a statement is DML. It always sends
 * `dmlSafety: { isConfirmed: false }` first; the daemon's DmlSafetyGuard parses the
 * statement and either runs it (read-only), refuses it (`Query.DmlBlocked`), or answers
 * `Query.DmlConfirmationRequired`. Only that answer leads to the modal below, and only
 * an explicit "Execute Anyway" leads to a second request with `isConfirmed: true`.
 */

/** Button label shared by every DML confirmation (Data Explorer wording). */
export const DML_CONFIRM_ACTION = 'Execute Anyway';

/** Safety options for a request the user has not confirmed. Sent on every first attempt and on every load-more. */
export const UNCONFIRMED_DML_SAFETY = Object.freeze({ isConfirmed: false });

const CONFIRMATION_REQUIRED_CODE = 'Query.DmlConfirmationRequired';
/** Error codes the daemon uses when a safety guard refuses a statement outright. */
const REFUSAL_CODES = new Set(['Query.DmlBlocked', 'Query.DmlRowCapExceeded', 'Safety.ShakedownActive']);
const MAX_PROMPT_STATEMENT_CHARS = 2000;

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

/**
 * When the daemon's safety guard refused the statement (no WHERE, row cap, protected or
 * cross-environment target, shakedown), returns a user-facing message; otherwise undefined.
 */
export function dmlRefusalMessage(error: unknown): string | undefined {
    const data = dmlErrorData(error);
    if (!data || !(data.dmlBlocked === true || (data.code !== undefined && REFUSAL_CODES.has(data.code)))) return undefined;
    const reason = data.message || (error instanceof Error ? error.message : '') || 'DML operation blocked.';
    return `Not executed: the DML safety guard refused this statement.\n${reason}`;
}

function promptStatement(statement: string): string {
    const trimmed = statement.trim();
    if (trimmed.length <= MAX_PROMPT_STATEMENT_CHARS) return trimmed;
    const hidden = trimmed.length - MAX_PROMPT_STATEMENT_CHARS;
    return `${trimmed.slice(0, MAX_PROMPT_STATEMENT_CHARS)}\n… (${hidden} more characters not shown)`;
}

/**
 * Modal confirmation for one DML execution. Returns true only for an explicit
 * "Execute Anyway"; dismissing, Escape or Cancel return false. Never remembered.
 */
export async function confirmDmlExecution(statement: string, target: DmlTarget): Promise<boolean> {
    const name = target.environmentName || target.environmentUrl || 'the selected environment';
    const type = target.environmentType || 'Unknown type';
    const choice = await vscode.window.showWarningMessage(
        `Execute this DML statement on ${name} (${type})?`,
        {
            modal: true,
            detail: `${target.environmentUrl ?? 'No environment URL'}\n\n${promptStatement(statement)}\n\n`
                + 'This statement inserts, updates or deletes Dataverse records. Nothing runs unless you choose '
                + `${DML_CONFIRM_ACTION}.`,
        },
        DML_CONFIRM_ACTION,
    );
    return choice === DML_CONFIRM_ACTION;
}

function numeric(value: unknown): number | undefined {
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (value && typeof value === 'object' && 'value' in value) return numeric(value.value);
    if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value);
    return undefined;
}

/**
 * Describes a DML execution in which rows failed, or returns undefined.
 *
 * Reads the additive response fields (`success: false`, `affectedRows`, `failedRows`,
 * `dmlError`) and, for daemons that predate them, the single DML summary row
 * (`affected_rows`, `failed_rows`) that every DML statement returns.
 */
export function describeDmlFailure(result: QueryResultResponse): string | undefined {
    const counts = dmlCounts(result);
    const reason = typeof result.dmlError === 'string' && result.dmlError.trim() ? result.dmlError.trim() : undefined;
    const reportedFailure = result.success === false && (reason !== undefined || counts.failed !== undefined);
    const rowsFailed = counts.failed !== undefined && counts.failed > 0;
    if (!reportedFailure && !rowsFailed) return undefined;
    const parts = [
        counts.failed !== undefined ? `${counts.failed} row(s) failed` : undefined,
        counts.affected !== undefined ? `${counts.affected} row(s) succeeded` : undefined,
    ].filter(Boolean).join(', ');
    return `DML failed${parts ? `: ${parts}` : ''}.
${reason ?? 'The daemon did not report a failure reason.'}`;
}

/** Counts from the additive fields, else from the legacy one-row DML summary. */
function dmlCounts(result: QueryResultResponse): { affected?: number; failed?: number } {
    const failed = numeric(result.failedRows);
    if (failed !== undefined) return { affected: numeric(result.affectedRows), failed };
    if (result.records.length !== 1) return { affected: numeric(result.affectedRows) };
    const row = result.records[0] ?? {};
    const keys = Object.keys(row);
    if (keys.length !== 2 || !keys.includes('affected_rows') || !keys.includes('failed_rows')) {
        return { affected: numeric(result.affectedRows) };
    }
    return { affected: numeric(row.affected_rows), failed: numeric(row.failed_rows) };
}
