using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PPDS.Migration.Models;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Reads user mapping files.
    /// </summary>
    public class UserMappingReader : IUserMappingReader
    {
        private readonly ILogger<UserMappingReader>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserMappingReader"/> class.
        /// </summary>
        public UserMappingReader()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UserMappingReader"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public UserMappingReader(ILogger<UserMappingReader> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<UserMappingCollection> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"User mapping file not found: {path}", path);
            }

            _logger?.LogInformation("Reading user mappings from {Path}", path);

#if NET8_0_OR_GREATER
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
#else
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
#endif
            return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<UserMappingCollection> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

#if NET8_0_OR_GREATER
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
#else
            var doc = XDocument.Load(stream, LoadOptions.None);
            await Task.CompletedTask;
#endif

            var collection = ParseMappings(doc);

            _logger?.LogInformation("Loaded {Count} user mappings", collection.Mappings.Count);

            return collection;
        }

        private UserMappingCollection ParseMappings(XDocument doc)
        {
            var root = doc.Root ?? throw new InvalidOperationException("User mapping XML has no root element");
            var collection = new UserMappingCollection();

            // Parse default settings
            var defaultUserAttr = root.Attribute("defaultUserId")?.Value;
            if (!string.IsNullOrEmpty(defaultUserAttr) && Guid.TryParse(defaultUserAttr, out var defaultUserId))
            {
                collection.DefaultUserId = defaultUserId;
            }

            var useCurrentAttr = root.Attribute("useCurrentUserAsDefault")?.Value;
            if (!string.IsNullOrEmpty(useCurrentAttr))
            {
                collection.UseCurrentUserAsDefault =
                    useCurrentAttr.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    useCurrentAttr == "1";
            }

            // Parse mappings
            foreach (var mappingElement in root.Elements("mapping"))
            {
                var sourceIdAttr = mappingElement.Attribute("sourceId")?.Value;
                var targetIdAttr = mappingElement.Attribute("targetId")?.Value;

                if (string.IsNullOrEmpty(sourceIdAttr) || !Guid.TryParse(sourceIdAttr, out var sourceId))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(targetIdAttr) || !Guid.TryParse(targetIdAttr, out var targetId))
                {
                    continue;
                }

                collection.Mappings[sourceId] = new UserMapping
                {
                    SourceUserId = sourceId,
                    SourceUserName = mappingElement.Attribute("sourceName")?.Value,
                    TargetUserId = targetId,
                    TargetUserName = mappingElement.Attribute("targetName")?.Value
                };
            }

            return collection;
        }
    }

    /// <summary>
    /// Interface for reading user mappings.
    /// </summary>
    public interface IUserMappingReader
    {
        /// <summary>
        /// Reads user mappings from a file.
        /// </summary>
        Task<UserMappingCollection> ReadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads user mappings from a stream.
        /// </summary>
        Task<UserMappingCollection> ReadAsync(Stream stream, CancellationToken cancellationToken = default);
    }
}
