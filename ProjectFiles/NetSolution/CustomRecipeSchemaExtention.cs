#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.RecipeX;
using OpcUa = UAManagedCore.OpcUa;
#endregion

/// <summary>
/// RecipeSchemaNetLogic: owns recipe lifecycle and business operations.
/// Must be added as a child of the RecipeSchema node.
/// </summary>
public class CustomRecipeSchemaExtention : BaseNetLogic
{
    private RecipeSchema _schema;
    private RecipeConfigurationLoader _configLoader;
    private const string ConfigFileName = "recipe_configuration.yaml";

    // Resolved from NetLogic NodeId variables — not hardcoded paths
    private IUANode _rolesFolder;
    private IUANode _recipeStatusesEnum;

    public override void Start()
    {
        // Resolve parent RecipeSchema
        _schema = (RecipeSchema)LogicObject.Owner;
        if (_schema == null)
        {
            Log.Error("RecipeSchemaNetLogic", "Owner is not a RecipeSchema. NetLogic must be child of RecipeSchema.");
            return;
        }

        // Resolve Roles folder from the configured NodeId variable
        var rolesVar = LogicObject.GetVariable("Roles");
        if (rolesVar != null)
        {
            NodeId rid = (NodeId)rolesVar.Value;
            if (rid != null && rid != NodeId.Empty)
                _rolesFolder = InformationModel.Get(rid);
        }
        if (_rolesFolder == null)
        {
            Log.Error("RecipeSchemaNetLogic", "Roles variable not configured or node not found. Authorization will fail.");
        }

        // Resolve RecipeStatuses enumeration from the configured NodeId variable
        var statusesVar = LogicObject.GetVariable("RecipeStatuses");
        if (statusesVar != null)
        {
            NodeId sid = (NodeId)statusesVar.Value;
            if (sid != null && sid != NodeId.Empty)
                _recipeStatusesEnum = InformationModel.Get(sid);
        }
        if (_recipeStatusesEnum == null)
        {
            Log.Warning("RecipeSchemaNetLogic", "RecipeStatuses variable not configured. Enum validation unavailable.");
        }
        else
        {
            // Validate C# enum mirrors Model/RecipeStatuses at startup
            ValidateRecipeStatusesEnum();
        }

        // Load YAML configuration
        _configLoader = new RecipeConfigurationLoader();
        string configPath = RecipeHelpers.GetConfigFilePath(ConfigFileName);
        if (!_configLoader.Load(configPath))
        {
            Log.Error("RecipeSchemaNetLogic", $"Configuration load failed. Errors: {string.Join("; ", _configLoader.ValidationErrors)}");
        }
        else
        {
            Log.Info("RecipeSchemaNetLogic", "Configuration loaded and validated successfully.");
        }
    }

    public override void Stop()
    {
        // No periodic resources to clean up
    }

    /// <summary>
    /// Resolve the current session user from the LogicObject context.
    /// Returns null if no user session is available (e.g. design-time or no login).
    /// </summary>
    private IUANode GetCurrentUser()
    {
        try
        {
            // In FTOptix, when called from UI context, the Session handler 
            // is implicitly available. Get user from the session associated
            // with this LogicObject's context.
            var session = LogicObject.Context?.Sessions?.CurrentSessionHandler;
            if (session == null)
                return null;

            return session.User;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check authorization for the current user on a given recipe status.
    /// Uses the Roles folder node resolved at startup from the NetLogic variable.
    /// </summary>
    private bool IsAuthorized(RecipeStatuses status)
    {
        if (_rolesFolder == null)
        {
            Log.Error("RecipeSchemaNetLogic", "Roles folder not resolved. Cannot authorize.");
            return false;
        }

        var user = GetCurrentUser();
        if (user == null)
        {
            Log.Warning("RecipeSchemaNetLogic", "No user session found for authorization check.");
            return false;
        }
        return RecipeHelpers.IsUserAuthorized(user, _rolesFolder, status);
    }

    /// <summary>
    /// Validate that the C# RecipeStatuses enum matches the Model/RecipeStatuses enumeration node.
    /// Logs errors on mismatch so developers catch drift immediately at startup.
    /// </summary>
    private void ValidateRecipeStatusesEnum()
    {
        if (_recipeStatusesEnum == null)
            return;

        // Read children of the enumeration node (each child = one enum value)
        var modelValues = _recipeStatusesEnum.Children.OfType<IUANode>().ToList();
        var csharpValues = Enum.GetValues(typeof(RecipeStatuses)).Cast<RecipeStatuses>().ToList();

        foreach (var csVal in csharpValues)
        {
            string expectedName = csVal.ToString();
            int expectedInt = (int)csVal;

            var match = modelValues.FirstOrDefault(n => n.BrowseName == expectedName);
            if (match == null)
            {
                Log.Error("RecipeSchemaNetLogic",
                    $"C# enum '{expectedName}' ({expectedInt}) not found in Model/RecipeStatuses. Sync required.");
            }
        }

        // Check for model values not in C#
        foreach (var modelNode in modelValues)
        {
            if (!Enum.TryParse<RecipeStatuses>(modelNode.BrowseName, out _))
            {
                Log.Error("RecipeSchemaNetLogic",
                    $"Model enum value '{modelNode.BrowseName}' has no matching C# RecipeStatuses entry. Sync required.");
            }
        }
    }

    #region CreateRecipe

    /// <summary>
    /// Create a new recipe. Optionally copies from a source/template recipe.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult CreateRecipe(string recipeName, float recipeFamily,
        string sourceRecipeName = null, int initialStatusInt = 1)
    {
        // Validate configuration is loaded
        if (!_configLoader.IsValid)
            return RecipeOperationResult.Fail("ConfigurationInvalid", "YAML configuration is invalid or not loaded.");

        // Parse initial status (default: Draft=1)
        if (!RecipeHelpers.TryParseStatus(initialStatusInt, out RecipeStatuses initialStatus))
            return RecipeOperationResult.Fail("InvalidStatus", $"Invalid initialStatus value: {initialStatusInt}");

        // Only draft and template are valid initial statuses
        if (initialStatus != RecipeStatuses.Draft && initialStatus != RecipeStatuses.Template)
            return RecipeOperationResult.Fail("InvalidStatus", "Initial status must be Draft or Template.");

        // Validate recipe name
        if (string.IsNullOrWhiteSpace(recipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "Recipe name cannot be empty.");

        // Validate recipe family is configured
        int familyKey = (int)recipeFamily;
        if (_configLoader.GetFamily(familyKey) == null)
            return RecipeOperationResult.Fail("InvalidPhaseTypeForRecipeFamily", $"Recipe family {familyKey} is not configured.");

        // Check duplicate name
        if (RecipeExists(recipeName))
            return RecipeOperationResult.Fail("RecipeAlreadyExists", $"Recipe '{recipeName}' already exists.");

        // If source specified, verify authorization on source
        if (!string.IsNullOrWhiteSpace(sourceRecipeName))
        {
            var sourceStatus = GetRecipeStatus(sourceRecipeName);
            if (sourceStatus == null)
                return RecipeOperationResult.Fail("RecipeNotFound", $"Source recipe '{sourceRecipeName}' not found.");

            if (!IsAuthorized(sourceStatus.Value))
                return RecipeOperationResult.Fail("Unauthorized", $"User not authorized to manage source recipe (status={sourceStatus.Value}).");
        }

        // Create the recipe in RecipeX store
        string version = "1.0";
        var recipeId = new RecipeId { Name = recipeName, Version = version };

        CreateRecipeResultCode createResult;
        if (!string.IsNullOrWhiteSpace(sourceRecipeName))
        {
            // Duplicate from source
            var sourceId = GetLatestRecipeId(sourceRecipeName);
            if (sourceId == null)
                return RecipeOperationResult.Fail("RecipeNotFound", $"Source recipe '{sourceRecipeName}' not found.");

            var dupResult = _schema.DuplicateRecipe(sourceId, recipeId);
            if (dupResult != DuplicateRecipeResultCode.Success)
                return RecipeOperationResult.Fail("StoreError", $"DuplicateRecipe failed: {dupResult}");
        }
        else
        {
            // Create blank recipe
            createResult = _schema.CreateRecipe(recipeId);
            if (createResult != CreateRecipeResultCode.Success)
                return RecipeOperationResult.Fail("StoreError", $"CreateRecipe failed: {createResult}");

            // Initialize with one active step and rest as tail
            InitializeBlankRecipe(recipeId, familyKey);
        }

        // Set metadata (version is already in RecipeId.Version)
        SetRecipeMetadata(recipeId, initialStatus);

        // Apply enablement rules
        ApplyEnablementRulesInternal(recipeId, familyKey);

        // Validate
        var validation = ValidateRecipeInternal(recipeId, familyKey);
        if (!validation.IsValid)
        {
            // Rollback: delete the invalid recipe
            _schema.DeleteRecipe(recipeId);
            return RecipeOperationResult.FailValidation("ValidationFailed",
                "Created recipe failed validation and was rolled back.", validation.Errors);
        }

        return RecipeOperationResult.Ok($"Recipe '{recipeName}' created (v{version}, status={initialStatus}).");
    }

    #endregion

    #region UpdateRecipe

    /// <summary>
    /// Update a draft recipe in-place, or create a new revision from non-draft.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult UpdateRecipe(string sourceRecipeName, string newRecipeName, NodeId updatedModelRoot)
    {
        if (!_configLoader.IsValid)
            return RecipeOperationResult.Fail("ConfigurationInvalid", "YAML configuration is invalid or not loaded.");

        if (string.IsNullOrWhiteSpace(sourceRecipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "Source recipe name cannot be empty.");

        // Load source recipe
        var sourceId = GetLatestRecipeId(sourceRecipeName);
        if (sourceId == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Source recipe '{sourceRecipeName}' not found.");

        var status = GetRecipeStatus(sourceRecipeName);
        if (status == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Cannot read status for recipe '{sourceRecipeName}'.");

        // Auth check
        if (!IsAuthorized(status.Value))
            return RecipeOperationResult.Fail("Unauthorized", $"User not authorized to manage recipe (status={status.Value}).");

        var currentVersion = GetRecipeVersion(sourceRecipeName);
        if (currentVersion == null)
            return RecipeOperationResult.Fail("InvalidVersionFormat", "Cannot read version from source recipe.");

        int familyKey = (int)GetRecipeFamily(sourceId);

        if (RecipeHelpers.IsDirectlyEditable(status.Value))
        {
            // Direct edit on draft/template — update in place
            var transferResult = _schema.TransferFromTargetToStore(sourceId, updatedModelRoot, overwrite: true, ErrorPolicy.Strict);
            if (transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeUpdated &&
                transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeCreated)
            {
                return RecipeOperationResult.Fail("StoreError", $"TransferFromTargetToStore failed: {transferResult}");
            }

            // Re-apply enablement rules
            ApplyEnablementRulesInternal(sourceId, familyKey);

            // Validate
            var validation = ValidateRecipeInternal(sourceId, familyKey);
            if (!validation.IsValid)
                return RecipeOperationResult.FailValidation("InvalidStepSequence",
                    "Updated recipe failed validation.", validation.Errors);

            return RecipeOperationResult.Ok($"Recipe '{sourceRecipeName}' updated in place.");
        }
        else
        {
            // Non-editable: create new revision
            if (string.IsNullOrWhiteSpace(newRecipeName))
                return RecipeOperationResult.Fail("InvalidRecipeName", "newRecipeName is required when source is not directly editable.");

            if (RecipeExists(newRecipeName))
                return RecipeOperationResult.Fail("RecipeAlreadyExists", $"Recipe '{newRecipeName}' already exists.");

            // Compute next version
            var nextVersion = RecipeHelpers.ComputeNextVersion(currentVersion.Value, status.Value);
            var newId = new RecipeId { Name = newRecipeName, Version = nextVersion.ToString() };

            // Create new recipe by duplicating source
            var dupResult = _schema.DuplicateRecipe(sourceId, newId);
            if (dupResult != DuplicateRecipeResultCode.Success)
                return RecipeOperationResult.Fail("StoreError", $"DuplicateRecipe failed: {dupResult}");

            // Apply updated values from the model root
            var transferResult = _schema.TransferFromTargetToStore(newId, updatedModelRoot, overwrite: true, ErrorPolicy.Strict);
            if (transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeUpdated &&
                transferResult != TransferFromTargetToStoreResultCode.SuccessRecipeCreated)
            {
                _schema.DeleteRecipe(newId);
                return RecipeOperationResult.Fail("StoreError", $"TransferFromTargetToStore failed: {transferResult}");
            }

            // Set metadata on new recipe (version is already in newId.Version)
            SetRecipeMetadata(newId, RecipeStatuses.Draft);

            // Apply enablement
            ApplyEnablementRulesInternal(newId, familyKey);

            // Validate
            var validation = ValidateRecipeInternal(newId, familyKey);
            if (!validation.IsValid)
            {
                _schema.DeleteRecipe(newId);
                return RecipeOperationResult.FailValidation("ValidationFailed",
                    "New revision failed validation and was rolled back.", validation.Errors);
            }

            return RecipeOperationResult.Ok($"New revision '{newRecipeName}' (v{nextVersion}) created from '{sourceRecipeName}'.");
        }
    }

    #endregion

    #region DeleteRecipe

    /// <summary>
    /// Logical delete: sets recipe status to Archived.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult DeleteRecipe(string recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "Recipe name cannot be empty.");

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Recipe '{recipeName}' not found.");

        var status = GetRecipeStatus(recipeName);
        if (status == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Cannot read status for recipe '{recipeName}'.");

        // Auth check
        if (!IsAuthorized(status.Value))
            return RecipeOperationResult.Fail("Unauthorized", $"User not authorized to manage recipe (status={status.Value}).");

        // Already archived
        if (status.Value == RecipeStatuses.Archived)
            return RecipeOperationResult.Fail("InvalidStatusTransition", "Recipe is already archived.");

        // Set status to archived
        _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)RecipeStatuses.Archived);
        return RecipeOperationResult.Ok($"Recipe '{recipeName}' archived.");
    }

    #endregion

    #region UpdateRecipeStatus

    /// <summary>
    /// Advance recipe status along allowed lifecycle transitions.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult UpdateRecipeStatus(string recipeName, int newStatusInt)
    {
        if (string.IsNullOrWhiteSpace(recipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "Recipe name cannot be empty.");

        if (!RecipeHelpers.TryParseStatus(newStatusInt, out RecipeStatuses newStatus))
            return RecipeOperationResult.Fail("InvalidStatus", $"Invalid status value: {newStatusInt}");

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Recipe '{recipeName}' not found.");

        var currentStatus = GetRecipeStatus(recipeName);
        if (currentStatus == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Cannot read status for recipe '{recipeName}'.");

        // Auth check on current status
        if (!IsAuthorized(currentStatus.Value))
            return RecipeOperationResult.Fail("Unauthorized", $"User not authorized to manage recipe (status={currentStatus.Value}).");

        // Validate transition
        if (!RecipeHelpers.IsTransitionAllowed(currentStatus.Value, newStatus))
            return RecipeOperationResult.Fail("InvalidStatusTransition",
                $"Transition from {currentStatus.Value} to {newStatus} is not allowed.");

        // If releasing, run full validation
        if (newStatus == RecipeStatuses.Released)
        {
            int familyKey = (int)GetRecipeFamily(recipeId);
            var validation = ValidateRecipeInternal(recipeId, familyKey);
            if (!validation.IsValid)
                return RecipeOperationResult.FailValidation("ValidationFailed",
                    "Recipe cannot be released due to validation errors.", validation.Errors);
        }

        // Apply status change
        _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)newStatus);
        return RecipeOperationResult.Ok($"Recipe '{recipeName}' status changed to {newStatus}.");
    }

    #endregion

    #region DuplicateRecipe

    /// <summary>
    /// Duplicate a recipe with a new name (and optionally a new family).
    /// Always creates as Draft v1.0.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult DuplicateRecipe(string sourceRecipeName, string newRecipeName, float newRecipeFamily = -1f)
    {
        if (!_configLoader.IsValid)
            return RecipeOperationResult.Fail("ConfigurationInvalid", "YAML configuration is invalid or not loaded.");

        if (string.IsNullOrWhiteSpace(sourceRecipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "Source recipe name cannot be empty.");
        if (string.IsNullOrWhiteSpace(newRecipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "New recipe name cannot be empty.");

        var sourceId = GetLatestRecipeId(sourceRecipeName);
        if (sourceId == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Source recipe '{sourceRecipeName}' not found.");

        var sourceStatus = GetRecipeStatus(sourceRecipeName);
        if (sourceStatus == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Cannot read status for source recipe.");

        // Auth on source
        if (!IsAuthorized(sourceStatus.Value))
            return RecipeOperationResult.Fail("Unauthorized", $"User not authorized to manage source recipe (status={sourceStatus.Value}).");

        if (RecipeExists(newRecipeName))
            return RecipeOperationResult.Fail("RecipeAlreadyExists", $"Recipe '{newRecipeName}' already exists.");

        // Determine family
        float family = newRecipeFamily > 0 ? newRecipeFamily : GetRecipeFamily(sourceId);
        int familyKey = (int)family;
        if (_configLoader.GetFamily(familyKey) == null)
            return RecipeOperationResult.Fail("InvalidPhaseTypeForRecipeFamily", $"Recipe family {familyKey} is not configured.");

        // Duplicate in store
        string version = "1.0";
        var newId = new RecipeId { Name = newRecipeName, Version = version };
        var dupResult = _schema.DuplicateRecipe(sourceId, newId);
        if (dupResult != DuplicateRecipeResultCode.Success)
            return RecipeOperationResult.Fail("StoreError", $"DuplicateRecipe failed: {dupResult}");

        // Set metadata (version is already in newId.Version)
        SetRecipeMetadata(newId, RecipeStatuses.Draft);

        // If family changed, recompute enablement
        ApplyEnablementRulesInternal(newId, familyKey);

        // Validate
        var validation = ValidateRecipeInternal(newId, familyKey);
        if (!validation.IsValid)
        {
            _schema.DeleteRecipe(newId);
            return RecipeOperationResult.FailValidation("ValidationFailed",
                "Duplicated recipe failed validation and was rolled back.", validation.Errors);
        }

        return RecipeOperationResult.Ok($"Recipe '{newRecipeName}' duplicated from '{sourceRecipeName}' (v{version}, Draft).");
    }

    #endregion

    #region CompareRecipes

    /// <summary>
    /// Compare two recipes, returning all differences on the full model.
    /// Read-only operation — no auth check required.
    /// </summary>
    [ExportMethod]
    public RecipeComparisonResult CompareRecipes(string leftRecipeName, string rightRecipeName)
    {
        var result = new RecipeComparisonResult();

        if (string.IsNullOrWhiteSpace(leftRecipeName) || string.IsNullOrWhiteSpace(rightRecipeName))
        {
            result.Success = false;
            result.ErrorCode = "InvalidRecipeName";
            result.Message = "Both recipe names are required.";
            return result;
        }

        var leftId = GetLatestRecipeId(leftRecipeName);
        var rightId = GetLatestRecipeId(rightRecipeName);

        if (leftId == null)
        {
            result.Success = false;
            result.ErrorCode = "RecipeNotFound";
            result.Message = $"Left recipe '{leftRecipeName}' not found.";
            return result;
        }
        if (rightId == null)
        {
            result.Success = false;
            result.ErrorCode = "RecipeNotFound";
            result.Message = $"Right recipe '{rightRecipeName}' not found.";
            return result;
        }

        // Compare metadata
        CompareMetadata(leftId, rightId, result.Differences);

        // Compare all data items
        CompareDataItems(leftId, rightId, result.Differences);

        result.Success = true;
        result.Message = $"Comparison complete. {result.Differences.Count} difference(s) found.";
        return result;
    }

    #endregion

    #region ValidateRecipe

    /// <summary>
    /// Full recipe validation per spec rules.
    /// </summary>
    [ExportMethod]
    public RecipeValidationResult ValidateRecipe(string recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName))
            return RecipeValidationResult.Invalid(new List<string> { "Recipe name cannot be empty." });

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
            return RecipeValidationResult.Invalid(new List<string> { $"Recipe '{recipeName}' not found." });

        int familyKey = (int)GetRecipeFamily(recipeId);
        return ValidateRecipeInternal(recipeId, familyKey);
    }

    #endregion

    #region ApplyRecipeEnablementRules

    /// <summary>
    /// Apply PhaseType/RecipeFamily enablement rules to a recipe. Idempotent.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult ApplyRecipeEnablementRules(string recipeName)
    {
        if (!_configLoader.IsValid)
            return RecipeOperationResult.Fail("ConfigurationInvalid", "YAML configuration is invalid or not loaded.");

        if (string.IsNullOrWhiteSpace(recipeName))
            return RecipeOperationResult.Fail("InvalidRecipeName", "Recipe name cannot be empty.");

        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
            return RecipeOperationResult.Fail("RecipeNotFound", $"Recipe '{recipeName}' not found.");

        int familyKey = (int)GetRecipeFamily(recipeId);
        ApplyEnablementRulesInternal(recipeId, familyKey);

        return RecipeOperationResult.Ok("Enablement rules applied.");
    }

    #endregion

    #region Internal methods

    /// <summary>
    /// Check if a recipe with the given name exists in the store.
    /// </summary>
    private bool RecipeExists(string recipeName)
    {
        var recipes = _schema.GetRecipes();
        if (recipes.ResultCode != GetRecipesResultCode.Success)
            return false;

        return recipes.Recipes.Any(r => r.RecipeId.Name == recipeName);
    }

    /// <summary>
    /// Get the latest RecipeId for a recipe by name.
    /// </summary>
    private RecipeId GetLatestRecipeId(string recipeName)
    {
        var recipes = _schema.GetRecipes();
        if (recipes.ResultCode != GetRecipesResultCode.Success)
            return null;

        var match = recipes.Recipes.FirstOrDefault(r => r.RecipeId.Name == recipeName);
        return match?.RecipeId;
    }

    /// <summary>
    /// Read recipe status from metadata.
    /// </summary>
    private RecipeStatuses? GetRecipeStatus(string recipeName)
    {
        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
            return null;

        var metaResult = _schema.GetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus);
        if (metaResult.ResultCode != GetRecipeMetadataValueResultCode.Success)
            return null;

        if (metaResult.MetadataValue?.Value is int statusInt)
        {
            if (RecipeHelpers.TryParseStatus(statusInt, out RecipeStatuses status))
                return status;
        }

        // Try string parse
        if (metaResult.MetadataValue?.Value is string statusStr)
        {
            if (RecipeHelpers.TryParseStatus(statusStr, out RecipeStatuses status))
                return status;
        }

        return null;
    }

    /// <summary>
    /// Read recipe version from metadata.
    /// </summary>
    private RecipeVersion? GetRecipeVersion(string recipeName)
    {
        var recipeId = GetLatestRecipeId(recipeName);
        if (recipeId == null)
            return null;

        // Version is stored in RecipeId.Version
        if (RecipeVersion.TryParse(recipeId.Version, out RecipeVersion version))
            return version;

        return null;
    }

    /// <summary>
    /// Read RecipeFamily from metadata or data item.
    /// </summary>
    private float GetRecipeFamily(RecipeId recipeId)
    {
        // RecipeFamily is a data item at Parameters1/RecipeFamily
        var result = _schema.GetRecipeDataItemValue(
            recipeId,
            new string[] { "Parameters1" },
            new string[] { "RecipeFamily" },
            new ElementAccessStruct());

        if (result.ResultCode == GetRecipeDataItemValueResultCode.Success && result.DataItemValue != null)
            return Convert.ToSingle(result.DataItemValue);

        return 0f;
    }

    /// <summary>
    /// Set standard metadata fields on a recipe.
    /// Version is stored natively in RecipeId.Version (DB column), not as metadata.
    /// </summary>
    private void SetRecipeMetadata(RecipeId recipeId, RecipeStatuses status)
    {
        _schema.SetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus, (int)status);
    }

    /// <summary>
    /// Initialize a blank recipe with 1 active step + 19 tail steps.
    /// </summary>
    private void InitializeBlankRecipe(RecipeId recipeId, int familyKey)
    {
        // Set RecipeFamily on the recipe
        _schema.SetRecipeDataItemValue(recipeId,
            new string[] { "Parameters1" },
            new string[] { "RecipeFamily" },
            new ElementAccessStruct(),
            (float)familyKey);

        // Set first step active (PhaseType=1), rest to 0
        for (int i = 1; i <= 20; i++)
        {
            string stepName = $"RecipeStepRSA{i}";
            float phaseType = (i == 1) ? 1f : 0f;

            _schema.SetRecipeDataItemValue(recipeId,
                new string[] { stepName },
                new string[] { "PhaseType" },
                new ElementAccessStruct(),
                phaseType);

            _schema.SetRecipeDataItemValue(recipeId,
                new string[] { stepName },
                new string[] { "StepEnabled" },
                new ElementAccessStruct(),
                (i == 1));
        }
    }

    /// <summary>
    /// Apply enablement rules to all steps in a recipe based on family config.
    /// Idempotent: safe to call multiple times.
    /// </summary>
    private void ApplyEnablementRulesInternal(RecipeId recipeId, int familyKey)
    {
        for (int i = 1; i <= 20; i++)
        {
            string stepName = $"RecipeStepRSA{i}";

            // Read PhaseType
            var ptResult = _schema.GetRecipeDataItemValue(recipeId,
                new string[] { stepName },
                new string[] { "PhaseType" },
                new ElementAccessStruct());

            float phaseType = 0f;
            if (ptResult.ResultCode == GetRecipeDataItemValueResultCode.Success && ptResult.DataItemValue != null)
                phaseType = Convert.ToSingle(ptResult.DataItemValue);

            if (phaseType == 0f)
            {
                // Unused step: disable everything
                _schema.SetRecipeDataItemValue(recipeId,
                    new string[] { stepName },
                    new string[] { "StepEnabled" },
                    new ElementAccessStruct(), false);

                SetAllParametersEnabled(recipeId, stepName, false);
            }
            else
            {
                int ptInt = (int)phaseType;
                bool phaseAllowed = _configLoader.IsPhaseTypeAllowed(familyKey, ptInt);

                // Step enabled only if phase is allowed for this family
                _schema.SetRecipeDataItemValue(recipeId,
                    new string[] { stepName },
                    new string[] { "StepEnabled" },
                    new ElementAccessStruct(), phaseAllowed);

                if (phaseAllowed)
                {
                    // Enable only configured parameters
                    var enabledParams = _configLoader.GetEnabledParameters(familyKey, ptInt);
                    SetParametersEnabledByConfig(recipeId, stepName, enabledParams);
                }
                else
                {
                    // Phase not allowed: disable all parameters, log warning
                    SetAllParametersEnabled(recipeId, stepName, false);
                    Log.Warning("RecipeSchemaNetLogic",
                        $"Step {stepName}: PhaseType {ptInt} is not allowed for family {familyKey}.");
                }
            }
        }
    }

    /// <summary>
    /// Set all step parameters to enabled/disabled.
    /// </summary>
    private void SetAllParametersEnabled(RecipeId recipeId, string stepName, bool enabled)
    {
        for (int p = 1; p <= 20; p++)
        {
            string paramName = $"StepParameter{p}";
            _schema.SetRecipeDataItemValue(recipeId,
                new string[] { stepName, paramName },
                new string[] { "ParameterEnabled" },
                new ElementAccessStruct(), enabled);
        }
    }

    /// <summary>
    /// Enable only the parameters in the config list; disable all others.
    /// </summary>
    private void SetParametersEnabledByConfig(RecipeId recipeId, string stepName, List<int> enabledIndexes)
    {
        var enabledSet = new HashSet<int>(enabledIndexes);
        for (int p = 1; p <= 20; p++)
        {
            string paramName = $"StepParameter{p}";
            bool enabled = enabledSet.Contains(p);
            _schema.SetRecipeDataItemValue(recipeId,
                new string[] { stepName, paramName },
                new string[] { "ParameterEnabled" },
                new ElementAccessStruct(), enabled);
        }
    }

    /// <summary>
    /// Internal validation implementing all spec rules.
    /// </summary>
    private RecipeValidationResult ValidateRecipeInternal(RecipeId recipeId, int familyKey)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Rule 1: recipe exists (already guaranteed if we have recipeId)

        // Rule 2-4: Version format
        if (!RecipeVersion.TryParse(recipeId.Version, out _))
            errors.Add($"Version '{recipeId.Version}' is not valid major.minor format.");

        // Rule 4: Status
        var statusMeta = _schema.GetRecipeMetadataValue(recipeId, RecipeHelpers.MetadataStatus);
        if (statusMeta.ResultCode != GetRecipeMetadataValueResultCode.Success)
        {
            errors.Add("Status metadata is missing.");
        }
        else
        {
            if (statusMeta.MetadataValue?.Value is int statusInt)
            {
                if (!RecipeHelpers.TryParseStatus(statusInt, out _))
                    errors.Add($"Status value {statusInt} is not a valid RecipeStatuses value.");
            }
            else
            {
                errors.Add("Status metadata has unexpected type.");
            }
        }

        // Rule 5: RecipeFamily configured
        if (_configLoader.GetFamily(familyKey) == null)
            errors.Add($"RecipeFamily {familyKey} is not configured.");

        // Rules 6-13: Step sequence and structure
        var phaseTypes = new List<float>();
        for (int i = 1; i <= 20; i++)
        {
            string stepName = $"RecipeStepRSA{i}";
            var ptResult = _schema.GetRecipeDataItemValue(recipeId,
                new string[] { stepName },
                new string[] { "PhaseType" },
                new ElementAccessStruct());

            if (ptResult.ResultCode != GetRecipeDataItemValueResultCode.Success)
            {
                errors.Add($"Step {i}: cannot read PhaseType.");
                phaseTypes.Add(-1f); // sentinel
            }
            else
            {
                phaseTypes.Add(Convert.ToSingle(ptResult.DataItemValue));
            }
        }

        // Validate step sequence
        var (seqValid, seqErrors) = RecipeHelpers.ValidateStepSequence(phaseTypes);
        errors.AddRange(seqErrors);

        // Rule 12: Each active PhaseType must be allowed for family
        if (_configLoader.GetFamily(familyKey) != null)
        {
            for (int i = 0; i < phaseTypes.Count; i++)
            {
                float pt = phaseTypes[i];
                if (pt != 0f && pt > 0f)
                {
                    if (!_configLoader.IsPhaseTypeAllowed(familyKey, (int)pt))
                        errors.Add($"Step {i + 1}: PhaseType {(int)pt} is not allowed for family {familyKey}.");
                }
            }
        }

        // Rule 14: Parameter values within EURange (best effort — check if EURange available)
        // Skipped for now — EURange not always available in RecipeX data items

        if (errors.Count > 0)
            return RecipeValidationResult.Invalid(errors, warnings);

        return RecipeValidationResult.Valid();
    }

    /// <summary>
    /// Compare metadata between two recipes.
    /// </summary>
    private void CompareMetadata(RecipeId leftId, RecipeId rightId, List<RecipeDifference> diffs)
    {
        // Compare version
        if (leftId.Version != rightId.Version)
        {
            diffs.Add(new RecipeDifference
            {
                Path = "Version",
                LeftValue = leftId.Version,
                RightValue = rightId.Version
            });
        }

        // Compare status
        var leftStatus = _schema.GetRecipeMetadataValue(leftId, RecipeHelpers.MetadataStatus);
        var rightStatus = _schema.GetRecipeMetadataValue(rightId, RecipeHelpers.MetadataStatus);

        string leftStatusStr = leftStatus.MetadataValue?.Value?.ToString() ?? "N/A";
        string rightStatusStr = rightStatus.MetadataValue?.Value?.ToString() ?? "N/A";

        if (leftStatusStr != rightStatusStr)
        {
            diffs.Add(new RecipeDifference
            {
                Path = "Status",
                LeftValue = leftStatusStr,
                RightValue = rightStatusStr
            });
        }
    }

    /// <summary>
    /// Compare all data items between two recipes.
    /// </summary>
    private void CompareDataItems(RecipeId leftId, RecipeId rightId, List<RecipeDifference> diffs)
    {
        // Get data items structure from left recipe
        var dataItemsResult = _schema.GetDataItems(leftId);
        if (dataItemsResult.ResultCode != GetDataItemsResultCode.Success)
            return;

        foreach (var dataItem in dataItemsResult.DataItems)
        {
            var leftVal = _schema.GetRecipeDataItemValue(leftId,
                dataItem.ItemRelativeBrowsePath,
                dataItem.DataItemRelativeBrowsePath,
                dataItem.ElementAccess);

            var rightVal = _schema.GetRecipeDataItemValue(rightId,
                dataItem.ItemRelativeBrowsePath,
                dataItem.DataItemRelativeBrowsePath,
                dataItem.ElementAccess);

            string leftStr = leftVal.ResultCode == GetRecipeDataItemValueResultCode.Success
                ? leftVal.DataItemValue?.ToString() ?? "null"
                : "N/A";
            string rightStr = rightVal.ResultCode == GetRecipeDataItemValueResultCode.Success
                ? rightVal.DataItemValue?.ToString() ?? "null"
                : "N/A";

            if (leftStr != rightStr)
            {
                string path = string.Join("/", dataItem.ItemRelativeBrowsePath);
                if (dataItem.DataItemRelativeBrowsePath != null && dataItem.DataItemRelativeBrowsePath.Length > 0)
                    path += "/" + string.Join("/", dataItem.DataItemRelativeBrowsePath);

                diffs.Add(new RecipeDifference
                {
                    Path = path,
                    LeftValue = leftStr,
                    RightValue = rightStr
                });
            }
        }
    }

    #endregion
}
