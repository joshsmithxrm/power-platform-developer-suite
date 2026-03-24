namespace PPDS.Plugins
{
    /// <summary>
    /// Specifies the data type of a Custom API request or response parameter.
    /// These values correspond to the Dataverse Custom API parameter types.
    /// </summary>
    public enum ApiParameterType
    {
        /// <summary>
        /// Boolean (0). A true/false value.
        /// Maps to the Dataverse Boolean type.
        /// </summary>
        Boolean = 0,

        /// <summary>
        /// DateTime (1). A date and time value.
        /// Maps to the Dataverse DateTime type.
        /// </summary>
        DateTime = 1,

        /// <summary>
        /// Decimal (2). A high-precision decimal number.
        /// Maps to the Dataverse Decimal type.
        /// </summary>
        Decimal = 2,

        /// <summary>
        /// Entity (3). A single entity record.
        /// Maps to the Dataverse Entity type.
        /// </summary>
        Entity = 3,

        /// <summary>
        /// Entity collection (4). A collection of entity records.
        /// Maps to the Dataverse EntityCollection type.
        /// </summary>
        EntityCollection = 4,

        /// <summary>
        /// Entity reference (5). A reference to a single entity record by logical name and ID.
        /// Maps to the Dataverse EntityReference type.
        /// </summary>
        EntityReference = 5,

        /// <summary>
        /// Float (6). A floating-point number.
        /// Maps to the Dataverse Float type.
        /// </summary>
        Float = 6,

        /// <summary>
        /// Integer (7). A whole number.
        /// Maps to the Dataverse Integer type.
        /// </summary>
        Integer = 7,

        /// <summary>
        /// Money (8). A monetary value with currency information.
        /// Maps to the Dataverse Money type.
        /// </summary>
        Money = 8,

        /// <summary>
        /// Picklist (9). An option set value.
        /// Maps to the Dataverse OptionSetValue type.
        /// </summary>
        Picklist = 9,

        /// <summary>
        /// String (10). A text value.
        /// Maps to the Dataverse String type.
        /// </summary>
        String = 10,

        /// <summary>
        /// String array (11). A collection of text values.
        /// Maps to the Dataverse StringArray type.
        /// </summary>
        StringArray = 11,

        /// <summary>
        /// Guid (12). A globally unique identifier.
        /// Maps to the Dataverse Guid type.
        /// </summary>
        Guid = 12
    }
}
