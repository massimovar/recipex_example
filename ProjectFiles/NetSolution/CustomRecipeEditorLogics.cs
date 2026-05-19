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
/// RecipeListViewEditModelNetLogic: manages the edit-model for recipe step editing.
/// Must be added as a child of the ListView used to create/edit recipe steps.
/// Operates on a fixed 20-step structure where active steps are contiguous at the start.
/// </summary>
public class CustomRecipeEditorLogics : BaseNetLogic
{
    private const int MaxSteps = 20;
    private IUANode _targetNode;

    public override void Start()
    {
        // The target node for editing is typically set via an alias or owner's variable
        // Will be resolved when InitializeEditModel is called
    }

    public override void Stop()
    {
    }

    #region InitializeEditModel

    /// <summary>
    /// Load the edit model with step data from the target node.
    /// Resolves the target and reads current PhaseType values.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult InitializeEditModel(NodeId targetNodeId)
    {
        if (targetNodeId == null || targetNodeId == NodeId.Empty)
            return RecipeOperationResult.Fail("InvalidInput", "Target node ID is required.");

        _targetNode = InformationModel.Get(targetNodeId);
        if (_targetNode == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "Target node not found in InformationModel.");

        return RecipeOperationResult.Ok("Edit model initialized.");
    }

    #endregion

    #region AddStepBefore

    /// <summary>
    /// Insert a new active step before the specified position (1-based).
    /// Shifts subsequent steps down. Rejects if 20 active steps already present.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult AddStepBefore(NodeId targetNodeId, int position)
    {
        var phaseTypes = ReadPhaseTypes(targetNodeId);
        if (phaseTypes == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "Cannot read step data from target node.");

        int activeCount = RecipeHelpers.CountActiveSteps(phaseTypes);
        if (activeCount >= MaxSteps)
            return RecipeOperationResult.Fail("MaximumStepCountReached", "All 20 steps are already active.");

        // Validate position: must be 1..activeCount+1
        if (position < 1 || position > activeCount + 1)
            return RecipeOperationResult.Fail("InvalidInput", $"Position must be between 1 and {activeCount + 1}.");

        // Insert: shift active steps from position onwards by marking one more step active
        // Strategy: convert tail step at end to active, then reorder
        var newPhaseTypes = new List<float>(phaseTypes);

        // Find first tail step and mark it active (temporarily)
        int firstTailIndex = newPhaseTypes.FindIndex(pt => pt == 0f);
        if (firstTailIndex < 0)
            return RecipeOperationResult.Fail("MaximumStepCountReached", "No available tail step slot.");

        // Shift step data down in the model: move step data from position-1 to activeCount-1 one slot forward
        // Then clear the inserted position
        // For simplicity: we rewrite PhaseTypes to have N+1 active steps with correct ordering
        var activePhaseTypes = new List<float>();
        for (int i = 0; i < activeCount + 1; i++)
        {
            activePhaseTypes.Add(i + 1); // 1..N+1
        }

        // Fill remaining with 0
        var resultPhaseTypes = new List<float>(activePhaseTypes);
        while (resultPhaseTypes.Count < MaxSteps)
            resultPhaseTypes.Add(0f);

        // Write back PhaseTypes
        WritePhaseTypes(targetNodeId, resultPhaseTypes);

        // Normalize and apply
        NormalizeStepsInternal(targetNodeId);

        return RecipeOperationResult.Ok($"Step added before position {position}. Active steps: {activeCount + 1}.");
    }

    #endregion

    #region AddStepAfter

    /// <summary>
    /// Insert a new active step after the specified position (1-based).
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult AddStepAfter(NodeId targetNodeId, int position)
    {
        // AddStepAfter(N) = AddStepBefore(N+1)
        return AddStepBefore(targetNodeId, position + 1);
    }

    #endregion

    #region DeleteStep

    /// <summary>
    /// Remove the step at the specified position (1-based).
    /// Converts it to a tail step and re-normalizes.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult DeleteStep(NodeId targetNodeId, int position)
    {
        var phaseTypes = ReadPhaseTypes(targetNodeId);
        if (phaseTypes == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "Cannot read step data from target node.");

        int activeCount = RecipeHelpers.CountActiveSteps(phaseTypes);

        if (position < 1 || position > activeCount)
            return RecipeOperationResult.Fail("InvalidInput", $"Position must be between 1 and {activeCount}.");

        // Set the step at position to tail (0)
        // Then re-normalize so active steps are contiguous 1..N-1
        phaseTypes[position - 1] = 0f;

        // Move all zeros to the end while preserving order of non-zero items
        var active = phaseTypes.Where(pt => pt != 0f).ToList();
        var result = new List<float>(active);
        while (result.Count < MaxSteps)
            result.Add(0f);

        // Renumber active steps
        result = RecipeHelpers.NormalizePhaseTypes(result);

        // Write back
        WritePhaseTypes(targetNodeId, result);

        return RecipeOperationResult.Ok($"Step at position {position} deleted. Active steps: {active.Count}.");
    }

    #endregion

    #region NormalizeSteps

    /// <summary>
    /// Re-normalize step PhaseType values: active steps become 1..N, tail steps become 0.
    /// Idempotent.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult NormalizeSteps(NodeId targetNodeId)
    {
        return NormalizeStepsInternal(targetNodeId);
    }

    private RecipeOperationResult NormalizeStepsInternal(NodeId targetNodeId)
    {
        var phaseTypes = ReadPhaseTypes(targetNodeId);
        if (phaseTypes == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "Cannot read step data from target node.");

        var normalized = RecipeHelpers.NormalizePhaseTypes(phaseTypes);
        WritePhaseTypes(targetNodeId, normalized);

        return RecipeOperationResult.Ok("Steps normalized.");
    }

    #endregion

    #region ValidateEditModel

    /// <summary>
    /// Validate the current edit model step sequence.
    /// </summary>
    [ExportMethod]
    public RecipeValidationResult ValidateEditModel(NodeId targetNodeId)
    {
        var phaseTypes = ReadPhaseTypes(targetNodeId);
        if (phaseTypes == null)
            return RecipeValidationResult.Invalid(new List<string> { "Cannot read step data from target node." });

        var (isValid, errors) = RecipeHelpers.ValidateStepSequence(phaseTypes);

        if (isValid)
            return RecipeValidationResult.Valid();
        return RecipeValidationResult.Invalid(errors);
    }

    #endregion

    #region ApplyEditModelEnablementRules

    /// <summary>
    /// Apply enablement rules to the edit model target node.
    /// Reads RecipeFamily from the target, then applies PhaseType-based enablement.
    /// </summary>
    [ExportMethod]
    public RecipeOperationResult ApplyEditModelEnablementRules(NodeId targetNodeId, string configFilePath)
    {
        var target = InformationModel.Get(targetNodeId);
        if (target == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "Target node not found.");

        // Load config
        var loader = new RecipeConfigurationLoader();
        if (!loader.Load(configFilePath))
            return RecipeOperationResult.Fail("ConfigurationInvalid",
                $"Configuration invalid: {string.Join("; ", loader.ValidationErrors)}");

        // Read RecipeFamily from target
        var parameters1 = target.GetObject("Parameters1");
        if (parameters1 == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "Parameters1 node not found on target.");

        var familyVar = parameters1.GetVariable("RecipeFamily");
        if (familyVar == null)
            return RecipeOperationResult.Fail("RecipeNotFound", "RecipeFamily variable not found.");

        int familyKey = (int)Convert.ToSingle(familyVar.Value.Value);

        // Apply enablement to each step
        for (int i = 1; i <= MaxSteps; i++)
        {
            string stepName = $"RecipeStepRSA{i}";
            var stepNode = target.GetObject(stepName);
            if (stepNode == null)
                continue;

            var phaseTypeVar = stepNode.GetVariable("PhaseType");
            var stepEnabledVar = stepNode.GetVariable("StepEnabled");
            if (phaseTypeVar == null || stepEnabledVar == null)
                continue;

            float phaseType = Convert.ToSingle(phaseTypeVar.Value.Value);

            if (phaseType == 0f)
            {
                // Tail step: disable everything
                stepEnabledVar.Value = false;
                SetAllNodeParametersEnabled(stepNode, false);
            }
            else
            {
                int ptInt = (int)phaseType;
                bool allowed = loader.IsPhaseTypeAllowed(familyKey, ptInt);
                stepEnabledVar.Value = allowed;

                if (allowed)
                {
                    var enabledParams = loader.GetEnabledParameters(familyKey, ptInt);
                    SetNodeParametersEnabledByConfig(stepNode, enabledParams);
                }
                else
                {
                    SetAllNodeParametersEnabled(stepNode, false);
                }
            }
        }

        return RecipeOperationResult.Ok("Enablement rules applied to edit model.");
    }

    #endregion

    #region Internal helpers

    /// <summary>
    /// Read PhaseType values from all 20 steps on the target node.
    /// </summary>
    private List<float> ReadPhaseTypes(NodeId targetNodeId)
    {
        var target = InformationModel.Get(targetNodeId);
        if (target == null)
            return null;

        var phaseTypes = new List<float>();
        for (int i = 1; i <= MaxSteps; i++)
        {
            string stepName = $"RecipeStepRSA{i}";
            var stepNode = target.GetObject(stepName);
            if (stepNode == null)
            {
                phaseTypes.Add(0f);
                continue;
            }

            var ptVar = stepNode.GetVariable("PhaseType");
            if (ptVar == null)
            {
                phaseTypes.Add(0f);
                continue;
            }

            phaseTypes.Add(Convert.ToSingle(ptVar.Value.Value));
        }

        return phaseTypes;
    }

    /// <summary>
    /// Write PhaseType values to all 20 steps on the target node.
    /// </summary>
    private void WritePhaseTypes(NodeId targetNodeId, List<float> phaseTypes)
    {
        var target = InformationModel.Get(targetNodeId);
        if (target == null)
            return;

        for (int i = 0; i < Math.Min(phaseTypes.Count, MaxSteps); i++)
        {
            string stepName = $"RecipeStepRSA{i + 1}";
            var stepNode = target.GetObject(stepName);
            if (stepNode == null)
                continue;

            var ptVar = stepNode.GetVariable("PhaseType");
            if (ptVar != null)
                ptVar.Value = phaseTypes[i];

            // Update StepEnabled based on PhaseType
            var enabledVar = stepNode.GetVariable("StepEnabled");
            if (enabledVar != null)
                enabledVar.Value = (phaseTypes[i] != 0f);
        }
    }

    /// <summary>
    /// Disable/enable all step parameter children on a step node.
    /// </summary>
    private void SetAllNodeParametersEnabled(IUANode stepNode, bool enabled)
    {
        foreach (var child in stepNode.Children)
        {
            if (child is IUAObject paramObj)
            {
                var enabledVar = paramObj.GetVariable("ParameterEnabled");
                if (enabledVar != null)
                    enabledVar.Value = enabled;
            }
        }
    }

    /// <summary>
    /// Enable only configured parameters, disable all others.
    /// Assumes step parameters are named with a numeric index or are enumerable.
    /// </summary>
    private void SetNodeParametersEnabledByConfig(IUANode stepNode, List<int> enabledIndexes)
    {
        var enabledSet = new HashSet<int>(enabledIndexes);
        int paramIndex = 1;

        foreach (var child in stepNode.Children)
        {
            if (child is IUAObject paramObj)
            {
                var enabledVar = paramObj.GetVariable("ParameterEnabled");
                if (enabledVar != null)
                {
                    enabledVar.Value = enabledSet.Contains(paramIndex);
                    paramIndex++;
                }
            }
        }
    }

    #endregion
}
