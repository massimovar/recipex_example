#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.RecipeX;
using FTOptix.NetLogic;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.Recipe;
using FTOptix.Alarm;
#endregion

/// <summary>
/// Retrieves the active recipe status by reading bool metadata fields.
/// Lives inside a UI object. Requires these variables on LogicObject:
///   - CustomRecipeSchemaExtention (NodeId) → points to CustomRecipeSchemaExtention NetLogic node
///   - RecipeName (string) → selected recipe name
///   - RecipeVersion (string) → selected recipe version
///   - RecipeActualStatus (string) → output, written with active status name
/// </summary>
public class CustomRecipeStatusRetriever : BaseNetLogic
{
    private const string Tag = "RecipeStatusRetriever";

    public override void Start()
    {
        GetRecipeStatus();
    }

    public override void Stop() { }

    [ExportMethod]
    public void GetRecipeStatus()
    {
        // Resolve CustomRecipeSchemaExtention NetLogic directly via NodeId variable
        var extVar = LogicObject.GetVariable("CustomRecipeSchemaExtention");
        if (extVar == null)
        { Log.Error(Tag, "CustomRecipeSchemaExtention variable not found on LogicObject."); return; }

        NodeId extNodeId = (NodeId)extVar.Value;
        if (extNodeId == null || extNodeId == NodeId.Empty)
        { Log.Error(Tag, "CustomRecipeSchemaExtention NodeId is empty."); return; }

        var schemaExt = InformationModel.Get<NetLogicObject>(extNodeId);
        if (schemaExt == null)
        { Log.Error(Tag, "Cannot resolve CustomRecipeSchemaExtention node."); return; }

        // Read RecipeName and RecipeVersion string variables
        string recipeName = LogicObject.GetVariable("RecipeName")?.Value ?? string.Empty;
        string recipeVersion = LogicObject.GetVariable("RecipeVersion")?.Value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(recipeName) || string.IsNullOrWhiteSpace(recipeVersion))
        { Log.Warning(Tag, "RecipeName or RecipeVersion is empty."); return; }

        // Call GetRecipeStatus ExportMethod on CustomRecipeSchemaExtention
        object[] inputArgs = new object[] { recipeName, recipeVersion };
        schemaExt.ExecuteMethod("GetRecipeStatus", inputArgs, out object[] outputArgs);

        string foundStatus = (string)outputArgs[0];
        bool ok = (bool)outputArgs[1];

        // Write result to RecipeActualStatus output variable
        var outputVar = LogicObject.GetVariable("RecipeActualStatus");
        if (outputVar == null)
        { Log.Error(Tag, "RecipeActualStatus variable not found on LogicObject."); return; }

        outputVar.Value = ok ? foundStatus : string.Empty;

        if (ok)
            Log.Info(Tag, $"Status for '{recipeName}' v{recipeVersion}: {foundStatus}");
        else
            Log.Warning(Tag, $"No active status found for '{recipeName}' v{recipeVersion}.");
    }
}
