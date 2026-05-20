using System;
using System.Collections.Generic;
using FTOptix.Alarm;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.Recipe;

/// <summary>
/// Recipe lifecycle statuses. Maps to RecipeX metadata "Status" field.
/// </summary>
public enum RecipeStatuses
{
    Template = 0,
    Draft = 1,
    Prepared = 2,
    Approved = 3,
    Released = 4,
    Archived = 5
}

/// <summary>
/// Parsed recipe version with major.minor structure.
/// </summary>
public struct RecipeVersion
{
    public int Major { get; set; }
    public int Minor { get; set; }

    public RecipeVersion(int major, int minor)
    {
        Major = major;
        Minor = minor;
    }

    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>
    /// Parse "major.minor" string. Returns false if format is invalid.
    /// </summary>
    public static bool TryParse(string versionString, out RecipeVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(versionString))
            return false;

        var parts = versionString.Split('.');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return false;

        if (major < 0 || minor < 0)
            return false;

        version = new RecipeVersion(major, minor);
        return true;
    }

    /// <summary>
    /// Increment minor version (for non-released revisions).
    /// </summary>
    public RecipeVersion IncrementMinor() => new RecipeVersion(Major, Minor + 1);

    /// <summary>
    /// Increment major version, reset minor (for released->new revision).
    /// </summary>
    public RecipeVersion IncrementMajor() => new RecipeVersion(Major + 1, 0);
}
