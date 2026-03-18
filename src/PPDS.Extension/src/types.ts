/**
 * Shared TypeScript interfaces for all PPDS daemon RPC responses.
 * These mirror the C# response DTOs in the daemon server.
 */

// ── Auth ────────────────────────────────────────────────────────────────────

export interface AuthListResponse {
    activeProfile: string | null;
    activeProfileIndex: number | null;
    profiles: ProfileInfo[];
}

export interface ProfileInfo {
    index: number;
    name: string | null;
    identity: string;
    authMethod: string;
    cloud: string;
    environment: EnvironmentSummary | null;
    isActive: boolean;
    createdAt: string | null;
    lastUsedAt: string | null;
}

export interface EnvironmentSummary {
    url: string;
    displayName: string;
    environmentId: string | null;
}

export interface AuthWhoResponse {
    index: number;
    name: string | null;
    authMethod: string;
    cloud: string;
    tenantId: string | null;
    username: string | null;
    objectId: string | null;
    applicationId: string | null;
    tokenExpiresOn: string | null;
    tokenStatus: string | null;
    environment: EnvironmentDetails | null;
    createdAt: string | null;
    lastUsedAt: string | null;
}

export interface EnvironmentDetails {
    url: string;
    displayName: string;
    uniqueName: string | null;
    environmentId: string | null;
    organizationId: string | null;
    type: string | null;
    region: string | null;
}

export interface AuthSelectResponse {
    index: number;
    name: string | null;
    identity: string;
    environment: string | null;
}

// ── Environment ─────────────────────────────────────────────────────────────

export interface EnvListResponse {
    filter: string | null;
    environments: EnvironmentInfo[];
}

export interface EnvironmentInfo {
    id: string;
    environmentId: string | null;
    friendlyName: string;
    uniqueName: string;
    apiUrl: string;
    url: string | null;
    type: string | null;
    state: string;
    region: string | null;
    version: string | null;
    isActive: boolean;
    source: 'discovered' | 'configured' | 'both';
}

export interface EnvConfigRemoveResponse {
    removed: boolean;
}

export interface EnvSelectResponse {
    url: string;
    displayName: string;
    uniqueName: string | null;
    environmentId: string | null;
    resolutionMethod: string;
}

// ── Query ───────────────────────────────────────────────────────────────────

export interface QueryResultResponse {
    success: boolean;
    entityName: string | null;
    columns: QueryColumnInfo[];
    records: Record<string, unknown>[];
    count: number;
    totalCount: number | null;
    moreRecords: boolean;
    pagingCookie: string | null;
    pageNumber: number;
    isAggregate: boolean;
    executedFetchXml: string | null;
    executionTimeMs: number;
    queryMode: 'tds' | 'dataverse' | null;
    dataSources?: { label: string; isRemote: boolean }[];
    appliedHints?: string[];
}

export interface QueryColumnInfo {
    logicalName: string;
    alias: string | null;
    displayName: string | null;
    dataType: string;
    linkedEntityAlias: string | null;
}

// ── Profiles ────────────────────────────────────────────────────────────────

export interface ProfileCreateResponse {
    index: number;
    name: string | null;
    identity: string;
    authMethod: string;
    environment: string | null;
}

export interface ProfileDeleteResponse {
    deleted: boolean;
    profileName: string | null;
}

export interface ProfileRenameResponse {
    index: number;
    previousName: string | null;
    newName: string;
}

export interface ProfilesInvalidateResponse {
    profileName: string;
    invalidated: boolean;
}

// ── Solutions ───────────────────────────────────────────────────────────────

export interface SolutionsListResponse {
    solutions: SolutionInfoDto[];
}

export interface SolutionInfoDto {
    id: string;
    uniqueName: string;
    friendlyName: string;
    version: string | null;
    isManaged: boolean;
    publisherName: string | null;
    description: string | null;
    createdOn: string | null;
    modifiedOn: string | null;
    installedOn: string | null;
}

export interface SolutionComponentsResponse {
    solutionId: string;
    uniqueName: string;
    components: SolutionComponentInfoDto[];
}

export interface SolutionComponentInfoDto {
    id: string;
    objectId: string;
    componentType: number;
    componentTypeName: string;
    rootComponentBehavior: number;
    isMetadata: boolean;
    displayName?: string;
    logicalName?: string;
    schemaName?: string;
}

// ── Environment Config types ────────────────────────────────────────
export interface EnvConfigGetResponse {
    environmentUrl: string;
    label?: string;
    type?: string;
    color?: string;
    resolvedType: string;
    resolvedColor: string;
}

export interface EnvConfigSetResponse {
    environmentUrl: string;
    label?: string;
    type?: string;
    color?: string;
    saved: boolean;
}

// ── Query History ────────────────────────────────────────────────────────────

export interface QueryHistoryListResponse {
    entries: QueryHistoryEntryDto[];
}

export interface QueryHistoryEntryDto {
    id: string;
    sql: string;
    rowCount: number | null;
    executionTimeMs: number | null;
    environmentUrl: string | null;
    executedAt: string;
}

export interface QueryHistoryDeleteResponse {
    deleted: boolean;
}

// ── Export ───────────────────────────────────────────────────────────────────

export interface QueryExportResponse {
    content: string;
    format: string;
    rowCount: number;
}

// ── Explain ─────────────────────────────────────────────────────────────────

export interface QueryExplainResponse {
    plan: string;
    format: string;
    fetchXml?: string;
}

// ── IntelliSense / Completion types ─────────────────────────────────
export interface QueryCompleteResponse {
    items: CompletionItemDto[];
}

export interface CompletionItemDto {
    label: string;
    insertText: string;
    kind: string;
    detail: string | null;
    description: string | null;
    sortOrder: number;
}

// ── Environment Who ─────────────────────────────────────────────────────────

export interface EnvWhoResponse {
    organizationName: string;
    url: string;
    uniqueName: string;
    version: string;
    organizationId: string;
    userId: string;
    businessUnitId: string;
    connectedAs: string;
    environmentType: string | null;
}

// ── Schema ──────────────────────────────────────────────────────────────────

export interface SchemaEntitiesResponse {
    entities: EntitySummaryDto[];
}

export interface EntitySummaryDto {
    logicalName: string;
    displayName: string | null;
    isCustom: boolean;
}

export interface SchemaAttributesResponse {
    entityName: string;
    attributes: AttributeSummaryDto[];
}

export interface AttributeSummaryDto {
    logicalName: string;
    displayName: string | null;
    dataType: string;
    isCustom: boolean;
}

// ── Import Jobs ──────────────────────────────────────────────────────────────

export interface ImportJobsListResponse {
    jobs: ImportJobInfoDto[];
}

export interface ImportJobsGetResponse {
    job: ImportJobDetailDto;
}

export interface ImportJobInfoDto {
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

export interface ImportJobDetailDto extends ImportJobInfoDto {
    data: string | null;
}

// ── Plugin Traces ────────────────────────────────────────────────────────────

export interface PluginTracesListResponse {
    traces: PluginTraceInfoDto[];
}

export interface PluginTracesGetResponse {
    trace: PluginTraceDetailDto;
}

export interface PluginTracesTimelineResponse {
    nodes: TimelineNodeDto[];
}

export interface PluginTracesDeleteResponse {
    deletedCount: number;
}

export interface PluginTracesTraceLevelResponse {
    level: string;
    levelValue: number;
}

export interface PluginTracesSetTraceLevelResponse {
    success: boolean;
}

export interface PluginTraceInfoDto {
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

export interface PluginTraceDetailDto extends PluginTraceInfoDto {
    constructorDurationMs: number | null;
    executionStartTime: string | null;
    exceptionDetails: string | null;
    messageBlock: string | null;
    configuration: string | null;
    secureConfiguration: string | null;
    requestId: string | null;
}

export interface TimelineNodeDto {
    traceId: string;
    typeName: string;
    messageName: string | null;
    depth: number;
    durationMs: number | null;
    hasException: boolean;
    offsetPercent: number;
    widthPercent: number;
    hierarchyDepth: number;
    children: TimelineNodeDto[];
}

export interface TraceFilterDto {
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
