using System;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Resolves an environment from a collection by various criteria.
/// </summary>
public static class EnvironmentResolver
{
    /// <summary>
    /// Resolves an environment by identifier (name, URL, or ID).
    /// </summary>
    /// <param name="environments">The collection of environments to search.</param>
    /// <param name="identifier">The identifier to match (friendly name, unique name, URL, or ID).</param>
    /// <returns>The matching environment, or null if not found.</returns>
    /// <exception cref="AmbiguousMatchException">If multiple environments match.</exception>
    public static DiscoveredEnvironment? Resolve(
        IReadOnlyList<DiscoveredEnvironment> environments,
        string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        identifier = identifier.Trim();

        // Try exact match by ID first
        if (Guid.TryParse(identifier, out var guidId))
        {
            var byId = environments.FirstOrDefault(e => e.Id == guidId);
            if (byId != null)
                return byId;
        }

        // Try exact match by URL
        var byUrl = environments.FirstOrDefault(e =>
            string.Equals(e.ApiUrl, identifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Url, identifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.ApiUrl.TrimEnd('/'), identifier.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (byUrl != null)
            return byUrl;

        // Try exact match by unique name
        var byUniqueName = environments.FirstOrDefault(e =>
            string.Equals(e.UniqueName, identifier, StringComparison.OrdinalIgnoreCase));

        if (byUniqueName != null)
            return byUniqueName;

        // Try exact match by friendly name
        var byFriendlyName = environments.FirstOrDefault(e =>
            string.Equals(e.FriendlyName, identifier, StringComparison.OrdinalIgnoreCase));

        if (byFriendlyName != null)
            return byFriendlyName;

        // Try partial URL match (just the subdomain part)
        var byUrlPartial = environments.Where(e =>
            e.ApiUrl.Contains(identifier, StringComparison.OrdinalIgnoreCase) ||
            (e.UrlName != null && e.UrlName.Equals(identifier, StringComparison.OrdinalIgnoreCase))).ToList();

        if (byUrlPartial.Count == 1)
            return byUrlPartial[0];

        if (byUrlPartial.Count > 1)
        {
            throw new AmbiguousMatchException(
                $"Multiple environments match '{identifier}':\n" +
                string.Join("\n", byUrlPartial.Select(e => $"  - {e.FriendlyName} ({e.UniqueName})")));
        }

        // Try partial friendly name match
        var byFriendlyPartial = environments.Where(e =>
            e.FriendlyName.Contains(identifier, StringComparison.OrdinalIgnoreCase)).ToList();

        if (byFriendlyPartial.Count == 1)
            return byFriendlyPartial[0];

        if (byFriendlyPartial.Count > 1)
        {
            throw new AmbiguousMatchException(
                $"Multiple environments match '{identifier}':\n" +
                string.Join("\n", byFriendlyPartial.Select(e => $"  - {e.FriendlyName} ({e.UniqueName})")));
        }

        return null;
    }

    /// <summary>
    /// Resolves an environment by URL only.
    /// </summary>
    /// <param name="environments">The collection of environments to search.</param>
    /// <param name="url">The URL to match.</param>
    /// <returns>The matching environment, or null if not found.</returns>
    public static DiscoveredEnvironment? ResolveByUrl(
        IReadOnlyList<DiscoveredEnvironment> environments,
        string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim().TrimEnd('/');

        // Extract the host from the URL if it's a full URL
        string host;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            host = uri.Host.ToLowerInvariant();
        }
        else
        {
            host = url.ToLowerInvariant();
        }

        return environments.FirstOrDefault(e =>
        {
            if (string.IsNullOrWhiteSpace(e.ApiUrl))
                return false;

            if (Uri.TryCreate(e.ApiUrl, UriKind.Absolute, out var envUri))
            {
                return envUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        });
    }
}

/// <summary>
/// Exception thrown when multiple environments match a given identifier.
/// </summary>
public class AmbiguousMatchException : Exception
{
    /// <summary>
    /// Creates a new AmbiguousMatchException.
    /// </summary>
    public AmbiguousMatchException(string message) : base(message)
    {
    }
}
