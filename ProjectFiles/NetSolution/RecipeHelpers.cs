using System;
using System.Collections.Generic;
using System.Linq;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.Alarm;
using OpcUa = UAManagedCore.OpcUa;

/// <summary>
/// Central helper methods for recipe lifecycle management.
/// Stateless utilities — no instance state required.
/// </summary>
public static class RecipeHelpers
{
    // Metadata field names used in RecipeX store
    // Note: Version is stored natively in RecipeId.Version (Recipes.Version DB column), not as metadata
    // Note: CreatedAt is a native Recipes DB column — no custom metadata needed for creation timestamp
    public const string MetadataStatus = "Status";

    // Valid forward transitions: key=from, value=allowed targets
    private static readonly Dictionary<RecipeStatuses, RecipeStatuses[]> AllowedTransitions = new Dictionary<RecipeStatuses, RecipeStatuses[]>
    {
        { RecipeStatuses.Template, new[] { RecipeStatuses.Draft, RecipeStatuses.Archived } },
        { RecipeStatuses.Draft, new[] { RecipeStatuses.Prepared, RecipeStatuses.Archived } },
        { RecipeStatuses.Prepared, new[] { RecipeStatuses.Approved, RecipeStatuses.Archived } },
        { RecipeStatuses.Approved, new[] { RecipeStatuses.Released, RecipeStatuses.Archived } },
        { RecipeStatuses.Released, new[] { RecipeStatuses.Archived } },
        { RecipeStatuses.Archived, Array.Empty<RecipeStatuses>() }
    };

    // Statuses that allow direct editing
    private static readonly HashSet<RecipeStatuses> EditableStatuses = new HashSet<RecipeStatuses>
    {
        RecipeStatuses.Draft,
        RecipeStatuses.Template
    };

    #region Version helpers

    /// <summary>
    /// Compute the next version based on source status.
    /// Released → major+1.0, otherwise → major.minor+1.
    /// </summary>
    public static RecipeVersion ComputeNextVersion(RecipeVersion current, RecipeStatuses sourceStatus)
    {
        if (sourceStatus == RecipeStatuses.Released)
            return current.IncrementMajor();
        return current.IncrementMinor();
    }

    #endregion

    #region Status transition helpers

    /// <summary>
    /// Check if a status transition is allowed.
    /// </summary>
    public static bool IsTransitionAllowed(RecipeStatuses from, RecipeStatuses to)
    {
        if (!AllowedTransitions.TryGetValue(from, out var allowed))
            return false;
        return allowed.Contains(to);
    }

    /// <summary>
    /// Check if a recipe with the given status is directly editable.
    /// </summary>
    public static bool IsDirectlyEditable(RecipeStatuses status)
    {
        return EditableStatuses.Contains(status);
    }

    /// <summary>
    /// Parse a status string to enum. Returns false if invalid.
    /// </summary>
    public static bool TryParseStatus(string statusString, out RecipeStatuses status)
    {
        return Enum.TryParse(statusString, ignoreCase: true, out status);
    }

    /// <summary>
    /// Parse int to status enum. Returns false if out of range.
    /// </summary>
    public static bool TryParseStatus(int statusInt, out RecipeStatuses status)
    {
        if (Enum.IsDefined(typeof(RecipeStatuses), statusInt))
        {
            status = (RecipeStatuses)statusInt;
            return true;
        }
        status = default;
        return false;
    }

    #endregion

    #region Step validation helpers

    /// <summary>
    /// Validate step ordering: active steps (PhaseType != 0) must be contiguous at the start,
    /// followed only by tail steps (PhaseType == 0). Active steps must be numbered 1..N.
    /// </summary>
    public static (bool isValid, List<string> errors) ValidateStepSequence(List<float> phaseTypes)
    {
        var errors = new List<string>();
        bool foundTail = false;
        int expectedPhaseType = 1;

        for (int i = 0; i < phaseTypes.Count; i++)
        {
            float pt = phaseTypes[i];

            if (pt == 0f)
            {
                // Tail step
                foundTail = true;
            }
            else
            {
                // Active step
                if (foundTail)
                {
                    errors.Add($"Step {i + 1}: active step (PhaseType={pt}) found after unused tail step.");
                }

                if ((int)pt != expectedPhaseType)
                {
                    errors.Add($"Step {i + 1}: expected PhaseType={expectedPhaseType}, found {pt}. Active steps must be sequential 1..N.");
                }
                expectedPhaseType++;
            }
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>
    /// Normalize step PhaseType values: renumber active steps as 1..N, set all tail steps to 0.
    /// Input: list of PhaseType values where != 0 means active. 
    /// Active steps stay in their current order, tail steps stay at end.
    /// </summary>
    public static List<float> NormalizePhaseTypes(List<float> phaseTypes)
    {
        var result = new List<float>(phaseTypes.Count);
        int activeIndex = 1;

        for (int i = 0; i < phaseTypes.Count; i++)
        {
            if (phaseTypes[i] != 0f)
            {
                result.Add(activeIndex);
                activeIndex++;
            }
            else
            {
                result.Add(0f);
            }
        }

        return result;
    }

    /// <summary>
    /// Count active steps (PhaseType != 0) in the list.
    /// </summary>
    public static int CountActiveSteps(List<float> phaseTypes)
    {
        return phaseTypes.Count(pt => pt != 0f);
    }

    #endregion

    #region Utility

    /// <summary>
    /// Resolve the FTOptix ProjectFiles directory path.
    /// </summary>
    public static string GetProjectFilesPath()
    {
        var resourceUri = ResourceUri.FromProjectRelativePath(string.Empty);
        string projectDir = resourceUri.Uri;
        // ProjectFiles is a sibling of NetSolution within ProjectFiles
        // The %PROJECTDIR% resolves to the ProjectFiles directory
        return projectDir;
    }

    /// <summary>
    /// Build configuration file full path.
    /// </summary>
    public static string GetConfigFilePath(string fileName = "recipe_configuration.yaml")
    {
        return System.IO.Path.Combine(GetProjectFilesPath(), fileName);
    }

    #endregion
}
