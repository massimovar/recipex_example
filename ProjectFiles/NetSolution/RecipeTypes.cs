using System;
using System.Collections.Generic;
using FTOptix.Alarm;

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
/// Structured result returned by all recipe operations.
/// </summary>
public class RecipeOperationResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; }
    public string Message { get; set; }
    public List<string> ValidationErrors { get; set; } = new List<string>();
    public List<string> Warnings { get; set; } = new List<string>();

    public static RecipeOperationResult Ok(string message = null)
    {
        return new RecipeOperationResult { Success = true, Message = message ?? "OK" };
    }

    public static RecipeOperationResult Fail(string errorCode, string message)
    {
        return new RecipeOperationResult { Success = false, ErrorCode = errorCode, Message = message };
    }

    public static RecipeOperationResult FailValidation(string errorCode, string message, List<string> errors)
    {
        return new RecipeOperationResult
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            ValidationErrors = errors ?? new List<string>()
        };
    }
}

/// <summary>
/// Result of recipe validation.
/// </summary>
public class RecipeValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new List<string>();
    public List<string> Warnings { get; set; } = new List<string>();

    public static RecipeValidationResult Valid() => new RecipeValidationResult { IsValid = true };

    public static RecipeValidationResult Invalid(List<string> errors, List<string> warnings = null)
    {
        return new RecipeValidationResult
        {
            IsValid = false,
            Errors = errors ?? new List<string>(),
            Warnings = warnings ?? new List<string>()
        };
    }
}

/// <summary>
/// Result of test recipe generation.
/// </summary>
public class TestRecipeGenerationResult
{
    public bool Success { get; set; }
    public int TotalRequested { get; set; }
    public int CreatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> GeneratedRecipeNames { get; set; } = new List<string>();
    public List<string> Errors { get; set; } = new List<string>();
}

/// <summary>
/// Result of bulk archive/delete operations.
/// </summary>
public class BulkOperationResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; }
    public int CandidateCount { get; set; }
    public int AffectedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> AffectedRecipeNames { get; set; } = new List<string>();
    public List<string> Errors { get; set; } = new List<string>();
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
