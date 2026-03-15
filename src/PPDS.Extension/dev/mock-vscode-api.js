/**
 * Mock VS Code webview API for standalone browser development.
 *
 * Must be loaded BEFORE any panel script that calls acquireVsCodeApi().
 * Intercepts postMessage calls and simulates extension host responses.
 */
(function () {
    'use strict';

    const SAMPLE_COLUMNS = [
        { logicalName: 'accountid', displayName: 'Account Id' },
        { logicalName: 'name', displayName: 'Name' },
        { logicalName: 'emailaddress1', displayName: 'Email' },
        { logicalName: 'telephone1', displayName: 'Phone' },
        { logicalName: 'address1_city', displayName: 'City' },
        { logicalName: 'revenue', displayName: 'Revenue' },
        { logicalName: 'createdon', displayName: 'Created On' },
        { logicalName: 'statuscode', displayName: 'Status' },
    ];

    const SAMPLE_RECORDS = [
        { accountid: 'a1b2c3d4-0001', name: 'Contoso Ltd', emailaddress1: 'info@contoso.com', telephone1: '555-0100', address1_city: 'Redmond', revenue: 5000000, createdon: '2024-01-15T10:30:00Z', statuscode: { value: 1, formatted: 'Active' } },
        { accountid: 'a1b2c3d4-0002', name: 'Fabrikam Inc', emailaddress1: 'contact@fabrikam.com', telephone1: '555-0200', address1_city: 'Seattle', revenue: 12000000, createdon: '2024-02-20T14:15:00Z', statuscode: { value: 1, formatted: 'Active' } },
        { accountid: 'a1b2c3d4-0003', name: 'Northwind Traders', emailaddress1: 'sales@northwind.com', telephone1: '555-0300', address1_city: 'Portland', revenue: 800000, createdon: '2024-03-10T09:00:00Z', statuscode: { value: 2, formatted: 'Inactive' } },
        { accountid: 'a1b2c3d4-0004', name: 'Adventure Works', emailaddress1: 'hello@adventureworks.com', telephone1: '555-0400', address1_city: 'San Francisco', revenue: 3200000, createdon: '2024-04-05T16:45:00Z', statuscode: { value: 1, formatted: 'Active' } },
        { accountid: 'a1b2c3d4-0005', name: 'Wide World Importers', emailaddress1: 'info@wideworldimporters.com', telephone1: '555-0500', address1_city: 'Chicago', revenue: 1500000, createdon: '2024-05-12T11:20:00Z', statuscode: { value: 1, formatted: 'Active' } },
        { accountid: 'a1b2c3d4-0006', name: 'Tailspin Toys', emailaddress1: 'support@tailspintoys.com', telephone1: '555-0600', address1_city: 'Dallas', revenue: 750000, createdon: '2024-06-18T08:00:00Z', statuscode: { value: 2, formatted: 'Inactive' } },
        { accountid: 'a1b2c3d4-0007', name: 'Datum Corporation', emailaddress1: 'info@datum.com', telephone1: '555-0700', address1_city: 'New York', revenue: 9800000, createdon: '2024-07-22T13:30:00Z', statuscode: { value: 1, formatted: 'Active' } },
        { accountid: 'a1b2c3d4-0008', name: 'Litware Inc', emailaddress1: 'contact@litware.com', telephone1: '555-0800', address1_city: 'Boston', revenue: 2100000, createdon: '2024-08-01T15:10:00Z', statuscode: { value: 1, formatted: 'Active' } },
    ];

    let _state = {};

    function createMockApi() {
        return {
            postMessage(message) {
                console.log('[mock-vscode-api] postMessage:', message);
                handleMessage(message);
            },
            getState() {
                return _state;
            },
            setState(newState) {
                _state = newState;
            },
        };
    }

    function handleMessage(message) {
        switch (message.command) {
            case 'ready':
                // Simulate extension host sending environment info after ready
                setTimeout(() => {
                    window.postMessage({ command: 'updateEnvironment', name: 'contoso-dev (mock)' }, '*');
                }, 100);
                break;

            case 'executeQuery':
                console.log('[mock-vscode-api] Executing query:', message.sql);
                // Simulate executionStarted then queryResult
                window.postMessage({ command: 'executionStarted' }, '*');
                setTimeout(() => {
                    window.postMessage({
                        command: 'queryResult',
                        data: {
                            columns: SAMPLE_COLUMNS,
                            records: SAMPLE_RECORDS,
                            moreRecords: true,
                            pagingCookie: 'mock-paging-cookie-page-1',
                            executionTimeMs: 142,
                        },
                    }, '*');
                }, 300);
                break;

            case 'loadMore':
                console.log('[mock-vscode-api] Loading more, page:', message.page);
                window.postMessage({ command: 'executionStarted' }, '*');
                setTimeout(() => {
                    window.postMessage({
                        command: 'appendResults',
                        data: {
                            columns: SAMPLE_COLUMNS,
                            records: [
                                { accountid: 'a1b2c3d4-0009', name: 'Proseware Inc', emailaddress1: 'info@proseware.com', telephone1: '555-0900', address1_city: 'Austin', revenue: 4300000, createdon: '2024-09-10T10:00:00Z', statuscode: { value: 1, formatted: 'Active' } },
                                { accountid: 'a1b2c3d4-0010', name: 'Coho Vineyard', emailaddress1: 'wine@cohovineyard.com', telephone1: '555-1000', address1_city: 'Napa', revenue: 600000, createdon: '2024-10-05T12:30:00Z', statuscode: { value: 1, formatted: 'Active' } },
                            ],
                            moreRecords: false,
                            pagingCookie: null,
                            executionTimeMs: 87,
                        },
                    }, '*');
                }, 300);
                break;

            case 'showFetchXml':
            case 'explainQuery':
            case 'exportResults':
            case 'showHistory':
            case 'openInNotebook':
            case 'requestEnvironmentList':
            case 'copyToClipboard':
                console.log('[mock-vscode-api] No-op for command:', message.command);
                break;

            default:
                console.log('[mock-vscode-api] Unhandled command:', message.command);
        }
    }

    // Expose acquireVsCodeApi globally (same contract as the real VS Code webview)
    let _api = null;
    window.acquireVsCodeApi = function () {
        if (_api) return _api;
        _api = createMockApi();
        return _api;
    };

    console.log('[mock-vscode-api] Mock VS Code API installed');
})();
