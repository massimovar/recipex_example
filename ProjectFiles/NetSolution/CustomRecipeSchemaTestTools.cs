#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.RecipeX;
using FTOptix.Alarm;
#endregion

/// <summary>
/// RecipeRuntimeTestToolsNetLogic: runtime-only testing utilities.
/// Generates test recipes and provides bulk cleanup.
/// Must NOT be part of validated production workflow.
/// </summary>
public class CustomRecipeSchemaTestTools : BaseNetLogic
{
    private RecipeSchema _schema;
    private RecipeConfigurationLoader _configLoader;
    private const string DefaultPrefix = "TEST_RECIPE_";
    private const string GeneratedByMarker = "RecipeRuntimeTestToolsNetLogic";
    private const string ConfigFileName = "recipe_configuration.yaml";

    // Safety: physical delete disabled by default
    private bool _physicalDeleteAllowed = false;

    public override void Start()
    {
        // Resolve RecipeSchema — must be passed or found in model
        _schema = (RecipeSchema) Owner;
        // Load config
        _configLoader = new RecipeConfigurationLoader();
        string configPath = RecipeHelpers.GetConfigFilePath(ConfigFileName);
        _configLoader.Load(configPath);
    }

    public override void Stop()
    {
    }

    #region GenerateTestRecipes

    /// <summary>
    /// Generate a variable number of test recipes for development/testing.
    /// </summary>
    /// <param name="count">Number of test recipes to generate (must be > 0).</param>
    /// <param name="recipeFamily">Recipe family key (cast to int, must exist in YAML config).</param>
    /// <param name="activeStepCount">Steps 1..N marked active/enabled; rest get PhaseType=0. Range: 1–20.</param>
    /// <param name="namePrefix">Name prefix for generated recipes. Default: "TEST_RECIPE_".</param>
    /// <param name="statusInt">Initial RecipeStatuses value written to metadata. Default: 1.</param>
    /// <param name="overwriteExistingTestRecipes">If true, archives existing recipes matching prefix before generating.</param>
    [ExportMethod]
    public TestRecipeGenerationResult GenerateTestRecipes(int count, float recipeFamily,
        int activeStepCount = 5, string namePrefix = null, int statusInt = 1,
        bool overwriteExistingTestRecipes = false)
    {
        var result = new TestRecipeGenerationResult { TotalRequested = count };

        // Validate inputs
        if (_schema == null)
        {
            result.Errors.Add("RecipeSchema not configured.");
            return result;
        }

        if (!_configLoader.IsValid)
        {
            result.Errors.Add("YAML configuration invalid: " + string.Join("; ", _configLoader.ValidationErrors));
            return result;
        }

        if (count <= 0)
        {
            result.Errors.Add("Count must be > 0.");
            return result;
        }

        if (activeStepCount < 1 || activeStepCount > 20)
        {
            result.Errors.Add("activeStepCount must be 1..20.");
            return result;
        }

        int familyKey = (int)recipeFamily;
        if (_configLoader.GetFamily(familyKey) == null)
        {
            result.Errors.Add($"Recipe family {familyKey} is not configured.");
            return result;
        }

        if (!RecipeHelpers.TryParseStatus(statusInt, out RecipeStatuses initialStatus))
        {
            result.Errors.Add($"Invalid status value: {statusInt}");
            return result;
        }

        string prefix = string.IsNullOrWhiteSpace(namePrefix) ? DefaultPrefix : namePrefix;
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // If overwrite requested, archive existing matching recipes first
        if (overwriteExistingTestRecipes)
        {
            BulkArchiveTestRecipesInternal(prefix, dryRun: false);
        }

        // Generate recipes
        for (int i = 1; i <= count; i++)
        {
            string recipeName = $"{prefix}{timestamp}_{i:D4}";

            try
            {
                // Check if already exists
                if (RecipeExists(recipeName))
                {
                    result.SkippedCount++;
                    continue;
                }

                // Create recipe
                var recipeId = new RecipeId { Name = recipeName, Version = "1.0" };
                var createCode = _schema.CreateRecipe(recipeId);
                if (createCode != CreateRecipeResultCode.Success)
                {
                    result.FailedCount++;
                    result.Errors.Add($"{recipeName}: CreateRecipe failed ({createCode})");
                    continue;
                }

                // Set RecipeFamily
                _schema.SetRecipeDataItemValue(recipeId,
                    new string[] { "Parameters1" },
                    new string[] { "RecipeFamily" },
                    new ElementAccessStruct(), recipeFamily);

                // Set steps: active 1..activeStepCount, tail for rest
                for (int s = 1; s <= 20; s++)
                {
                    string stepName = $"RecipeStepRSA{s}";
                    float phaseType = (s <= activeStepCount) ? (float)s : 0f;

                    _schema.SetRecipeDataItemValue(recipeId,
                        new string[] { stepName },
                        new string[] { "PhaseType" },
                        new ElementAccessStruct(), phaseType);

                    _schema.SetRecipeDataItemValue(recipeId,
                        new string[] { stepName },
                        new string[] { "StepEnabled" },
                        new ElementAccessStruct(), s <= activeStepCount);
                }

                // Set metadata (version is in RecipeId.Version, CreatedAt is native DB column)
                _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)initialStatus);
                _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataGeneratedBy, GeneratedByMarker);

                result.CreatedCount++;
                result.GeneratedRecipeNames.Add(recipeName);
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add($"{recipeName}: {ex.Message}");
            }
        }

        result.Success = (result.FailedCount == 0);
        return result;
    }

    #endregion

    #region BulkArchiveTestRecipes

    /// <summary>
    /// Archive all test recipes matching prefix and/or generated-by marker.
    /// </summary>
    /// <param name="namePrefix">Name prefix filter. Default: "TEST_RECIPE_".</param>
    /// <param name="onlyCreatedByThisTool">If true, only archives recipes with GeneratedBy marker matching this tool.</param>
    /// <param name="dryRun">If true, reports candidates without modifying anything.</param>
    [ExportMethod]
    public BulkOperationResult BulkArchiveTestRecipes(string namePrefix = null,
        bool onlyCreatedByThisTool = true, bool dryRun = true)
    {
        string prefix = string.IsNullOrWhiteSpace(namePrefix) ? DefaultPrefix : namePrefix;
        return BulkArchiveTestRecipesInternal(prefix, dryRun, onlyCreatedByThisTool);
    }

    private BulkOperationResult BulkArchiveTestRecipesInternal(string prefix, bool dryRun, bool onlyCreatedByThisTool = true)
    {
        var result = new BulkOperationResult();

        if (_schema == null)
        {
            result.ErrorCode = "ConfigurationInvalid";
            result.Errors.Add("RecipeSchema not configured.");
            return result;
        }

        var recipesResult = _schema.GetRecipes();
        if (recipesResult.ResultCode != GetRecipesResultCode.Success)
        {
            result.ErrorCode = "StoreError";
            result.Errors.Add($"GetRecipes failed: {recipesResult.ResultCode}");
            return result;
        }

        // Find candidates
        var candidates = recipesResult.Recipes
            .Where(r => r.RecipeId.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Optionally filter by generated-by marker
        if (onlyCreatedByThisTool)
        {
            candidates = candidates.Where(r =>
            {
                var meta = _schema.GetRecipeMetadataValue(r.RecipeId, RecipeHelpers.MetadataGeneratedBy);
                return meta.ResultCode == GetRecipeMetadataValueResultCode.Success &&
                       meta.MetadataValue?.Value?.ToString() == GeneratedByMarker;
            }).ToList();
        }

        result.CandidateCount = candidates.Count;

        if (dryRun)
        {
            // Just report candidates
            result.Success = true;
            result.AffectedRecipeNames = candidates.Select(r => r.RecipeId.Name).ToList();
            return result;
        }

        // Archive each candidate
        foreach (var recipe in candidates)
        {
            try
            {
                // Check if already archived
                var statusMeta = _schema.GetRecipeMetadataValue(recipe.RecipeId, RecipeHelpers.MetadataStatus);
                if (statusMeta.ResultCode == GetRecipeMetadataValueResultCode.Success &&
                    statusMeta.MetadataValue?.Value is int si && si == (int)RecipeStatuses.Archived)
                {
                    result.SkippedCount++;
                    continue;
                }

                _schema.SetRecipeMetadataValue(recipe.RecipeId, RecipeHelpers.MetadataStatus, (int)RecipeStatuses.Archived);
                result.AffectedCount++;
                result.AffectedRecipeNames.Add(recipe.RecipeId.Name);
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add($"{recipe.RecipeId.Name}: {ex.Message}");
            }
        }

        result.Success = (result.FailedCount == 0);
        return result;
    }

    #endregion

    #region BulkDeleteTestRecipes

    /// <summary>
    /// Physically delete test recipes. Disabled by default for safety.
    /// Only operates on recipes clearly identified as test recipes.
    /// </summary>
    /// <param name="namePrefix">Name prefix filter. Default: "TEST_RECIPE_".</param>
    /// <param name="onlyCreatedByThisTool">If true, only deletes recipes with GeneratedBy marker matching this tool.</param>
    /// <param name="dryRun">If true, reports candidates without deleting anything.</param>
    /// <param name="confirmPhysicalDelete">Must be true to allow physical deletion. Double-gate with _physicalDeleteAllowed.</param>
    [ExportMethod]
    public BulkOperationResult BulkDeleteTestRecipes(string namePrefix = null,
        bool onlyCreatedByThisTool = true, bool dryRun = true, bool confirmPhysicalDelete = false)
    {
        var result = new BulkOperationResult();

        // Safety gate: physical delete must be explicitly allowed
        if (!_physicalDeleteAllowed)
        {
            result.Success = false;
            result.ErrorCode = "PhysicalDeleteNotAllowed";
            result.Errors.Add("Physical deletion is disabled. Set _physicalDeleteAllowed=true in configuration to enable.");
            return result;
        }

        if (!confirmPhysicalDelete)
        {
            result.Success = false;
            result.ErrorCode = "PhysicalDeleteNotAllowed";
            result.Errors.Add("confirmPhysicalDelete must be true to physically delete recipes.");
            return result;
        }

        if (_schema == null)
        {
            result.ErrorCode = "ConfigurationInvalid";
            result.Errors.Add("RecipeSchema not configured.");
            return result;
        }

        string prefix = string.IsNullOrWhiteSpace(namePrefix) ? DefaultPrefix : namePrefix;

        var recipesResult = _schema.GetRecipes();
        if (recipesResult.ResultCode != GetRecipesResultCode.Success)
        {
            result.ErrorCode = "StoreError";
            result.Errors.Add($"GetRecipes failed: {recipesResult.ResultCode}");
            return result;
        }

        // Find candidates: must match prefix AND have generated-by marker
        var candidates = recipesResult.Recipes
            .Where(r => r.RecipeId.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (onlyCreatedByThisTool)
        {
            candidates = candidates.Where(r =>
            {
                var meta = _schema.GetRecipeMetadataValue(r.RecipeId, RecipeHelpers.MetadataGeneratedBy);
                return meta.ResultCode == GetRecipeMetadataValueResultCode.Success &&
                       meta.MetadataValue?.Value?.ToString() == GeneratedByMarker;
            }).ToList();
        }

        result.CandidateCount = candidates.Count;

        if (dryRun)
        {
            result.Success = true;
            result.AffectedRecipeNames = candidates.Select(r => r.RecipeId.Name).ToList();
            return result;
        }

        // Physically delete
        foreach (var recipe in candidates)
        {
            try
            {
                var deleteCode = _schema.DeleteRecipe(recipe.RecipeId);
                if (deleteCode == DeleteRecipeResultCode.Success)
                {
                    result.AffectedCount++;
                    result.AffectedRecipeNames.Add(recipe.RecipeId.Name);
                }
                else
                {
                    result.FailedCount++;
                    result.Errors.Add($"{recipe.RecipeId.Name}: DeleteRecipe failed ({deleteCode})");
                }
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add($"{recipe.RecipeId.Name}: {ex.Message}");
            }
        }

        result.Success = (result.FailedCount == 0);
        return result;
    }

    #endregion

    #region Helpers

    private bool RecipeExists(string recipeName)
    {
        var recipes = _schema.GetRecipes();
        if (recipes.ResultCode != GetRecipesResultCode.Success)
            return false;
        return recipes.Recipes.Any(r => r.RecipeId.Name == recipeName);
    }

    #endregion
}
