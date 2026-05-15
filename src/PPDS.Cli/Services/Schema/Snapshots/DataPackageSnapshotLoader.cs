using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Migration.Formats;

namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>
/// Builds a <see cref="SchemaSnapshot"/> from a CMT-format data package (zip
/// containing <c>data_schema.xml</c> or <c>schema.xml</c>).
/// </summary>
/// <remarks>
/// CMT schema files don't carry option-set values, so the resulting snapshot
/// has <see cref="SchemaSnapshot.IncludesOptionSetValues"/> = false and any
/// option-value diffs against it are suppressed by the comparison service.
/// </remarks>
public sealed class DataPackageSnapshotLoader : ISnapshotLoader
{
    private readonly string _packagePath;
    private readonly ICmtSchemaReader _schemaReader;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="packagePath">Path to a CMT data package zip.</param>
    /// <param name="schemaReader">Reader used to parse the embedded schema.xml.</param>
    public DataPackageSnapshotLoader(string packagePath, ICmtSchemaReader schemaReader)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path must be provided.", nameof(packagePath));
        }
        _packagePath = packagePath;
        _schemaReader = schemaReader ?? throw new ArgumentNullException(nameof(schemaReader));
    }

    /// <inheritdoc />
    public async Task<SchemaSnapshot> LoadAsync(
        IReadOnlyCollection<string>? entityFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_packagePath))
        {
            throw new PpdsException(
                ErrorCodes.Validation.FileNotFound,
                $"Data package not found: {_packagePath}");
        }

        try
        {
            await using var fileStream = new FileStream(
                _packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

            var schemaEntry = archive.GetEntry("data_schema.xml")
                ?? archive.GetEntry("schema.xml");

            if (schemaEntry is null)
            {
                throw new PpdsException(
                    ErrorCodes.Validation.SchemaInvalid,
                    "Data package does not contain a schema file (data_schema.xml or schema.xml).");
            }

            await using var schemaStream = schemaEntry.Open();
            using var buffer = new MemoryStream();
            await schemaStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            var migrationSchema = await _schemaReader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            var entities = migrationSchema.Entities
                .Where(e => entityFilter is null || entityFilter.Count == 0 ||
                            entityFilter.Contains(e.LogicalName, StringComparer.OrdinalIgnoreCase))
                .Select(e => new EntitySnapshot
                {
                    LogicalName = e.LogicalName,
                    DisplayName = e.DisplayName,
                    Attributes = e.Fields.Select(f => new AttributeSnapshot
                    {
                        LogicalName = f.LogicalName,
                        AttributeType = string.IsNullOrEmpty(f.Type) ? "unknown" : f.Type.ToLowerInvariant(),
                        RequiredLevel = f.IsRequired ? "ApplicationRequired" : "None",
                        MaxLength = f.MaxLength,
                        Precision = f.Precision,
                        LookupTargets = string.IsNullOrEmpty(f.LookupEntity)
                            ? null
                            : new[] { f.LookupEntity!.ToLowerInvariant() }
                    }).ToList(),
                    Relationships = e.Relationships.Select(r => new RelationshipSnapshot
                    {
                        SchemaName = r.Name,
                        RelationshipType = r.IsManyToMany ? "ManyToMany" : "OneToMany"
                    }).ToList()
                })
                .ToList();

            return new SchemaSnapshot
            {
                Source = $"data:{Path.GetFileName(_packagePath)}",
                Entities = entities,
                IncludesOptionSetValues = false
            };
        }
        catch (InvalidDataException ex)
        {
            throw new PpdsException(
                ErrorCodes.Validation.SchemaInvalid,
                $"Data package is not a valid zip archive: {_packagePath}",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException)
        {
            throw new PpdsException(
                ErrorCodes.Validation.SchemaInvalid,
                $"Failed to read schema from data package: {ex.Message}",
                ex);
        }
    }
}
