using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace PPDS.Query.Provider;

/// <summary>
/// A collection of <see cref="PpdsDbParameter"/> instances for a <see cref="PpdsDbCommand"/>.
/// Standard ADO.NET parameter collection backed by a <see cref="List{T}"/>.
/// </summary>
public sealed class PpdsDbParameterCollection : DbParameterCollection
{
    private readonly List<PpdsDbParameter> _parameters = new();

    /// <inheritdoc />
    public override int Count => _parameters.Count;

    /// <inheritdoc />
    public override object SyncRoot { get; } = new object();

    /// <inheritdoc />
    public override int Add(object value)
    {
        if (value is not PpdsDbParameter param)
            throw new ArgumentException($"Only {nameof(PpdsDbParameter)} instances can be added.", nameof(value));

        _parameters.Add(param);
        return _parameters.Count - 1;
    }

    /// <summary>
    /// Adds a <see cref="PpdsDbParameter"/> to the collection and returns it.
    /// </summary>
    /// <param name="parameter">The parameter to add.</param>
    /// <returns>The added parameter.</returns>
    public PpdsDbParameter Add(PpdsDbParameter parameter)
    {
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));

        _parameters.Add(parameter);
        return parameter;
    }

    /// <summary>
    /// Creates a new parameter with the specified name and value, adds it to the collection,
    /// and returns it.
    /// </summary>
    /// <param name="parameterName">The name of the parameter.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>The newly created parameter.</returns>
    public PpdsDbParameter AddWithValue(string parameterName, object? value)
    {
        var parameter = new PpdsDbParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    /// <inheritdoc />
    public override void AddRange(Array values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    /// <inheritdoc />
    public override void Clear() => _parameters.Clear();

    /// <inheritdoc />
    public override bool Contains(object value)
    {
        return value is PpdsDbParameter param && _parameters.Contains(param);
    }

    /// <inheritdoc />
    public override bool Contains(string value)
    {
        return _parameters.Any(p =>
            string.Equals(p.ParameterName, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public override void CopyTo(Array array, int index)
    {
        ((ICollection)_parameters).CopyTo(array, index);
    }

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    /// <inheritdoc />
    public override int IndexOf(object value)
    {
        return value is PpdsDbParameter param ? _parameters.IndexOf(param) : -1;
    }

    /// <inheritdoc />
    public override int IndexOf(string parameterName)
    {
        for (var i = 0; i < _parameters.Count; i++)
        {
            if (string.Equals(_parameters[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <inheritdoc />
    public override void Insert(int index, object value)
    {
        if (value is not PpdsDbParameter param)
            throw new ArgumentException($"Only {nameof(PpdsDbParameter)} instances can be inserted.", nameof(value));

        _parameters.Insert(index, param);
    }

    /// <inheritdoc />
    public override void Remove(object value)
    {
        if (value is PpdsDbParameter param)
            _parameters.Remove(param);
    }

    /// <inheritdoc />
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    /// <inheritdoc />
    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            _parameters.RemoveAt(index);
    }

    /// <inheritdoc />
    protected override DbParameter GetParameter(int index) => _parameters[index];

    /// <inheritdoc />
    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        return _parameters[index];
    }

    /// <inheritdoc />
    protected override void SetParameter(int index, DbParameter value)
    {
        if (value is not PpdsDbParameter param)
            throw new ArgumentException($"Only {nameof(PpdsDbParameter)} instances can be set.", nameof(value));

        _parameters[index] = param;
    }

    /// <inheritdoc />
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        if (value is not PpdsDbParameter param)
            throw new ArgumentException($"Only {nameof(PpdsDbParameter)} instances can be set.", nameof(value));

        var index = IndexOf(parameterName);
        if (index < 0)
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));

        _parameters[index] = param;
    }

    /// <summary>
    /// Gets the list of parameters for internal use by <see cref="PpdsDbCommand"/>.
    /// </summary>
    internal IReadOnlyList<PpdsDbParameter> InternalList => _parameters;
}
