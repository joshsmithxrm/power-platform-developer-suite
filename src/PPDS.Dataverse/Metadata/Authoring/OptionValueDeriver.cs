using System.Collections.Generic;
using System.Linq;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Pure helper that picks a numeric option value for status-reason add and local-column option-add.
/// Single source of truth for both surfaces — identical collision/prefix semantics.
/// </summary>
public static class OptionValueDeriver
{
    /// <summary>
    /// Chooses an option value. Throws <see cref="MetadataValidationException"/> on collision or missing inputs.
    /// </summary>
    /// <param name="explicitValue">--value if supplied (wins over derivation).</param>
    /// <param name="publisherOptionPrefix">Publisher customizationoptionvalueprefix, if --solution was given.</param>
    /// <param name="existingValues">Values already on the target option set.</param>
    /// <returns>The chosen option value.</returns>
    public static int Derive(int? explicitValue, int? publisherOptionPrefix, IReadOnlyCollection<int> existingValues)
    {
        if (explicitValue.HasValue)
        {
            if (existingValues.Contains(explicitValue.Value))
                throw new MetadataValidationException(
                    MetadataErrorCodes.DuplicateOptionValue,
                    $"Option value {explicitValue.Value} already exists on the target option set.",
                    "Value");
            return explicitValue.Value;
        }

        if (publisherOptionPrefix.HasValue)
        {
            var candidate = publisherOptionPrefix.Value * 10_000;
            while (existingValues.Contains(candidate))
                candidate++;
            return candidate;
        }

        throw new MetadataValidationException(
            MetadataErrorCodes.MissingRequiredField,
            "Provide --value or --solution to determine the option value.",
            "Value");
    }
}
