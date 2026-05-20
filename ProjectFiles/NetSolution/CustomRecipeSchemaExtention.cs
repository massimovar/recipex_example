#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.RecipeX;
using FTOptix.Store;
using FTOptix.Alarm;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using OpcUa = UAManagedCore.OpcUa;
#endregion

/// <summary>
/// RecipeSchemaNetLogic: owns recipe lifecycle and business operations.
/// Must be added as a child of the RecipeSchema node.
/// </summary>
public class CustomRecipeSchemaExtention : BaseNetLogic
{
    private RecipeSchema _schema;
    private const string Tag = "RecipeSchemaNetLogic";

    private IUANode _recipeStatusesEnum;

    public override void Start()
    {
        _schema = (RecipeSchema)LogicObject.Owner;
        if (_schema == null)
        {
            Log.Error(Tag, "Owner is not a RecipeSchema. NetLogic must be child of RecipeSchema.");
            return;
        }

        // Resolve RecipeStatuses enumeration from configured NodeId variable
        var statusesVar = LogicObject.GetVariable("RecipeStatuses");
        if (statusesVar != null)
        {
            NodeId sid = (NodeId)statusesVar.Value;
            if (sid != null && sid != NodeId.Empty)
                _recipeStatusesEnum = InformationModel.Get(sid);
        }
        if (_recipeStatusesEnum == null)
        {
            Log.Warning(Tag, "RecipeStatuses variable not configured. Enum validation unavailable.");
        }
        else
        {
            ValidateRecipeStatusesEnum();
        }
    }

    public override void Stop() { }

    /// <summary>
    /// Validate C# RecipeStatuses enum matches Model/RecipeStatuses enumeration node.
    /// </summary>
    private void ValidateRecipeStatusesEnum()
    {
        if (_recipeStatusesEnum == null) return;

        var modelValues = _recipeStatusesEnum.Children.OfType<IUANode>().ToList();
        var csharpValues = Enum.GetValues(typeof(RecipeStatuses)).Cast<RecipeStatuses>().ToList();

        foreach (var csVal in csharpValues)
        {
            string expectedName = csVal.ToString();
            var match = modelValues.FirstOrDefault(n => n.BrowseName == expectedName);
            if (match == null)
                Log.Error(Tag, $"C# enum '{expectedName}' ({(int)csVal}) not found in Model/RecipeStatuses. Sync required.");
        }

        foreach (var modelNode in modelValues)
        {
            if (!Enum.TryParse<RecipeStatuses>(modelNode.BrowseName, out _))
                Log.Error(Tag, $"Model enum value '{modelNode.BrowseName}' has no matching C# RecipeStatuses entry.");
        }
    }

    #region CreateRecipe

    /// <summary>
    /// Create a new recipe. Optionally copies from a source/template recipe.
    /// </summary>
    [ExportMethod]
    public void CreateRecipe(string recipeName, float recipeFamily,
        string sourceRecipeName, int initialStatusInt, out bool success)
    {
        success = false;

        if (!RecipeHelpers.TryParseStatus(initialStatusInt, out RecipeStatuses initialStatus))
        { Log.Error(Tag, $"CreateRecipe: invalid initialStatus value: {initialStatusInt}"); return; }

        if (initialStatus != RecipeStatuses.Draft && initialStatus != RecipeStatuses.Template)
        { Log.Error(Tag, "CreateRecipe: initial status must be Draft or Template."); return; }

        if (string.IsNullOrWhiteSpace(recipeName))
        { Log.Error(Tag, "CreateRecipe: recipe name cannot be empty."); return; }

        if (RecipeExists(recipeName))
        { Log.Warning(Tag, $"CreateRecipe: recipe '{recipeName}' already exists."); return; }

        if (!string.IsNullOrWhiteSpace(sourceRecipeName))
        {
            if (GetRecipeStatus(sourceRecipeName) == null)
            { Log.Error(Tag, $"CreateRecipe: source recipe '{sourceRecipeName}' not found."); return; }
        }

        int familyKey = (int)recipeFamily;
        string version = "1.0";
        var recipeId = new RecipeId { Name = recipeName, Version = version };

        if (!string.IsNullOrWhiteSpace(sourceRecipeName))
        {
            var sourceId = GetLatestRecipeId(sourceRecipeName);
            if (sourceId == null)
            { Log.Error(Tag, $"CreateRecipe: source recipe '{sourceRecipeName}' not found."); return; }

            var dupResult = _schema.DuplicateRecipe(sourceId, recipeId);
            if (dupResult != DuplicateRecipeResultCode.Success)
            { Log.Error(Tag, $"CreateRecipe: DuplicateRecipe failed: {dupResult}"); return; }
        }
        else
        {
            var createResult = _schema.CreateRecipe(recipeId);
            if (createResult != CreateRecipeResultCode.Success)
            { Log.Error(Tag, $"CreateRecipe: CreateRecipe failed: {createResult}"); return; }

            InitializeBlankRecipe(recipeId, familyKey);
        }

        _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)initialStatus);

        var (valid, errors) = ValidateRecipeInternal(recipeId);
        if (!valid)
        {
            _schema.DeleteRecipe(recipeId);
            Log.Error(Tag, $"CreateRecipe: validation failed, rolled back. {string.Join("; ", errors)}");
            return;
        }

        success = true;
        Log.Info(Tag, $"CreateRecipe: '{recipeName}' created (v{version}, status={initialStatus}).");
    }

    #endregion

    #region UpdateRecipe

    /// <summary>
    /// Update a draft recipe in-place, or create a new revision from non-draft.
    /// </summary>
    [ExportMethod]
    public void UpdateRecipe(string sourceRecipeName, string newRecipeName, NodeId updatedModelRoot, out bool success)
    {
        success = false;

        if (string.IsNullOrWhiteSpace(sourceRecipeName))
        { Log.Error(Tag, "UpdateRecipe: source recipe name cannot be empty."); return; }

        var sourceId = GetLatestRecipeId(sourceRecipeName);
        if (sourceId == null)
        { Log.Error(Tag, $"UpdateRecipe: source recipe '{sourceRecipeName}' not found."); return; }

        var status = GetRecipeStatus(sourceRecipeName);
        if (status == null)
        { Log.Error(Tag, $"UpdateRecipe: cannot read status for '{sourceRecipeName}'."); return; }

        var currentVersion = GetRecipeVersion(sourceRecipeName);
        if (currentVersion == null)
        { Log.Error(Tag, "UpdateRecipe: cannot read version from source recipe."); return; }

        if (RecipeHelpers.IsDirectlyEditable(status.Value))
        {
            var transferResult = _schema.TransferFromTargetToStore(sourceId, updatedModelRoot, overwrite: true, ErrorPolicy.Strict);
            if (transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeUpdated &&
                transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeCreated)
            { Log.Error(Tag, $"UpdateRecipe: TransferFromTargetToStore failed: {transferResult}"); return; }

            var (valid, errors) = ValidateRecipeInternal(sourceId);
            if (!valid)
            { Log.Error(Tag, $"UpdateRecipe: validation failed. {string.Join("; ", errors)}"); return; }

            success = true;
            Log.Info(Tag, $"UpdateRecipe: '{sourceRecipeName}' updated in place.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(newRecipeName))
            { Log.Error(Tag, "UpdateRecipe: newRecipeName required when source is not directly editable."); return; }

            if (RecipeExists(newRecipeName))
            { Log.Warning(Tag, $"UpdateRecipe: recipe '{newRecipeName}' already exists."); return; }

            var nextVersion = RecipeHelpers.ComputeNextVersion(currentVersion.Value, status.Value);
            var newId = new RecipeId { Name = newRecipeName, Version = nextVersion.ToString() };

            var dupResult = _schema.DuplicateRecipe(sourceId, newId);
            if (dupResult != DuplicateRecipeResultCode.Success)
            { Log.Error(Tag, $"UpdateRecipe: DuplicateRecipe failed: {dupResult}"); return; }

            var transferResult = _schema.TransferFromTargetToStore(newId, updatedModelRoot, overwrite: true, ErrorPolicy.Strict);
            if (transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeUpdated &&
                transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeCreated)
            {
                _schema.DeleteRecipe(newId);
                Log.Error(Tag, $"UpdateRecipe: TransferFromTargetToStore failed: {transferResult}");
                return;
            }

            _schema.SetRecipeMetadataValue(newId, RecipeHelpers.MetadataStatus, (int)RecipeStatuses.Draft);

            var (valid, errors) = ValidateRecipeInternal(newId);
            if (!valid)
            {
                _schema.DeleteRecipe(newId);
                Log.Error(Tag, $"UpdateRecipe: new revision failed validation, rolled back. {string.Join("; ", errors)}");
                return;
            }

            success = true;
            Log.Info(Tag, $"UpdateRecipe: new revision '{newRecipeName}' (v{nextVersion}) created from '{sourceRecipeName}'.");
        }
    }

    #endregion

    #region DeleteRecipe

    /// <summary>
    /// Logical delete: sets recipe status to Archived.
    /// </summary>
    [ExportMethod]
    public void DeleteRecipe(string recipeName, out bool success)
    {
        success = false;

        if (string.IsNullOrWhiteSpace(recipeName))
        { Log.Error(Tag, "DeleteRecipe: recipe name cannot be empty."); return; }

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
        { Log.Error(Tag, $"DeleteRecipe: recipe '{recipeName}' not found."); return; }

        var status = GetRecipeStatus(recipeName);
        if (status == null)
        { Log.Error(Tag, $"DeleteRecipe: cannot read status for '{recipeName}'."); return; }

        if (status.Value == RecipeStatuses.Archived)
        { Log.Warning(Tag, "DeleteRecipe: recipe is already archived."); return; }

        _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)RecipeStatuses.Archived);

        success = true;
        Log.Info(Tag, $"DeleteRecipe: '{recipeName}' archived.");
    }

    #endregion

    #region UpdateRecipeStatus

    /// <summary>
    /// Advance recipe status along allowed lifecycle transitions.
    /// </summary>
    [ExportMethod]
    public void UpdateRecipeStatus(string recipeName, int newStatusInt, out bool success)
    {
        success = false;

        if (string.IsNullOrWhiteSpace(recipeName))
        { Log.Error(Tag, "UpdateRecipeStatus: recipe name cannot be empty."); return; }

        if (!RecipeHelpers.TryParseStatus(newStatusInt, out RecipeStatuses newStatus))
        { Log.Error(Tag, $"UpdateRecipeStatus: invalid status value: {newStatusInt}"); return; }

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
        { Log.Error(Tag, $"UpdateRecipeStatus: recipe '{recipeName}' not found."); return; }

        var currentStatus = GetRecipeStatus(recipeName);
        if (currentStatus == null)
        { Log.Error(Tag, $"UpdateRecipeStatus: cannot read status for '{recipeName}'."); return; }

        if (!RecipeHelpers.IsTransitionAllowed(currentStatus.Value, newStatus))
        { Log.Warning(Tag, $"UpdateRecipeStatus: transition from {currentStatus.Value} to {newStatus} not allowed."); return; }

        if (newStatus == RecipeStatuses.Released)
        {
            var (valid, errors) = ValidateRecipeInternal(recipeId);
            if (!valid)
            { Log.Error(Tag, $"UpdateRecipeStatus: cannot release, validation failed. {string.Join("; ", errors)}"); return; }
        }

        _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)newStatus);

        success = true;
        Log.Info(Tag, $"UpdateRecipeStatus: '{recipeName}' status changed to {newStatus}.");
    }

    #endregion

    #region DuplicateRecipe

    /// <summary>
    /// Duplicate a recipe with a new name (and optionally a new family). Always creates as Draft v1.0.
    /// </summary>
    [ExportMethod]
    public void DuplicateRecipe(string sourceRecipeName, string newRecipeName, float newRecipeFamily, out bool success)
    {
        success = false;

        if (string.IsNullOrWhiteSpace(sourceRecipeName))
        { Log.Error(Tag, "DuplicateRecipe: source recipe name cannot be empty."); return; }

        if (string.IsNullOrWhiteSpace(newRecipeName))
        { Log.Error(Tag, "DuplicateRecipe: new recipe name cannot be empty."); return; }

        var sourceId = GetLatestRecipeId(sourceRecipeName);
        if (sourceId == null)
        { Log.Error(Tag, $"DuplicateRecipe: source recipe '{sourceRecipeName}' not found."); return; }

        if (RecipeExists(newRecipeName))
        { Log.Warning(Tag, $"DuplicateRecipe: recipe '{newRecipeName}' already exists."); return; }

        string version = "1.0";
        var newId = new RecipeId { Name = newRecipeName, Version = version };
        var dupResult = _schema.DuplicateRecipe(sourceId, newId);
        if (dupResult != DuplicateRecipeResultCode.Success)
        { Log.Error(Tag, $"DuplicateRecipe: DuplicateRecipe failed: {dupResult}"); return; }

        _schema.SetRecipeMetadataValue(newId, RecipeHelpers.MetadataStatus, (int)RecipeStatuses.Draft);

        var (valid, errors) = ValidateRecipeInternal(newId);
        if (!valid)
        {
            _schema.DeleteRecipe(newId);
            Log.Error(Tag, $"DuplicateRecipe: validation failed, rolled back. {string.Join("; ", errors)}");
            return;
        }

        success = true;
        Log.Info(Tag, $"DuplicateRecipe: '{newRecipeName}' duplicated from '{sourceRecipeName}' (v{version}, Draft).");
    }

    #endregion

    #region ValidateRecipe

    /// <summary>
    /// Full recipe validation. Logs all errors found.
    /// </summary>
    [ExportMethod]
    public void ValidateRecipe(string recipeName, out bool success)
    {
        success = false;

        if (string.IsNullOrWhiteSpace(recipeName))
        { Log.Error(Tag, "ValidateRecipe: recipe name cannot be empty."); return; }

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
        { Log.Error(Tag, $"ValidateRecipe: recipe '{recipeName}' not found."); return; }

        var (valid, errors) = ValidateRecipeInternal(recipeId);

        if (!valid)
        {
            foreach (var err in errors)
                Log.Error(Tag, $"ValidateRecipe: {err}");
            return;
        }

        success = true;
        Log.Info(Tag, $"ValidateRecipe: '{recipeName}' is valid.");
    }

    #endregion

    #region SetRecipeMetadataBoolField

    /// <summary>
    /// Set one bool metadata field to true, all others to false.
    /// Field names derived from RecipeStatuses enum (lowercase).
    /// Uses RecipeSchema.SetRecipeMetadataValue for each field.
    /// Takes recipeName + recipeVersion to build RecipeId directly.
    /// </summary>
    [ExportMethod]
    public void SetRecipeMetadataBoolField(string recipeName, string recipeVersion, string fieldName, out bool success)
    {
        success = false;

        if (string.IsNullOrWhiteSpace(recipeName))
        { Log.Error(Tag, "SetRecipeMetadataBoolField: recipe name cannot be empty."); return; }

        if (string.IsNullOrWhiteSpace(recipeVersion))
        { Log.Error(Tag, "SetRecipeMetadataBoolField: recipe version cannot be empty."); return; }

        if (string.IsNullOrWhiteSpace(fieldName))
        { Log.Error(Tag, "SetRecipeMetadataBoolField: field name cannot be empty."); return; }

        // Derive valid field names from RecipeStatuses enum (lowercase)
        var statusNames = Enum.GetNames(typeof(RecipeStatuses));

        // Validate field name against enum values
        if (!statusNames.Any(s => s.Equals(fieldName)))
        {
            Log.Error(Tag, $"SetRecipeMetadataBoolField: '{fieldName}' is not a valid RecipeStatuses value. " +
                           $"Valid: {string.Join(", ", statusNames)}");
            return;
        }

        var recipeId = new RecipeId { Name = recipeName, Version = recipeVersion };

        // Set target field true, all others false
        foreach (var s in statusNames)
        {
            string metaKey = s;
            bool val = s.Equals(fieldName);
            var result = _schema.SetRecipeMetadataValue(recipeId, metaKey, val);
            if (result != SetRecipeMetadataValueResultCode.Success)
            {
                Log.Error(Tag, $"SetRecipeMetadataBoolField: failed to set '{metaKey}' = {val} on '{recipeName}' v{recipeVersion}: {result}");
                return;
            }
        }

        success = true;
        Log.Info(Tag, $"SetRecipeMetadataBoolField: '{fieldName}' = true on '{recipeName}' v{recipeVersion}, others cleared.");
    }

    #endregion

    #region GetRecipeStatus

    /// <summary>
    /// Returns the active status name (the only bool metadata field set to true)
    /// among the RecipeStatuses enum values.
    /// </summary>
    [ExportMethod]
    public void GetRecipeStatus(string recipeName, string recipeVersion, out string currentStatus, out bool success)
    {
        currentStatus = string.Empty;
        success = false;

        if (_schema == null)
        { Log.Error(Tag, "Schema not initialized."); return; }

        if (string.IsNullOrWhiteSpace(recipeName))
        { Log.Error(Tag, "GetRecipeStatus: recipe name cannot be empty."); return; }

        if (string.IsNullOrWhiteSpace(recipeVersion))
        { Log.Error(Tag, "GetRecipeStatus: recipe version cannot be empty."); return; }

        var recipeId = new RecipeId { Name = recipeName, Version = recipeVersion };

        // Read each bool metadata field named after RecipeStatuses enum values
        var statusNames = Enum.GetNames(typeof(RecipeStatuses));
        string foundStatus = null;

        foreach (var name in statusNames)
        {
            var result = _schema.GetRecipeMetadataValue(recipeId, name);
            if (result.ResultCode != GetRecipeMetadataValueResultCode.Success)
            {
                Log.Warning(Tag, $"GetRecipeStatus: cannot read metadata '{name}' for '{recipeName}' v{recipeVersion}: {result.ResultCode}");
                continue;
            }

            // Check if this field is true (handle bool, int, string representations)
            object raw = result.MetadataValue?.Value;
            bool isTrue = false;

            if (raw is bool b) isTrue = b;
            else if (raw is int i) isTrue = (i != 0);
            else if (raw is long l) isTrue = (l != 0);
            else if (raw is string s) isTrue = s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
            else Log.Warning(Tag, $"GetRecipeStatus: unexpected type for '{name}': {raw?.GetType().Name ?? "null"}");

            if (isTrue)
            {
                if (foundStatus != null)
                {
                    // Multiple fields set to true — ambiguous state
                    Log.Warning(Tag, $"GetRecipeStatus: multiple status fields true ('{foundStatus}' and '{name}'). Returning first.");
                }
                else
                {
                    foundStatus = name;
                }
            }
        }

        if (foundStatus == null)
        {
            Log.Warning(Tag, $"GetRecipeStatus: no bool status field is true for '{recipeName}' v{recipeVersion}.");
            return;
        }

        currentStatus = foundStatus;
        success = true;
    }

    #endregion

    #region Internal methods

    private bool RecipeExists(string recipeName)
    {
        var recipes = _schema.GetRecipes();
        if (recipes.ResultCode != GetRecipesResultCode.Success) return false;
        return recipes.Recipes.Any(r => r.RecipeId.Name == recipeName);
    }

    private RecipeId GetLatestRecipeId(string recipeName)
    {
        var recipes = _schema.GetRecipes();
        if (recipes.ResultCode != GetRecipesResultCode.Success) return null;
        return recipes.Recipes.FirstOrDefault(r => r.RecipeId.Name == recipeName)?.RecipeId;
    }

    private RecipeStatuses? GetRecipeStatus(string recipeName)
    {
        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null) return null;

        var metaResult = _schema.GetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus);
        if (metaResult.ResultCode != GetRecipeMetadataValueResultCode.Success) return null;

        if (metaResult.MetadataValue?.Value is int statusInt &&
            RecipeHelpers.TryParseStatus(statusInt, out RecipeStatuses status))
            return status;

        if (metaResult.MetadataValue?.Value is string statusStr &&
            RecipeHelpers.TryParseStatus(statusStr, out RecipeStatuses statusFromStr))
            return statusFromStr;

        return null;
    }

    private RecipeVersion? GetRecipeVersion(string recipeName)
    {
        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null) return null;
        return RecipeVersion.TryParse(recipeId.Version, out RecipeVersion v) ? v : (RecipeVersion?)null;
    }

    private float GetRecipeFamily(RecipeId recipeId)
    {
        var result = _schema.GetRecipeDataItemValue(recipeId,
            new string[] { "Parameters1" },
            new string[] { "RecipeFamily" },
            new ElementAccessStruct());

        if (result.ResultCode == GetRecipeDataItemValueResultCode.Success && result.DataItemValue != null)
            return Convert.ToSingle(result.DataItemValue);
        return 0f;
    }

    private void InitializeBlankRecipe(RecipeId recipeId, int familyKey)
    {
        // Set RecipeFamily data item
        _schema.SetRecipeDataItemValue(recipeId,
            new string[] { "Parameters1" },
            new string[] { "RecipeFamily" },
            new ElementAccessStruct(), (float)familyKey);

        // Initialize steps: first active, rest inactive
        for (int i = 1; i <= 20; i++)
        {
            string stepName = $"RecipeStepRSA{i}";
            _schema.SetRecipeDataItemValue(recipeId, new string[] { stepName },
                new string[] { "PhaseType" }, new ElementAccessStruct(), (i == 1) ? 1f : 0f);
            _schema.SetRecipeDataItemValue(recipeId, new string[] { stepName },
                new string[] { "StepEnabled" }, new ElementAccessStruct(), (i == 1));
        }
    }

    /// <summary>
    /// Internal validation: version format, status metadata, step sequence.
    /// </summary>
    private (bool IsValid, List<string> Errors) ValidateRecipeInternal(RecipeId recipeId)
    {
        var errors = new List<string>();

        // Version format check
        if (!RecipeVersion.TryParse(recipeId.Version, out _))
            errors.Add($"Version '{recipeId.Version}' is not valid major.minor format.");

        // Status metadata exists and valid
        var statusMeta = _schema.GetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus);
        if (statusMeta.ResultCode != GetRecipeMetadataValueResultCode.Success)
            errors.Add("Status metadata is missing.");
        else if (statusMeta.MetadataValue?.Value is int statusInt)
        {
            if (!RecipeHelpers.TryParseStatus(statusInt, out _))
                errors.Add($"Status value {statusInt} is not a valid RecipeStatuses value.");
        }
        else
            errors.Add("Status metadata has unexpected type.");

        // Step sequence validation
        var phaseTypes = new List<float>();
        for (int i = 1; i <= 20; i++)
        {
            var ptResult = _schema.GetRecipeDataItemValue(recipeId,
                new string[] { $"RecipeStepRSA{i}" },
                new string[] { "PhaseType" },
                new ElementAccessStruct());

            if (ptResult.ResultCode != GetRecipeDataItemValueResultCode.Success)
            {
                errors.Add($"Step {i}: cannot read PhaseType.");
                phaseTypes.Add(-1f);
            }
            else
                phaseTypes.Add(Convert.ToSingle(ptResult.DataItemValue));
        }

        var (_, seqErrors) = RecipeHelpers.ValidateStepSequence(phaseTypes);
        errors.AddRange(seqErrors);

        return (errors.Count == 0, errors);
    }

    #endregion
}
