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
    private const string ConfigFileName = "recipe_configuration.yaml";

    // Safety: physical delete disabled by default
    private bool _physicalDeleteAllowed = true;

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
    /// Generate test recipes. Reads parameters from LogicObject variables:
    ///   Count (int), RecipeFamily (float), ActiveStepCount (int),
    ///   NamePrefix (string), Status (int), OverwriteExistingTestRecipes (bool).
    /// Falls back to defaults if variables not configured.
    /// </summary>
    [ExportMethod]
    public void GenerateTestRecipes()
    {
        int count = GetVariableValueOrDefault("Count", 10);
        float recipeFamily = GetVariableValueOrDefault("RecipeFamily", 1f);
        int activeStepCount = GetVariableValueOrDefault("ActiveStepCount", 5);
        string namePrefix = GetVariableValueOrDefault("NamePrefix", DefaultPrefix);
        int statusInt = GetVariableValueOrDefault("Status", 1);
        bool overwrite = GetVariableValueOrDefault("OverwriteExistingTestRecipes", false);

        var result = GenerateTestRecipesInternal(count, recipeFamily, activeStepCount, namePrefix, statusInt, overwrite);

        if (result.Success)
            Log.Info("TestTools", $"Generated {result.CreatedCount}/{result.TotalRequested} recipes.");
        else
            Log.Error("TestTools", $"Generation failed. Errors: {string.Join("; ", result.Errors)}");
    }

    /// <summary>
    /// Internal implementation with explicit parameters. Can be called programmatically.
    /// </summary>
    /// <param name="count">Number of test recipes to generate (must be > 0).</param>
    /// <param name="recipeFamily">Recipe family key (cast to int, must exist in YAML config).</param>
    /// <param name="activeStepCount">Steps 1..N marked active/enabled; rest get PhaseType=0. Range: 1–20.</param>
    /// <param name="namePrefix">Name prefix for generated recipes. Default: "TEST_RECIPE_".</param>
    /// <param name="statusInt">Initial RecipeStatuses value written to metadata. Default: 1.</param>
    /// <param name="overwriteExistingTestRecipes">If true, archives existing recipes matching prefix before generating.</param>
    public TestRecipeGenerationResult GenerateTestRecipesInternal(int count, float recipeFamily,
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

        if (activeStepCount < 1)
        {
            result.Errors.Add("activeStepCount must be >= 1.");
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

        // Discover actual step items from schema (don't hardcode step count)
        var stepItems = DiscoverStepItems();
        int actualStepCount = stepItems.Count;
        // Clamp activeStepCount to actual available steps
        if (activeStepCount > actualStepCount)
            activeStepCount = actualStepCount;

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
                var setFamilyResult = _schema.SetRecipeDataItemValue(recipeId,
                    new string[] { "Parameters1" },
                    new string[] { "RecipeFamily" },
                    new ElementAccessStruct(), recipeFamily);
                if (setFamilyResult != SetRecipeDataItemValueResultCode.Success)
                    Log.Warning("TestTools", $"{recipeName}: SetRecipeFamily failed ({setFamilyResult})");

                // Set steps: active 1..activeStepCount, rest disabled
                for (int s = 0; s < actualStepCount; s++)
                {
                    string stepName = stepItems[s];
                    bool isActive = (s < activeStepCount);
                    float phaseType = isActive ? (float)(s + 1) : 0f;

                    _schema.SetRecipeDataItemValue(recipeId,
                        new string[] { stepName },
                        new string[] { "PhaseType" },
                        new ElementAccessStruct(), phaseType);

                    _schema.SetRecipeDataItemValue(recipeId,
                        new string[] { stepName },
                        new string[] { "StepEnabled" },
                        new ElementAccessStruct(), isActive);
                }

                // Set Status metadata
                TrySetMetadata(recipeId, RecipeHelpers.MetadataStatus, (int)initialStatus);

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
    /// Archive all test recipes matching prefix (sets Status=Archived).
    /// Reads parameters from LogicObject variables.
    /// </summary>
    /// <param name="NamePrefix">LogicObject variable. Name prefix filter. Default: "TEST_RECIPE_".</param>
    /// <param name="DryRun">LogicObject variable. If true, reports candidates without archiving. Default: false.</param>
    [ExportMethod]
    public void BulkArchiveTestRecipes()
    {
        string namePrefix = GetVariableValueOrDefault("NamePrefix", DefaultPrefix);
        bool dryRun = GetVariableValueOrDefault("DryRun", false);

        string prefix = string.IsNullOrWhiteSpace(namePrefix) ? DefaultPrefix : namePrefix;
        var result = BulkArchiveTestRecipesInternal(prefix, dryRun);

        if (result.Success)
            Log.Info("TestTools", $"BulkArchive: {result.AffectedCount} archived, {result.CandidateCount} candidates (dryRun={dryRun}).");
        else
            Log.Error("TestTools", $"BulkArchive failed. Errors: {string.Join("; ", result.Errors)}");
    }

    private BulkOperationResult BulkArchiveTestRecipesInternal(string prefix, bool dryRun)
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

        // Find candidates matching prefix
        var candidates = recipesResult.Recipes
            .Where(r => r.RecipeId.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

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
    /// Physically delete test recipes matching prefix. Disabled by default for safety.
    /// Reads parameters from LogicObject variables.
    /// </summary>
    /// <remarks>
    /// Safety: requires _physicalDeleteAllowed=true (code-level) AND DryRun=false (user-level).
    /// </remarks>
    /// <param name="NamePrefix">LogicObject variable. Name prefix filter. Default: "TEST_RECIPE_".</param>
    /// <param name="DryRun">LogicObject variable. If true, reports candidates without deleting. Default: true.</param>
    [ExportMethod]
    public void BulkDeleteTestRecipes()
    {
        string namePrefix = GetVariableValueOrDefault("NamePrefix", DefaultPrefix);
        bool dryRun = GetVariableValueOrDefault("DryRun", false);

        var result = BulkDeleteTestRecipesInternal(namePrefix, dryRun);

        if (result.Success)
            Log.Info("TestTools", $"BulkDelete: {result.AffectedCount} deleted, {result.CandidateCount} candidates (dryRun={dryRun}).");
        else
            Log.Error("TestTools", $"[{result.ErrorCode}] BulkDelete failed. Errors: {string.Join("; ", result.Errors)}");
    }

    private BulkOperationResult BulkDeleteTestRecipesInternal(string namePrefix, bool dryRun)
    {
        var result = new BulkOperationResult();

        // Safety gate: physical delete must be explicitly allowed at code level
        if (!_physicalDeleteAllowed)
        {
            result.Success = false;
            result.ErrorCode = "PhysicalDeleteNotAllowed";
            result.Errors.Add("Physical deletion is disabled. Set _physicalDeleteAllowed=true in code to enable.");
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

        // Find candidates matching prefix
        var candidates = recipesResult.Recipes
            .Where(r => r.RecipeId.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

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

    /// <summary>
    /// Read a variable value from LogicObject, return default if not found or wrong type.
    /// </summary>
    private T GetVariableValueOrDefault<T>(string variableName, T defaultValue)
    {
        try
        {
            var variable = LogicObject.GetVariable(variableName);
            if (variable == null)
                return defaultValue;

            var value = variable.Value;
            if (value == null)
                return defaultValue;

            return (T)Convert.ChangeType(value.Value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Discover step item names from schema by inspecting data items.
    /// Returns list of top-level item browse paths matching "RecipeStepRSA*".
    /// </summary>
    private List<string> DiscoverStepItems()
    {
        var steps = new List<string>();
        try
        {
            // Use first existing recipe to discover structure, or create a probe
            var recipesResult = _schema.GetRecipes();
            RecipeId probeId = null;

            if (recipesResult.ResultCode == GetRecipesResultCode.Success && recipesResult.Recipes.Length > 0)
            {
                probeId = recipesResult.Recipes[0].RecipeId;
            }
            else
            {
                // Create a temporary recipe to probe structure
                probeId = new RecipeId { Name = "_PROBE_STRUCTURE_", Version = "1.0" };
                var createResult = _schema.CreateRecipe(probeId);
                if (createResult != CreateRecipeResultCode.Success)
                {
                    Log.Warning("TestTools", "Cannot discover steps — using fallback 1..3");
                    return new List<string> { "RecipeStepRSA1", "RecipeStepRSA2", "RecipeStepRSA3" };
                }
            }

            var dataItems = _schema.GetDataItems(probeId);
            if (dataItems.ResultCode == GetDataItemsResultCode.Success)
            {
                // Collect distinct top-level items matching step pattern
                steps = dataItems.DataItems
                    .Where(di => di.ItemRelativeBrowsePath.Length > 0 &&
                                 di.ItemRelativeBrowsePath[0].StartsWith("RecipeStepRSA"))
                    .Select(di => di.ItemRelativeBrowsePath[0])
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }

            // Clean up probe if we created one
            if (probeId.Name == "_PROBE_STRUCTURE_")
                _schema.DeleteRecipe(probeId);
        }
        catch (Exception ex)
        {
            Log.Warning("TestTools", $"DiscoverStepItems failed: {ex.Message}. Using fallback.");
        }

        // Fallback if discovery returned nothing
        if (steps.Count == 0)
            steps = new List<string> { "RecipeStepRSA1", "RecipeStepRSA2", "RecipeStepRSA3" };

        return steps;
    }

    /// <summary>
    /// Safely attempt to set metadata. Logs warning if field not found on schema.
    /// </summary>
    private void TrySetMetadata(RecipeId recipeId, string metadataName, object value)
    {
        try
        {
            var resultCode = _schema.SetRecipeMetadataValue(recipeId, metadataName, value);
            if (resultCode != SetRecipeMetadataValueResultCode.Success)
                Log.Warning("TestTools", $"SetMetadata '{metadataName}' on '{recipeId.Name}': {resultCode}");
        }
        catch (Exception ex)
        {
            Log.Warning("TestTools", $"SetMetadata '{metadataName}' exception: {ex.Message}");
        }
    }

    #endregion
}
