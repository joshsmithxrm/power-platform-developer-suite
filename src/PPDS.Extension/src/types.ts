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
    warnings?: string[];
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
    totalCount: number;
    filtersApplied: string[];
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
    isVisible: boolean;
    isApiManaged: boolean;
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

// ── Metadata ─────────────────────────────────────────────────────────────────

export interface MetadataEntitiesResponse {
    entities: MetadataEntitySummaryDto[];
    intersectHiddenCount: number;
}

export interface MetadataGlobalChoiceSummaryDto {
    name: string;
    displayName: string;
    optionSetType: string;
    isCustomOptionSet: boolean;
    isManaged: boolean;
    optionCount: number;
    description: string | null;
}

export interface MetadataGlobalOptionSetsResponse {
    optionSets: MetadataGlobalChoiceSummaryDto[];
}

export interface MetadataGlobalOptionSetDetailResponse {
    optionSet: MetadataOptionSetDto;
}

export interface MetadataEntitySummaryDto {
    logicalName: string;
    schemaName: string;
    displayName: string;
    isCustomEntity: boolean;
    isManaged: boolean;
    ownershipType: string | null;
    objectTypeCode: number;
    description: string | null;
}

export interface MetadataEntityResponse {
    entity: MetadataEntityDetailDto;
}

export interface MetadataEntityDetailDto extends MetadataEntitySummaryDto {
    primaryIdAttribute: string | null;
    primaryNameAttribute: string | null;
    entitySetName: string | null;
    isActivity: boolean;
    attributes: MetadataAttributeDto[];
    oneToManyRelationships: MetadataRelationshipDto[];
    manyToOneRelationships: MetadataRelationshipDto[];
    manyToManyRelationships: MetadataManyToManyDto[];
    keys: MetadataKeyDto[];
    privileges: MetadataPrivilegeDto[];
    globalOptionSets: MetadataOptionSetDto[];
}

export interface MetadataAttributeDto {
    logicalName: string;
    displayName: string | null;
    schemaName: string | null;
    attributeType: string;
    attributeTypeName: string | null;
    isPrimaryId: boolean;
    isPrimaryName: boolean;
    isCustomAttribute: boolean;
    requiredLevel: string | null;
    maxLength: number | null;
    minValue: number | null;
    maxValue: number | null;
    precision: number | null;
    targets: string[] | null;
    optionSetName: string | null;
    isGlobalOptionSet: boolean;
    options: MetadataOptionValueDto[] | null;
    format: string | null;
    dateTimeBehavior: string | null;
    sourceType: number | null;
    isSecured: boolean;
    description: string | null;
    autoNumberFormat: string | null;
}

export interface MetadataRelationshipDto {
    schemaName: string;
    relationshipType: string;
    referencedEntity: string | null;
    referencedAttribute: string | null;
    referencingEntity: string | null;
    referencingAttribute: string | null;
    cascadeAssign: string | null;
    cascadeDelete: string | null;
    cascadeMerge: string | null;
    cascadeReparent: string | null;
    cascadeShare: string | null;
    cascadeUnshare: string | null;
    isHierarchical: boolean;
}

export interface MetadataManyToManyDto {
    schemaName: string;
    entity1LogicalName: string | null;
    entity1IntersectAttribute: string | null;
    entity2LogicalName: string | null;
    entity2IntersectAttribute: string | null;
    intersectEntityName: string | null;
}

export interface MetadataKeyDto {
    schemaName: string;
    logicalName: string;
    displayName: string | null;
    keyAttributes: string[];
    entityKeyIndexStatus: string | null;
    isManaged: boolean;
}

export interface MetadataPrivilegeDto {
    privilegeId: string;
    name: string;
    privilegeType: string;
    canBeLocal: boolean;
    canBeDeep: boolean;
    canBeGlobal: boolean;
    canBeBasic: boolean;
}

export interface MetadataOptionSetDto {
    name: string;
    displayName: string | null;
    optionSetType: string;
    isGlobal: boolean;
    options: MetadataOptionValueDto[];
}

export interface MetadataOptionValueDto {
    value: number;
    label: string;
    color: string | null;
    description: string | null;
}

// ── Metadata Authoring ──────────────────────────────────────────────────────

export interface MetadataAuthoringResult {
    success: boolean;
    logicalName?: string;
    metadataId?: string;
    wasDryRun: boolean;
    error?: string;
    errorCode?: string;
    validationMessages?: MetadataValidationMessageDto[];
}

export interface MetadataDeleteResult {
    success: boolean;
    dependencies?: MetadataDependencyDto[];
    dependencyCount: number;
    error?: string;
    errorCode?: string;
}

export interface MetadataValidationMessageDto {
    field: string;
    rule: string;
    message: string;
}

export interface MetadataDependencyDto {
    dependentComponentType: string;
    dependentComponentName: string;
    dependentComponentSchemaName?: string;
}

// ── Import Jobs ──────────────────────────────────────────────────────────────

export interface ImportJobsListResponse {
    jobs: ImportJobInfoDto[];
    totalCount: number;
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
    operationContext: string | null;
}

export interface ImportJobDetailDto extends ImportJobInfoDto {
    data: string | null;
}

// ── Plugin Traces ────────────────────────────────────────────────────────────

export interface PluginTracesListResponse {
    traces: PluginTraceInfoDto[];
    totalCount?: number;
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
    // Additional fields (PT-01 through PT-09)
    stage?: string | null;
    constructorStartTime?: string | null;
    isSystemCreated?: boolean;
    createdById?: string | null;
    createdOnBehalfById?: string | null;
    pluginStepId?: string | null;
    persistenceKey?: string | null;
    organizationId?: string | null;
    profile?: string | null;
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

// ── Connection References ───────────────────────────────────────────────

export interface ConnectionReferencesListResponse {
    references: ConnectionReferenceInfoDto[];
    totalCount: number;
    filtersApplied: string[] | null;
}

export interface ConnectionReferencesGetResponse {
    reference: ConnectionReferenceDetailDto;
}

export interface ConnectionReferencesAnalyzeResponse {
    orphanedReferences: OrphanedReferenceDto[];
    orphanedFlows: OrphanedFlowDto[];
    totalReferences: number;
    totalFlows: number;
}

export interface ConnectionReferenceInfoDto {
    logicalName: string;
    displayName: string | null;
    connectorId: string | null;
    connectionId: string | null;
    isManaged: boolean;
    modifiedOn: string | null;
    connectionStatus: string;
    connectorDisplayName: string | null;
}

export interface ConnectionReferenceDetailDto extends ConnectionReferenceInfoDto {
    description: string | null;
    isBound: boolean;
    createdOn: string | null;
    flows: FlowReferenceDto[];
    connectionOwner: string | null;
    connectionIsShared: boolean | null;
}

export interface FlowReferenceDto {
    flowId: string;
    uniqueName: string;
    displayName: string | null;
    state: string | null;
}

export interface OrphanedReferenceDto {
    logicalName: string;
    displayName: string | null;
    connectorId: string | null;
}

export interface OrphanedFlowDto {
    uniqueName: string;
    displayName: string | null;
    missingReference: string | null;
}

// ── Environment Variables ───────────────────────────────────────────────

export interface EnvironmentVariablesListResponse {
    variables: EnvironmentVariableInfoDto[];
    totalCount: number;
    filtersApplied?: string[];
}

export interface SyncStatisticsDto {
    added: number;
    removed: number;
    preserved: number;
}

export interface EnvironmentVariablesSyncDeploymentSettingsResponse {
    filePath: string;
    environmentVariables: SyncStatisticsDto;
    connectionReferences: SyncStatisticsDto;
}

export interface EnvironmentVariablesGetResponse {
    variable: EnvironmentVariableDetailDto;
}

export interface EnvironmentVariablesSetResponse {
    success: boolean;
}

export interface EnvironmentVariableInfoDto {
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

export interface EnvironmentVariableDetailDto extends EnvironmentVariableInfoDto {
    description: string | null;
    createdOn: string | null;
}

// ── Web Resources ──────────────────────────────────────────────────────────

export interface WebResourceInfoDto {
    id: string;
    name: string;
    displayName?: string;
    type: number;
    typeName: string;
    fileExtension: string;
    isManaged: boolean;
    isTextType: boolean;
    createdBy?: string;
    createdOn?: string;
    modifiedBy?: string;
    modifiedOn?: string;
}

export interface WebResourceDetailDto {
    id: string;
    name: string;
    webResourceType: number;
    content?: string;
    modifiedOn?: string;
}
