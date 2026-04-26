/**
 * Typed message protocols for VS Code webview panels.
 * These discriminated unions are shared between the host (TypeScript, Node.js)
 * and webview (TypeScript, browser) to ensure type-safe communication.
 */

// Import daemon response types that appear in message payloads
import type { QueryResultResponse, CompletionItemDto, MetadataEntityDetailDto, WebResourceInfoDto, MetadataGlobalChoiceSummaryDto, MetadataOptionSetDto, MetadataAuthoringResult, MetadataDeleteResult } from '../../../types.js';

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
    | { command: 'requestValidation'; requestId: number; sql: string; language: string }
    | { command: 'webviewError'; error: string; stack?: string }
    | { command: 'cancelQuery' }
    | { command: 'convertQuery'; sql: string; fromLanguage: string; toLanguage: string }
    | { command: 'refresh' }
    | { command: 'requestEnvironmentList' };

/** Messages the extension host sends to the Query Panel webview. */
export type QueryPanelHostToWebview =
    | { command: 'loadQuery'; sql: string }
    | { command: 'updateEnvironment'; name: string; url: string | null; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'executionStarted' }
    | { command: 'queryResult'; data: QueryResultResponse }
    | { command: 'queryCancelled' }
    | { command: 'queryError'; error: string; diagnostics?: Array<{ start: number; length: number; severity: string; message: string }> }
    | { command: 'appendResults'; data: QueryResultResponse }
    | { command: 'clipboardContent'; text: string }
    | { command: 'completionResult'; requestId: number; items: CompletionItemDto[] }
    | { command: 'validationResult'; requestId: number; diagnostics: Array<{ start: number; length: number; severity: string; message: string }> }
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
    isVisible: boolean;
    isApiManaged: boolean;
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
    | { command: 'setVisibilityFilter'; includeInternal: boolean }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Solutions Panel webview. */
export type SolutionsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'solutionsLoaded'; solutions: SolutionViewDto[]; totalCount: number; filtersApplied: string[] }
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
    operationContext: string | null;
}

/** Messages the Import Jobs Panel webview sends to the extension host. */
export type ImportJobsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'viewImportLog'; id: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Import Jobs Panel webview. */
export type ImportJobsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'importJobsLoaded'; jobs: ImportJobViewDto[]; totalCount: number }
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
    // Additional fields (PT-01 through PT-09)
    stage: string | null;
    constructorStartTime: string | null;
    isSystemCreated: boolean;
    createdById: string | null;
    createdOnBehalfById: string | null;
    pluginStepId: string | null;
    persistenceKey: string | null;
    organizationId: string | null;
    profile: string | null;
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
    hasNoException?: boolean;
    correlationId?: string;
    minDurationMs?: number;
    startDate?: string;
    endDate?: string;
    operationType?: string;
    stage?: string;
}

/** Advanced query builder condition. */
export interface QueryConditionViewDto {
    id: string;
    enabled: boolean;
    field: string;
    operator: string;
    value: string;
    logicalOperator: 'and' | 'or';
}

/** Full advanced query sent from the query builder. */
export interface AdvancedQueryViewDto {
    quickFilterIds: string[];
    conditions: QueryConditionViewDto[];
}

/** Messages the Plugin Traces Panel webview sends to the extension host. */
export type PluginTracesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'applyFilter'; filter: TraceFilterViewDto }
    | { command: 'applyAdvancedFilter'; query: AdvancedQueryViewDto }
    | { command: 'selectTrace'; id: string }
    | { command: 'loadTimeline'; correlationId: string }
    | { command: 'deleteTraces'; ids: string[] }
    | { command: 'deleteOlderThan'; days: number }
    | { command: 'requestTraceLevel' }
    | { command: 'setTraceLevel'; level: string }
    | { command: 'setAutoRefresh'; intervalSeconds: number | null }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'exportTraces'; format: string }
    | { command: 'persistState'; key: string; value: unknown }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Plugin Traces Panel webview. */
export type PluginTracesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'tracesLoaded'; traces: PluginTraceViewDto[]; totalCount: number }
    | { command: 'traceDetailLoaded'; trace: PluginTraceDetailViewDto }
    | { command: 'timelineLoaded'; nodes: TimelineNodeViewDto[] }
    | { command: 'traceLevelLoaded'; level: string; levelValue: number }
    | { command: 'deleteComplete'; deletedCount: number }
    | { command: 'selectTraceById'; id: string }
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
    | { command: 'setIncludeIntersect'; includeIntersect: boolean }
    | { command: 'selectEntity'; logicalName: string }
    | { command: 'selectGlobalChoice'; name: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker'; entityLogicalName?: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'createTable' }
    | { command: 'deleteTable'; entityLogicalName: string }
    | { command: 'createColumn'; entityLogicalName: string }
    | { command: 'deleteColumn'; entityLogicalName: string; columnLogicalName: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Metadata Browser Panel webview. */
export type MetadataBrowserPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'entitiesLoaded'; entities: MetadataEntityViewDto[]; intersectHiddenCount: number }
    | { command: 'globalChoicesLoaded'; choices: MetadataGlobalChoiceSummaryDto[] }
    | { command: 'globalChoiceDetailLoaded'; choice: MetadataOptionSetDto }
    | { command: 'entityDetailLoaded'; entity: MetadataEntityDetailDto }
    | { command: 'entityDetailLoading'; logicalName: string }
    | { command: 'globalChoiceDetailLoading'; name: string }
    | { command: 'authoringResult'; result: MetadataAuthoringResult }
    | { command: 'deleteResult'; result: MetadataDeleteResult }
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
    /** Number of flows using this connection reference. */
    flowCount?: number;
    /** True if the CR is unbound or has orphaned flows (health warning). */
    hasHealthWarning?: boolean;
}

/** Connection reference detail sent when a row is selected. */
export interface ConnectionReferenceDetailViewDto extends ConnectionReferenceViewDto {
    description: string | null;
    isBound: boolean;
    createdOn: string | null;
    flows: { flowId: string; uniqueName: string; displayName: string | null; state: string | null }[];
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
    | { command: 'getDetail'; logicalName: string }
    | { command: 'analyze' }
    | { command: 'filterBySolution'; solutionId: string | null }
    | { command: 'setIncludeInactive'; includeInactive: boolean }
    | { command: 'requestSolutionList' }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'openFlowInMaker'; url: string }
    | { command: 'syncDeploymentSettings' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Connection References Panel webview. */
export type ConnectionReferencesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'loading' }
    | { command: 'connectionReferencesLoaded'; references: ConnectionReferenceViewDto[]; totalCount: number; filtersApplied: string[] }
    | { command: 'connectionReferenceDetailLoaded'; detail: ConnectionReferenceDetailViewDto; environmentId: string | null }
    | { command: 'analyzeResult'; result: ConnectionReferencesAnalyzeViewDto }
    | { command: 'solutionListLoaded'; solutions: SolutionOptionDto[] }
    | { command: 'deploymentSettingsSynced'; filePath: string; envVars: DeploymentSettingsSyncStatsDto; connectionRefs: DeploymentSettingsSyncStatsDto }
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

/** Result counts for a sync deployment settings operation. */
export interface DeploymentSettingsSyncStatsDto {
    added: number;
    removed: number;
    preserved: number;
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
    | { command: 'syncDeploymentSettings' }
    | { command: 'setIncludeInactive'; includeInactive: boolean }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Environment Variables Panel webview. */
export type EnvironmentVariablesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'loading' }
    | { command: 'environmentVariablesLoaded'; variables: EnvironmentVariableViewDto[]; totalCount: number; filtersApplied: string[] }
    | { command: 'environmentVariableDetailLoaded'; detail: EnvironmentVariableDetailViewDto }
    | { command: 'editVariableDialog'; schemaName: string; displayName: string | null; type: string; currentValue: string | null }
    | { command: 'variableSaved'; schemaName: string; success: boolean }
    | { command: 'solutionListLoaded'; solutions: SolutionOptionDto[] }
    | { command: 'deploymentSettingsExported'; filePath: string }
    | { command: 'deploymentSettingsSynced'; filePath: string; envVars: DeploymentSettingsSyncStatsDto; connectionRefs: DeploymentSettingsSyncStatsDto }
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
    | { command: 'publishAll' }
    | { command: 'openInMaker' }
    | { command: 'serverSearch'; term: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Web Resources Panel webview. */
export type WebResourcesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'solutionListLoaded'; solutions: SolutionOptionDto[] }
    | { command: 'webResourcesLoaded'; resources: WebResourceInfoDto[]; requestId: number; totalCount: number }
    | { command: 'webResourcesPage'; resources: WebResourceInfoDto[]; requestId: number; loadedSoFar: number; totalCount: number }
    | { command: 'webResourcesLoadComplete'; requestId: number; totalCount: number }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'filterState'; solutionId: string | null; textOnly: boolean }
    | { command: 'publishResult'; count: number; error?: string }
    | { command: 'daemonReconnected' };

// ── Plugins Panel ────────────────────────────────────────────────────────────

/** Messages the Plugins Panel webview sends to the extension host. */
export type PluginsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'setViewMode'; mode: 'assembly' | 'message' | 'entity' }
    | { command: 'expandNode'; nodeId: string; nodeType: string }
    | { command: 'selectNode'; nodeId: string; nodeType: string }
    | { command: 'search'; text: string }
    | { command: 'applyFilter'; hideHidden: boolean; hideMicrosoft: boolean }
    | { command: 'registerEntity'; entityType: string; parentId?: string; fields: Record<string, unknown> }
    | { command: 'updateEntity'; entityType: string; id: string; fields: Record<string, unknown> }
    | { command: 'toggleStep'; id: string; enabled: boolean }
    | { command: 'unregister'; entityType: string; id: string; force: boolean }
    | { command: 'downloadBinary'; entityType: string; id: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openHelp' }
    | { command: 'webviewError'; error: string; stack?: string }
    | { command: 'copyToClipboard'; text: string };

/** Messages the extension host sends to the Plugins Panel webview. */
export type PluginsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; profileName?: string; envType: string | null; envColor: string | null }
    | { command: 'treeLoaded'; data: PluginTreeData }
    | { command: 'childrenLoaded'; parentId: string; children: PluginTreeNode[] }
    | { command: 'nodeUpdated'; node: PluginTreeNode }
    | { command: 'nodeRemoved'; nodeId: string }
    | { command: 'detailLoaded'; detail: Record<string, unknown> }
    | { command: 'messagesLoaded'; messages: string[] }
    | { command: 'entitiesLoaded'; entities: string[] }
    | { command: 'attributesLoaded'; attributes: AttributeViewDto[] }
    | { command: 'showRegisterForm'; formType: 'step' | 'image' | 'assembly' | 'package' | 'webhook' | 'serviceendpoint' | 'customapi' | 'dataprovider'; parentId?: string; contract?: string }
    | { command: 'showUpdateForm'; formType: 'step' | 'image' | 'assembly' | 'package' | 'webhook' | 'serviceendpoint' | 'customapi' | 'dataprovider'; id: string; data: Record<string, unknown>; contract?: string }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'detailError'; message: string }
    | { command: 'daemonReconnected' };

/** Tree data sent on initial load. */
export interface PluginTreeData {
    assemblies: PluginTreeNode[];
    packages: PluginTreeNode[];
    serviceEndpoints: PluginTreeNode[];
    customApis: PluginTreeNode[];
    dataSources: PluginTreeNode[];
    /** Human-readable summary e.g. "2 packages — 3 assemblies — 336 custom APIs" */
    statusSummary?: string;
}

/** A single node in the plugin tree. */
export interface PluginTreeNode {
    id: string;
    name: string;
    nodeType: string;
    icon?: string;
    badge?: string;
    isEnabled?: boolean;
    isManaged?: boolean;
    isHidden?: boolean;
    children?: PluginTreeNode[];
    hasChildren?: boolean;
}

/** Attribute info sent when entity attributes are loaded. */
export interface AttributeViewDto {
    logicalName: string;
    displayName: string;
    attributeType: string;
}

/** Shape of a child entity returned by the daemon for lazy tree loading. */
export interface PluginEntityChild {
    id: string;
    name?: string;
    typeName?: string;
    nodeType?: string;
    isEnabled?: boolean;
    isManaged?: boolean;
    hasChildren?: boolean;
}
