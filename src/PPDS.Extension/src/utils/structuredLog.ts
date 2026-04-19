import type * as vscode from 'vscode';

/**
 * Structured log schema shared by every command wrapper in the extension so
 * `ppds logs dump` (and any user who opens the PPDS output channel) can
 * grep for start/end/error triples by command name or correlation id.
 *
 * This is **local observability only** — nothing is sent over the network.
 * v1 has no remote telemetry.
 */
export type StructuredLogEvent =
    | 'command.start'
    | 'command.end'
    | 'command.error';

/**
 * Payload passed to {@link logCommand}. The shape intentionally matches the
 * RPC-side log lines emitted by `SafeExecuteAsync` in `RpcMethodHandler.cs`
 * so both sides of a client/daemon interaction can be joined by correlation id.
 */
export interface StructuredCommandLog {
    /** Which lifecycle phase this entry represents. */
    event: StructuredLogEvent;
    /** Stable identifier for the command (e.g., `ppds.openSolutions`, `restartDaemon`). */
    command: string;
    /** Optional correlation id; expect a GUID string when present. */
    correlationId?: string;
    /** Wall-clock duration of the command in milliseconds (populated on `end`/`error`). */
    durationMs?: number;
    /** Terminal outcome (populated on `end`/`error`). */
    outcome?: 'success' | 'failure' | 'cancelled';
    /** Redacted error message when `event === 'command.error'`. */
    errorMessage?: string;
    /** Arbitrary additional structured fields; kept flat for greppability. */
    // eslint-disable-next-line @typescript-eslint/no-explicit-any -- free-form diagnostic metadata
    extra?: Record<string, any>;
}

/**
 * Emits a structured JSON line to the PPDS `LogOutputChannel`. Callers should
 * produce matched `command.start` + `command.end`/`command.error` pairs so the
 * log stream is self-describing without knowing the command's internals.
 *
 * The entry is written as a single-line JSON object (with a human-readable
 * `event:` prefix) so:
 *   - `ppds logs dump` ships the raw line and JSON parsers still work after
 *     secret redaction (redaction only changes value bodies, not structure).
 *   - Users scrolling the LogOutputChannel in VS Code see a legible summary.
 *
 * @param channel The shared PPDS LogOutputChannel (may be undefined during
 *     startup before activation completes).
 * @param entry Structured log payload.
 */
export function logCommand(
    channel: vscode.LogOutputChannel | undefined,
    entry: StructuredCommandLog,
): void {
    if (!channel) return;

    // Stable insertion order so a user looking at the log sees command first.
    const payload = {
        event: entry.event,
        command: entry.command,
        correlationId: entry.correlationId,
        durationMs: entry.durationMs,
        outcome: entry.outcome,
        errorMessage: entry.errorMessage,
        ...(entry.extra ?? {}),
    };

    let serialized: string;
    try {
        serialized = JSON.stringify(payload);
    } catch {
        serialized = `${entry.event} ${entry.command} (unserializable payload)`;
    }

    const prefix = `${entry.event} ${entry.command}`;
    const line = `${prefix} ${serialized}`;

    // Route each event to the matching log level so users can filter via the
    // LogOutputChannel dropdown (Trace/Debug/Info/Warning/Error).
    switch (entry.event) {
        case 'command.start':
            channel.debug(line);
            return;
        case 'command.end':
            if (entry.outcome === 'failure') {
                channel.warn(line);
            } else {
                channel.info(line);
            }
            return;
        case 'command.error':
            channel.error(line);
            return;
    }
}

/**
 * Generates a short correlation id for client-side commands that don't call
 * the daemon (so `command.start`/`command.end` still pair up). For commands
 * that hit the daemon, prefer the correlation id returned in the RPC error/
 * response payload so logs on both sides join.
 */
export function newClientCorrelationId(): string {
    // Prefer the browser-style crypto.randomUUID if available (Node 19+ / Electron).
    const cryptoRef = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
    if (cryptoRef?.randomUUID) {
        return cryptoRef.randomUUID();
    }
    // Fallback: timestamp + random to keep this dependency-free.
    return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 10)}`;
}
