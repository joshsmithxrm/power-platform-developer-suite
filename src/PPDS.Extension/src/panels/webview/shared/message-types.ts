/**
 * Typed message protocols for VS Code webview panels.
 * These discriminated unions are shared between the host (TypeScript, Node.js)
 * and webview (TypeScript, browser) to ensure type-safe communication.
 */

// Import daemon response types that appear in message payloads
import type { QueryResultResponse, CompletionItemDto, MetadataEntityDetailDto, WebResourceInfoDto } from '../../../types.js';

// ── Query Panel ─────────────────────────────────────────────────────────────

/** Messages the Query Panel webview sends to the extension host. */
export type QueryPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'executeQuery'; sql: string; useTds?: boolean; language?: string }
    | { command: 'showFetchXml'; sql: string }
    | { command: 'loadMore'; pagingCookie: string; page: number }
    | { command: 'explainQuery'; sql: string }
    | { command: 'exportResults'; format?: string }
    | { command: 'saveQuery'; sql: string; language: string }
    | { command: 'loadQueryFromFile' }
    | { command: 'openInNotebook'; sql: string }
    | { command: 'showHistory' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openRecordUrl'; url: string }
    | { command: 'requestClipboard' }
    | { command: 'requestCompletions'; requestId: number; sql: string; cursorOffset: number; language: string }
    | { command: 'webviewError'; error: string; stack?: string }
    | { command: 'cancelQuery' }
    | { command: 'convertQuery'; sql: string; fromLanguage: string; toLanguage: string }
    | { command: 'refresh' }
    | { command: 'requestEnvironmentList' };

/** Messages the extension host sends to the Query Panel webview. */
export type QueryPanelHostToWebview =
    | { command: 'loadQuery'; sql: string }
    | { command: 'updateEnvironment'; name: string; url: string | null; envType: string | null; envColor: string | null }
    | { command: 'executionStarted' }
    | { command: 'queryResult'; data: QueryResultResponse }
    | { command: 'queryCancelled' }
    | { command: 'queryError'; error: string }
    | { command: 'appendResults'; data: QueryResultResponse }
    | { command: 'clipboardContent'; text: string }
    | { command: 'completionResult'; requestId: number; items: CompletionItemDto[] }
    | { command: 'daemonReconnected' }
    | { command: 'queryConverted'; content: string; language: string }
    | { command: 'conversionFailed'; error: string; language: string };

// ── Solutions Panel ─────────────────────────────────────────────────────────

/** Serialized solution data sent to the webview. */
export interface SolutionViewDto {
    id: string;
    uniqueName: string;
    friendlyName: string;
    version: string;
    publisherName: string;
    isManaged: boolean;
    description: string;
    createdOn: string | null;
    modifiedOn: string | null;
    installedOn: string | null;
}

/** Component group sent after expanding a solution. */
export interface ComponentGroupDto {
    typeName: string;
    components: {
        objectId: string;
        isMetadata: boolean;
        logicalName?: string;
        schemaName?: string;
        displayName?: string;
        rootComponentBehavior: number;
    }[];
}

/** Messages the Solutions Panel webview sends to the extension host. */
export type SolutionsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'requestEnvironmentList' }
    | { command: 'refresh' }
    | { command: 'expandSolution'; uniqueName: string }
    | { command: 'collapseSolution'; uniqueName: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openInMaker'; solutionId?: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Solutions Panel webview. */
export type SolutionsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'solutionsLoaded'; solutions: SolutionViewDto[] }
    | { command: 'componentsLoading'; uniqueName: string }
    | { command: 'componentsLoaded'; uniqueName: string; groups: ComponentGroupDto[] }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Import Jobs Panel ─────────────────────────────────────────────────────

/** Import job info as sent to the webview for table display. */
export interface ImportJobViewDto {
    id: string;
    solutionName: string | null;
    status: string;
    progress: number;
    createdBy: string | null;
    createdOn: string | null;
    startedOn: string | null;
    completedOn: string | null;
    duration: string | null;
}

/** Messages the Import Jobs Panel webview sends to the extension host. */
export type ImportJobsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectJob'; id: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Import Jobs Panel webview. */
export type ImportJobsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'importJobsLoaded'; jobs: ImportJobViewDto[] }
    | { command: 'importJobDetailLoaded'; id: string; data: string | null }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Plugin Traces Panel ──────────────────────────────────────────────────

/** Plugin trace info as sent to the webview for table display. */
export interface PluginTraceViewDto {
    id: string;
    typeName: string;
    messageName: string | null;
    primaryEntity: string | null;
    mode: string;
    operationType: string;
    depth: number;
    createdOn: string;
    durationMs: number | null;
    hasException: boolean;
    correlationId: string | null;
}

/** Plugin trace detail for the detail pane. */
export interface PluginTraceDetailViewDto extends PluginTraceViewDto {
    constructorDurationMs: number | null;
    executionStartTime: string | null;
    exceptionDetails: string | null;
    messageBlock: string | null;
    configuration: string | null;
    secureConfiguration: string | null;
    requestId: string | null;
}

/** Timeline node for the waterfall visualization. */
export interface TimelineNodeViewDto {
    traceId: string;
    typeName: string;
    messageName: string | null;
    depth: number;
    durationMs: number | null;
    hasException: boolean;
    offsetPercent: number;
    widthPercent: number;
    hierarchyDepth: number;
    children: TimelineNodeViewDto[];
}

/** Filter state sent from webview to host. */
export interface TraceFilterViewDto {
    typeName?: string;
    messageName?: string;
    primaryEntity?: string;
    mode?: string;
    hasException?: boolean;
    correlationId?: string;
    minDurationMs?: number;
    startDate?: string;
    endDate?: string;
}

/** Messages the Plugin Traces Panel webview sends to the extension host. */
export type PluginTracesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'applyFilter'; filter: TraceFilterViewDto }
    | { command: 'selectTrace'; id: string }
    | { command: 'loadTimeline'; correlationId: string }
    | { command: 'deleteTraces'; ids: string[] }
    | { command: 'deleteOlderThan'; days: number }
    | { command: 'requestTraceLevel' }
    | { command: 'setTraceLevel'; level: string }
    | { command: 'setAutoRefresh'; intervalSeconds: number | null }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Plugin Traces Panel webview. */
export type PluginTracesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'tracesLoaded'; traces: PluginTraceViewDto[] }
    | { command: 'traceDetailLoaded'; trace: PluginTraceDetailViewDto }
    | { command: 'timelineLoaded'; nodes: TimelineNodeViewDto[] }
    | { command: 'traceLevelLoaded'; level: string; levelValue: number }
    | { command: 'deleteComplete'; deletedCount: number }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Metadata Browser Panel ─────────────────────────────────────────────

/** Entity summary as sent to the webview for the entity list. */
export interface MetadataEntityViewDto {
    logicalName: string;
    schemaName: string;
    displayName: string;
    isCustomEntity: boolean;
    isManaged: boolean;
    ownershipType: string | null;
    description: string | null;
}

/** Messages the Metadata Browser Panel webview sends to the extension host. */
export type MetadataBrowserPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectEntity'; logicalName: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker'; entityLogicalName?: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Metadata Browser Panel webview. */
export type MetadataBrowserPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'entitiesLoaded'; entities: MetadataEntityViewDto[] }
    | { command: 'entityDetailLoaded'; entity: MetadataEntityDetailDto }
    | { command: 'entityDetailLoading'; logicalName: string }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Connection References Panel ──────────────────────────────────────────

/** Connection reference info as sent to the webview for table display. */
export interface ConnectionReferenceViewDto {
    logicalName: string;
    displayName: string | null;
    connectorId: string | null;
    connectionId: string | null;
    isManaged: boolean;
    modifiedOn: string | null;
    connectionStatus: string;
    connectorDisplayName: string | null;
}

/** Connection reference detail sent when a row is selected. */
export interface ConnectionReferenceDetailViewDto extends ConnectionReferenceViewDto {
    description: string | null;
    isBound: boolean;
    createdOn: string | null;
    flows: { uniqueName: string; displayName: string | null; state: string | null }[];
    connectionOwner: string | null;
    connectionIsShared: boolean | null;
}

/** Analysis result sent after running orphan analysis. */
export interface ConnectionReferencesAnalyzeViewDto {
    orphanedReferences: { logicalName: string; displayName: string | null; connectorId: string | null }[];
    orphanedFlows: { uniqueName: string; displayName: string | null; missingReference: string | null }[];
    totalReferences: number;
    totalFlows: number;
}

/** Solution option used by the solution filter dropdown. */
export interface SolutionOptionDto {
    id: string;
    uniqueName: string;
    friendlyName: string;
}

/** Messages the Connection References Panel webview sends to the extension host. */
export type ConnectionReferencesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectReference'; logicalName: string }
    | { command: 'analyze' }
    | { command: 'filterBySolution'; solutionId: string | null }
    | { command: 'requestSolutionList' }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Connection References Panel webview. */
export type ConnectionReferencesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'loading' }
    | { command: 'connectionReferencesLoaded'; references: ConnectionReferenceViewDto[] }
    | { command: 'connectionReferenceDetailLoaded'; detail: ConnectionReferenceDetailViewDto }
    | { command: 'analyzeResult'; result: ConnectionReferencesAnalyzeViewDto }
    | { command: 'solutionListLoaded'; solutions: SolutionOptionDto[] }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Environment Variables Panel ──────────────────────────────────────────

/** Environment variable info as sent to the webview for table display. */
export interface EnvironmentVariableViewDto {
    schemaName: string;
    displayName: string | null;
    type: string;
    defaultValue: string | null;
    currentValue: string | null;
    isManaged: boolean;
    isRequired: boolean;
    modifiedOn: string | null;
    hasOverride: boolean;
    isMissing: boolean;
}

/** Environment variable detail sent when a row is selected. */
export interface EnvironmentVariableDetailViewDto extends EnvironmentVariableViewDto {
    description: string | null;
    createdOn: string | null;
}

/** Messages the Environment Variables Panel webview sends to the extension host. */
export type EnvironmentVariablesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectVariable'; schemaName: string }
    | { command: 'editVariable'; schemaName: string }
    | { command: 'saveVariable'; schemaName: string; value: string }
    | { command: 'filterBySolution'; solutionId: string | null }
    | { command: 'requestSolutionList' }
    | { command: 'exportDeploymentSettings' }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Environment Variables Panel webview. */
export type EnvironmentVariablesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'loading' }
    | { command: 'environmentVariablesLoaded'; variables: EnvironmentVariableViewDto[] }
    | { command: 'environmentVariableDetailLoaded'; detail: EnvironmentVariableDetailViewDto }
    | { command: 'editVariableDialog'; schemaName: string; displayName: string | null; type: string; currentValue: string | null }
    | { command: 'variableSaved'; schemaName: string; success: boolean }
    | { command: 'solutionListLoaded'; solutions: SolutionOptionDto[] }
    | { command: 'deploymentSettingsExported'; filePath: string }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Web Resources Panel ──────────────────────────────────────────────────

/** Messages the Web Resources Panel webview sends to the extension host. */
export type WebResourcesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'requestEnvironmentList' }
    | { command: 'requestSolutionList' }
    | { command: 'selectSolution'; solutionId: string | null }
    | { command: 'toggleTextOnly'; textOnly: boolean }
    | { command: 'openWebResource'; id: string; name: string; isTextType: boolean; webResourceType: number }
    | { command: 'publishSelected'; ids: string[] }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Web Resources Panel webview. */
export type WebResourcesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'solutionListLoaded'; solutions: SolutionOptionDto[] }
    | { command: 'webResourcesLoaded'; resources: WebResourceInfoDto[]; requestId: number }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'publishResult'; count: number; error?: string }
    | { command: 'daemonReconnected' };
