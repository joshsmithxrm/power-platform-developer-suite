namespace PPDS.Plugins
{
    /// <summary>
    /// Specifies whether a Custom API parameter is an input or output parameter.
    /// </summary>
    public enum ParameterDirection
    {
        /// <summary>
        /// Input (0). The parameter is a request parameter passed into the Custom API.
        /// Use for values that the caller provides when invoking the API.
        /// </summary>
        Input = 0,

        /// <summary>
        /// Output (1). The parameter is a response parameter returned from the Custom API.
        /// Use for values that the API returns to the caller.
        /// </summary>
        Output = 1
    }
}
