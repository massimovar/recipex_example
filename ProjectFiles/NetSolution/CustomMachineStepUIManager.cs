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
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.Recipe;
using OpcUa = UAManagedCore.OpcUa;
#endregion

/// <summary>
/// Per-step NetLogic for recipe EditModel step manipulation.
/// Ordering invariant: steps are always ordered by their fixed StepIndex (1-based, immutable).
/// Active steps (PhaseType != 0) occupy the front positions by StepIndex order.
/// Inactive steps (PhaseType == 0) are pushed to the end positions.
/// All operations move actual step DATA (including PhaseType) between physical slots.
/// StepIndex is never modified — it is the fixed slot identifier.
/// </summary>
public class CustomMachineStepUIManager : BaseNetLogic
{
    private const string LogCategory = "RecipeEditor";

    // Resolved dynamically from RSAMachine type at Start()
    private int MaxSteps;
    private string[] ParameterObjects;

    public override void Start()
    {
        MaxSteps = 20; // safe default
        ParameterObjects = Array.Empty<string>();

        // Resolve RSAMachine type from LogicObject variable
        var rsaVar = LogicObject.GetVariable("RSAMachine");
        if (rsaVar == null)
        {
            Log.Error(LogCategory, "Start: RSAMachine variable not found on LogicObject.");
            return;
        }

        NodeId rsaTypeId = (NodeId)rsaVar.Value;
        if (rsaTypeId == null || rsaTypeId == NodeId.Empty)
        {
            Log.Error(LogCategory, "Start: RSAMachine NodeId is empty.");
            return;
        }

        var rsaType = InformationModel.Get(rsaTypeId);
        if (rsaType == null)
        {
            Log.Error(LogCategory, "Start: cannot resolve RSAMachine type node.");
            return;
        }

        // Count RecipeStepRSA children → MaxSteps
        var stepChildren = rsaType.Children
            .OfType<IUANode>()
            .Where(n => n.BrowseName.StartsWith("RecipeStepRSA", StringComparison.Ordinal))
            .ToList();

        if (stepChildren.Count > 0)
            MaxSteps = stepChildren.Count;

        // Get parameter object names from first step's RecipeStepParameter children
        var firstStep = stepChildren.FirstOrDefault();
        if (firstStep != null)
        {
            ParameterObjects = firstStep.Children
                .OfType<IUAObject>()
                .Where(o => o.Children.OfType<IUAVariable>().Any(v => v.BrowseName == "ParameterValue"))
                .Select(o => o.BrowseName)
                .ToArray();
        }

        Log.Info(LogCategory, $"Start: MaxSteps={MaxSteps}, ParameterObjects=[{string.Join(", ", ParameterObjects)}]");
    }

    public override void Stop() { }

    #region ExportMethods

    /// <summary>
    /// Insert a new empty step before this step's StepIndex position.
    /// Shifts data from this position to last active rightward, clears the opened slot.
    /// Not allowed on StepIndex == 1 (first step) or inactive steps (PhaseType == 0).
    /// </summary>
    [ExportMethod]
    public void AddStepBefore(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "AddStepBefore: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "AddStepBefore: cannot resolve target node."); return; }

        // Use StepIndex for position determination
        int myPosition = GetStepIndex(myStep);
        float myPT = GetPhaseType(myStep);

        // Block on first step or inactive step
        if (myPosition <= 1 || myPT == 0f)
        {
            Log.Warning(LogCategory, "AddStepBefore: not allowed on first step or inactive step.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);

        // Cannot add if all slots are occupied
        if (activeCount >= MaxSteps)
        {
            Log.Warning(LogCategory, "AddStepBefore: max steps reached.");
            return;
        }

        // Shift data RIGHT: from last active slot down to myPosition, copy slot[i-1] -> slot[i]
        // This opens a gap at myPosition (1-based)
        for (int i = activeCount; i >= myPosition; i--)
        {
            CopyStepContent(allSteps[i - 1], allSteps[i]);
        }

        // Clear the opened slot and mark it active (PhaseType != 0)
        ClearStepContent(allSteps[myPosition - 1]);
        SetPhaseType(allSteps[myPosition - 1], 1f);

        // Normalize: ensure all PT==0 content is at the end
        EnsureInactiveAtEnd(allSteps);

        success = true;
        Log.Info(LogCategory, $"AddStepBefore: inserted at StepIndex {myPosition}. Active steps: {activeCount + 1}.");
    }

    /// <summary>
    /// Append a new empty step at end of active sequence.
    /// Not allowed on inactive steps (PhaseType == 0).
    /// </summary>
    [ExportMethod]
    public void AddStepAfter(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "AddStepAfter: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "AddStepAfter: cannot resolve target node."); return; }

        float myPT = GetPhaseType(myStep);
        if (myPT == 0f)
        {
            Log.Warning(LogCategory, "AddStepAfter: not allowed on inactive step.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);

        // Cannot add if all slots are occupied
        if (activeCount >= MaxSteps)
        {
            Log.Warning(LogCategory, "AddStepAfter: max steps reached.");
            return;
        }

        // Activate the first inactive slot (right after last active, by StepIndex order)
        int newSlot = activeCount; // 0-based index of first inactive slot
        ClearStepContent(allSteps[newSlot]);
        SetPhaseType(allSteps[newSlot], 1f);

        // Normalize: ensure ordering invariant holds
        EnsureInactiveAtEnd(allSteps);

        success = true;
        Log.Info(LogCategory, $"AddStepAfter: activated step at StepIndex {activeCount + 1}. Active steps: {activeCount + 1}.");
    }

    /// <summary>
    /// Move this step one position up (lower StepIndex) by swapping content with the step above.
    /// Not allowed on StepIndex == 1 (first step) or inactive steps (PhaseType == 0).
    /// </summary>
    [ExportMethod]
    public void MoveStepUp(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "MoveStepUp: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "MoveStepUp: cannot resolve target node."); return; }

        // Use StepIndex for position
        int myPosition = GetStepIndex(myStep);
        float myPT = GetPhaseType(myStep);

        // Block on first position or inactive step
        if (myPosition <= 1 || myPT == 0f)
        {
            Log.Warning(LogCategory, "MoveStepUp: already at top or inactive.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int myIndex = myPosition - 1;     // 0-based
        int aboveIndex = myIndex - 1;

        // Block if the step above is inactive (shouldn't happen under normal invariant)
        if (GetPhaseType(allSteps[aboveIndex]) == 0f)
        {
            Log.Warning(LogCategory, "MoveStepUp: step above is inactive, cannot swap.");
            return;
        }

        // Swap all content (including PhaseType) between this slot and slot above
        SwapStepContent(allSteps[myIndex], allSteps[aboveIndex]);

        success = true;
        Log.Info(LogCategory, $"MoveStepUp: swapped StepIndex {myPosition} with {myPosition - 1}.");
    }

    /// <summary>
    /// Move this step one position down (higher StepIndex) by swapping content with the step below.
    /// Not allowed on last active step or inactive steps (PhaseType == 0).
    /// </summary>
    [ExportMethod]
    public void MoveStepDown(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "MoveStepDown: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "MoveStepDown: cannot resolve target node."); return; }

        // Use StepIndex for position
        int myPosition = GetStepIndex(myStep);
        float myPT = GetPhaseType(myStep);

        // Block on inactive step
        if (myPT == 0f)
        {
            Log.Warning(LogCategory, "MoveStepDown: not allowed on inactive step.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);

        // Block if already at the last active position
        if (myPosition >= activeCount)
        {
            Log.Warning(LogCategory, "MoveStepDown: already at bottom active position.");
            return;
        }

        int myIndex = myPosition - 1;     // 0-based
        int belowIndex = myIndex + 1;

        // Block if the step below is inactive (shouldn't happen under normal invariant)
        if (GetPhaseType(allSteps[belowIndex]) == 0f)
        {
            Log.Warning(LogCategory, "MoveStepDown: step below is inactive, cannot swap.");
            return;
        }

        // Swap all content (including PhaseType) between this slot and slot below
        SwapStepContent(allSteps[myIndex], allSteps[belowIndex]);

        success = true;
        Log.Info(LogCategory, $"MoveStepDown: swapped StepIndex {myPosition} with {myPosition + 1}.");
    }

    /// <summary>
    /// Delete this step: clear its content, set PhaseType to 0, then normalize order
    /// so inactive content moves to the end.
    /// Not allowed on inactive steps (PhaseType == 0).
    /// </summary>
    [ExportMethod]
    public void DeleteStep(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "DeleteStep: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "DeleteStep: cannot resolve target node."); return; }

        float myPT = GetPhaseType(myStep);
        if (myPT == 0f)
        {
            Log.Warning(LogCategory, "DeleteStep: step is already inactive.");
            return;
        }

        // Use StepIndex for position
        int myPosition = GetStepIndex(myStep);
        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);

        // Block deletion if this is the last remaining active step
        if (activeCount <= 1)
        {
            Log.Warning(LogCategory, "DeleteStep: cannot delete the last active step.");
            return;
        }

        // Clear this step's content and mark it inactive (PT = 0)
        ClearStepContent(allSteps[myPosition - 1]);
        SetPhaseType(allSteps[myPosition - 1], 0f);

        // Normalize: push the now-inactive step content to the end
        EnsureInactiveAtEnd(allSteps);

        success = true;
        Log.Info(LogCategory, $"DeleteStep: removed StepIndex {myPosition}. Active steps: {activeCount - 1}.");
    }

    #endregion

    #region Data Movement Helpers

    /// <summary>
    /// Swap all content between two step nodes: StepName, StepEnabled, PhaseType, and parameters.
    /// StepIndex is NOT swapped — it is the fixed slot identifier.
    /// </summary>
    private void SwapStepContent(IUANode a, IUANode b)
    {
        // Swap PhaseType (belongs to content, not to slot)
        SwapVariable(a, b, "PhaseType");
        // Swap StepName
        SwapVariable(a, b, "StepName");
        // Swap StepEnabled
        SwapVariable(a, b, "StepEnabled");
        // Swap parameter sub-objects
        foreach (var paramName in ParameterObjects)
        {
            var objA = a.GetObject(paramName);
            var objB = b.GetObject(paramName);
            if (objA != null && objB != null)
            {
                SwapVariable(objA, objB, "ParameterValue");
                SwapVariable(objA, objB, "ParameterEnabled");
            }
        }
    }

    /// <summary>
    /// Copy all content from src to dst: StepName, StepEnabled, PhaseType, and parameters.
    /// StepIndex is NOT copied — it is the fixed slot identifier.
    /// </summary>
    private void CopyStepContent(IUANode src, IUANode dst)
    {
        // Copy PhaseType (belongs to content, not to slot)
        CopyVariable(src, dst, "PhaseType");
        CopyVariable(src, dst, "StepName");
        CopyVariable(src, dst, "StepEnabled");
        foreach (var paramName in ParameterObjects)
        {
            var objSrc = src.GetObject(paramName);
            var objDst = dst.GetObject(paramName);
            if (objSrc != null && objDst != null)
            {
                CopyVariable(objSrc, objDst, "ParameterValue");
                CopyVariable(objSrc, objDst, "ParameterEnabled");
            }
        }
    }

    /// <summary>
    /// Clear step content to defaults (empty name, disabled, zero parameters).
    /// Does NOT clear PhaseType — caller sets PT explicitly after this call.
    /// </summary>
    private void ClearStepContent(IUANode step)
    {
        var nameVar = step.GetVariable("StepName");
        if (nameVar != null) nameVar.Value = new LocalizedText("", "");

        var enabledVar = step.GetVariable("StepEnabled");
        if (enabledVar != null) enabledVar.Value = false;

        foreach (var paramName in ParameterObjects)
        {
            var obj = step.GetObject(paramName);
            if (obj == null) continue;
            var pv = obj.GetVariable("ParameterValue");
            if (pv != null) pv.Value = 0f;
            var pe = obj.GetVariable("ParameterEnabled");
            if (pe != null) pe.Value = false;
        }
    }

    /// <summary>
    /// Enforce the ordering invariant: any step with PhaseType == 0 must have its content
    /// placed at the end (higher StepIndex positions). Uses bubble-sort style passes
    /// to move inactive content toward the end while preserving relative order of active steps.
    /// </summary>
    private void EnsureInactiveAtEnd(List<IUANode> allSteps)
    {
        // Bubble inactive (PT==0) content toward the end of the list
        bool swapped;
        do
        {
            swapped = false;
            for (int i = 0; i < allSteps.Count - 1; i++)
            {
                // If current slot is inactive and next slot is active, swap content
                if (GetPhaseType(allSteps[i]) == 0f && GetPhaseType(allSteps[i + 1]) != 0f)
                {
                    SwapStepContent(allSteps[i], allSteps[i + 1]);
                    swapped = true;
                }
            }
        } while (swapped);
    }

    /// <summary>
    /// Swap a single variable's value between two parent nodes.
    /// </summary>
    private void SwapVariable(IUANode parentA, IUANode parentB, string varName)
    {
        var vA = parentA.GetVariable(varName);
        var vB = parentB.GetVariable(varName);
        if (vA == null || vB == null) return;
        var temp = vA.Value;
        vA.Value = vB.Value;
        vB.Value = temp;
    }

    /// <summary>
    /// Copy a single variable's value from src parent to dst parent.
    /// </summary>
    private void CopyVariable(IUANode srcParent, IUANode dstParent, string varName)
    {
        var vSrc = srcParent.GetVariable(varName);
        var vDst = dstParent.GetVariable(varName);
        if (vSrc == null || vDst == null) return;
        vDst.Value = vSrc.Value;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Resolve this row's step node from "RowItem" variable on LogicObject.
    /// </summary>
    private IUANode ResolveMyStep()
    {
        var v = LogicObject.GetVariable("RowItem");
        if (v == null) return null;
        if (v.Value?.Value is NodeId id && id != NodeId.Empty)
            return InformationModel.Get(id);
        return null;
    }

    /// <summary>
    /// Get all step nodes (RecipeStepRSA1..MaxSteps) from the target node, ordered by StepIndex.
    /// Returns list indexed 0-based: index 0 = step with StepIndex 1.
    /// </summary>
    private List<IUANode> GetAllStepNodes(IUANode target)
    {
        var steps = new List<IUANode>();
        for (int i = 1; i <= MaxSteps; i++)
        {
            var step = target.GetObject($"RecipeStepRSA{i:D2}");
            if (step != null) steps.Add(step);
        }
        // Sort by StepIndex to guarantee correct ordering
        steps.Sort((a, b) => GetStepIndex(a).CompareTo(GetStepIndex(b)));
        return steps;
    }

    /// <summary>
    /// Read StepIndex from a step node. Returns the fixed 1-based position of this slot.
    /// </summary>
    private int GetStepIndex(IUANode stepNode)
    {
        var v = stepNode.GetVariable("StepIndex");
        return v != null ? Convert.ToInt32(v.Value.Value) : 0;
    }

    /// <summary>
    /// Read PhaseType from a step node. Returns 0 if not found.
    /// Only used for active/inactive determination (0 = inactive, non-zero = active).
    /// </summary>
    private float GetPhaseType(IUANode stepNode)
    {
        var v = stepNode.GetVariable("PhaseType");
        return v != null ? Convert.ToSingle(v.Value.Value) : 0f;
    }

    /// <summary>
    /// Write PhaseType value on step node.
    /// </summary>
    private void SetPhaseType(IUANode stepNode, float value)
    {
        var ptVar = stepNode.GetVariable("PhaseType");
        if (ptVar != null) ptVar.Value = value;
    }

    /// <summary>
    /// Count active steps (PhaseType != 0).
    /// </summary>
    private int CountActive(List<IUANode> steps)
    {
        return steps.Count(s => GetPhaseType(s) != 0f);
    }

    #endregion
}
