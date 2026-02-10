using System;
using System.Globalization;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// Dataverse-specific functions evaluated client-side.
/// These functions are unique to the Power Platform / Dataverse ecosystem.
/// </summary>
public static class DataverseFunctions
{
    /// <summary>
    /// Registers all Dataverse-specific functions into the given registry.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        registry.Register("CREATEELASTICLOOKUP", new CreateElasticLookupFunction());
    }

    // ── CREATEELASTICLOOKUP ──────────────────────────────────────────
    /// <summary>
    /// CREATEELASTICLOOKUP(entity, logicalname, id, partitionid)
    /// Returns a formatted string suitable for creating elastic table lookup references.
    /// Format: entity:logicalname:id:partitionid
    /// </summary>
    private sealed class CreateElasticLookupFunction : IScalarFunction
    {
        public int MinArgs => 4;
        public int MaxArgs => 4;

        public object? Execute(object?[] args)
        {
            // Any null argument returns null (SQL NULL propagation)
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is null) return null;
            }

            var entity = Convert.ToString(args[0], CultureInfo.InvariantCulture);
            var logicalName = Convert.ToString(args[1], CultureInfo.InvariantCulture);
            var id = Convert.ToString(args[2], CultureInfo.InvariantCulture);
            var partitionId = Convert.ToString(args[3], CultureInfo.InvariantCulture);

            return $"{entity}:{logicalName}:{id}:{partitionId}";
        }
    }
}
