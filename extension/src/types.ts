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
}

export interface QueryColumnInfo {
    logicalName: string;
    alias: string | null;
    displayName: string | null;
    dataType: string;
    linkedEntityAlias: string | null;
}

// ── Profiles ────────────────────────────────────────────────────────────────

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
}

// ── Query History (future daemon endpoint) ──────────────────────────────────

export interface QueryHistoryEntry {
    sql: string;
    executedAt: string;
    rowCount: number;
    environmentUrl: string;
}

export interface QueryHistoryResponse {
    entries: QueryHistoryEntry[];
}

// ── Export (future daemon endpoint) ─────────────────────────────────────────

export interface ExportRequest {
    sql: string;
    format: 'csv' | 'tsv' | 'json';
    includeHeaders: boolean;
}

export interface ExportResponse {
    content: string;
    format: string;
    rowCount: number;
}
