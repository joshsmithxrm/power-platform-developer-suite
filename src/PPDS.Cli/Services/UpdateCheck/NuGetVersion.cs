namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Immutable SemVer value type for parsing and comparing NuGet version strings.
/// Handles the <c>major.minor.patch[-prerelease][+buildmetadata]</c> format,
/// including the <c>InformationalVersion</c> format emitted by MinVer
/// (e.g., <c>1.2.3-beta.1+abc1234</c>).
/// </summary>
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
{
    /// <summary>Gets the major version component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version component.</summary>
    public int Patch { get; }

    /// <summary>
    /// Gets the pre-release label (everything between <c>-</c> and <c>+</c>).
    /// Returns <see cref="string.Empty"/> for stable releases.
    /// </summary>
    public string PreReleaseLabel { get; }

    /// <summary>Gets a value indicating whether this version is a pre-release.</summary>
    public bool IsPreRelease => PreReleaseLabel.Length > 0;

    /// <summary>
    /// Gets a value indicating whether the minor version is odd.
    /// PPDS convention: odd minor versions denote a pre-release/development line
    /// (e.g., 1.1.x is the development line between stable 1.0.x and 1.2.x).
    /// </summary>
    public bool IsOddMinor => Minor % 2 != 0;

    private NuGetVersion(int major, int minor, int patch, string preReleaseLabel)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreReleaseLabel = preReleaseLabel;
    }

    /// <summary>
    /// Parses a NuGet version string. Build metadata (<c>+…</c>) is stripped before parsing.
    /// </summary>
    /// <param name="version">The version string to parse.</param>
    /// <returns>The parsed <see cref="NuGetVersion"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="version"/> is not a valid SemVer string.</exception>
    public static NuGetVersion Parse(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (!TryParseCore(version, out var result))
            throw new FormatException($"'{version}' is not a valid NuGet/SemVer version string.");

        return result!;
    }

    /// <summary>
    /// Attempts to parse a NuGet version string.
    /// </summary>
    /// <param name="version">The version string to parse, or <see langword="null"/>.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, contains the parsed <see cref="NuGetVersion"/>;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? version, out NuGetVersion? result)
    {
        if (version is null)
        {
            result = null;
            return false;
        }

        return TryParseCore(version, out result);
    }

    private static bool TryParseCore(string version, out NuGetVersion? result)
    {
        result = null;

        if (string.IsNullOrEmpty(version))
            return false;

        // Strip build metadata (everything from '+' onwards)
        var plusIndex = version.IndexOf('+');
        var withoutMeta = plusIndex >= 0 ? version[..plusIndex] : version;

        // Split on the first '-' to separate core from pre-release label
        var dashIndex = withoutMeta.IndexOf('-');
        string core;
        string preRelease;

        if (dashIndex >= 0)
        {
            core = withoutMeta[..dashIndex];
            preRelease = withoutMeta[(dashIndex + 1)..];
        }
        else
        {
            core = withoutMeta;
            preRelease = string.Empty;
        }

        // Parse major.minor.patch
        var parts = core.Split('.');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
            return false;

        // SemVer requires non-negative integers
        if (major < 0 || minor < 0 || patch < 0)
            return false;

        result = new NuGetVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(NuGetVersion? other)
    {
        if (other is null) return 1;

        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // Same major.minor.patch: stable beats pre-release
        if (!IsPreRelease && !other.IsPreRelease) return 0;   // both stable
        if (!IsPreRelease) return 1;                           // this stable, other pre-release
        if (!other.IsPreRelease) return -1;                    // this pre-release, other stable

        // Both are pre-release: compare labels segment by segment
        return ComparePreReleaseLabels(PreReleaseLabel, other.PreReleaseLabel);
    }

    /// <summary>
    /// Compares two pre-release labels segment by segment per SemVer 2.0.0 spec:
    /// <list type="bullet">
    ///   <item>Segments are split on <c>.</c></item>
    ///   <item>Numeric segments are compared as integers.</item>
    ///   <item>Alphanumeric segments are compared lexicographically (ordinal).</item>
    ///   <item>Numeric segments always sort before alphanumeric segments.</item>
    ///   <item>A longer label with a common prefix is greater than a shorter one.</item>
    /// </list>
    /// </summary>
    private static int ComparePreReleaseLabels(string a, string b)
    {
        var aSegments = a.Split('.');
        var bSegments = b.Split('.');

        var length = Math.Min(aSegments.Length, bSegments.Length);

        for (var i = 0; i < length; i++)
        {
            var aIsNum = int.TryParse(aSegments[i], out var aNum);
            var bIsNum = int.TryParse(bSegments[i], out var bNum);

            int cmp;
            if (aIsNum && bIsNum)
            {
                // Both numeric: integer comparison
                cmp = aNum.CompareTo(bNum);
            }
            else if (!aIsNum && !bIsNum)
            {
                // Both alphanumeric: lexicographic (ordinal)
                cmp = string.Compare(aSegments[i], bSegments[i], StringComparison.Ordinal);
            }
            else
            {
                // Mixed: numeric < alphanumeric (per SemVer spec)
                cmp = aIsNum ? -1 : 1;
            }

            if (cmp != 0) return cmp;
        }

        // Common prefix — more segments wins
        return aSegments.Length.CompareTo(bSegments.Length);
    }

    /// <inheritdoc/>
    public bool Equals(NuGetVersion? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Major == other.Major
            && Minor == other.Minor
            && Patch == other.Patch
            && string.Equals(PreReleaseLabel, other.PreReleaseLabel, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as NuGetVersion);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, PreReleaseLabel);

    /// <summary>
    /// Returns the canonical string representation: <c>major.minor.patch</c> for stable versions,
    /// or <c>major.minor.patch-prerelease</c> for pre-release versions.
    /// Build metadata is never included.
    /// </summary>
    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreReleaseLabel}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> equals <paramref name="right"/> by value.</summary>
    public static bool operator ==(NuGetVersion? left, NuGetVersion? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> does not equal <paramref name="right"/> by value.</summary>
    public static bool operator !=(NuGetVersion? left, NuGetVersion? right) =>
        !(left == right);

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    public static bool operator >(NuGetVersion? left, NuGetVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    public static bool operator <(NuGetVersion? left, NuGetVersion? right) =>
        right is not null && right.CompareTo(left) > 0;

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(NuGetVersion? left, NuGetVersion? right) =>
        left is not null && left.CompareTo(right) >= 0;

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(NuGetVersion? left, NuGetVersion? right) =>
        right is not null && right.CompareTo(left) >= 0;
}
