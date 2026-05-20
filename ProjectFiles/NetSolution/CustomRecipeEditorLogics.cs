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
using OpcUa = UAManagedCore.OpcUa;
#endregion

/// <summary>
/// Per-step NetLogic for recipe EditModel step manipulation.
/// Invariant: active steps (PT=1..N) occupy physical slots 1..N in order.
/// Inactive steps (PT=0) occupy slots N+1..MaxSteps.
/// All operations move actual step DATA between physical slots to maintain this invariant.
/// </summary>
public class CustomRecipeEditorLogics : BaseNetLogic
{
    private const int MaxSteps = 20;
    private const string LogCategory = "RecipeEditor";

    // Sub-object names containing step parameters
    private static readonly string[] ParameterObjects = { "dsp", "tsp", "psp" };

    public override void Start() { }
    public override void Stop() { }

    #region ExportMethods

    /// <summary>
    /// Insert a new empty step before this step's position.
    /// Shifts data from position P..N right by one slot, clears slot P.
    /// Not allowed on first step (PT=1) or inactive steps (PT=0).
    /// </summary>
    [ExportMethod]
    public void AddStepBefore(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "AddStepBefore: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "AddStepBefore: cannot resolve target node."); return; }

        float myPT = GetPhaseType(myStep);
        if (myPT <= 1f)
        {
            Log.Warning(LogCategory, "AddStepBefore: not allowed on first or inactive step.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);
        if (activeCount >= MaxSteps)
        {
            Log.Warning(LogCategory, "AddStepBefore: max steps reached.");
            return;
        }

        int insertPos = (int)myPT; // 1-based position to insert at

        // Shift data RIGHT: from slot N down to insertPos, copy slot[i-1] -> slot[i]
        // This opens a gap at insertPos
        for (int i = activeCount; i >= insertPos; i--)
        {
            CopyStepContent(allSteps[i - 1], allSteps[i]);
        }

        // Clear the inserted slot and activate it
        ClearStepContent(allSteps[insertPos - 1]);
        SetPhaseType(allSteps[insertPos - 1], (float)insertPos);

        // Reassign PTs for all active slots (1..N+1)
        for (int i = 0; i <= activeCount; i++)
        {
            SetPhaseType(allSteps[i], (float)(i + 1));
        }
        // Ensure remaining slots are inactive
        for (int i = activeCount + 1; i < allSteps.Count; i++)
        {
            SetPhaseType(allSteps[i], 0f);
        }

        success = true;
        Log.Info(LogCategory, $"AddStepBefore: inserted at position {insertPos}. Active steps: {activeCount + 1}.");
    }

    /// <summary>
    /// Append a new empty step at end of active sequence.
    /// Not allowed on inactive steps (PT=0).
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

        if (activeCount >= MaxSteps)
        {
            Log.Warning(LogCategory, "AddStepAfter: max steps reached.");
            return;
        }

        // Next slot after last active — clear and activate
        int newSlot = activeCount; // 0-based index of slot N+1
        ClearStepContent(allSteps[newSlot]);
        SetPhaseType(allSteps[newSlot], (float)(activeCount + 1));

        success = true;
        Log.Info(LogCategory, $"AddStepAfter: activated step at position {activeCount + 1}. Active steps: {activeCount + 1}.");
    }

    /// <summary>
    /// Move this step one position up by swapping all data with the step above.
    /// Not allowed on first step (PT=1) or inactive steps (PT=0).
    /// </summary>
    [ExportMethod]
    public void MoveStepUp(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "MoveStepUp: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "MoveStepUp: cannot resolve target node."); return; }

        float myPT = GetPhaseType(myStep);
        if (myPT <= 1f)
        {
            Log.Warning(LogCategory, "MoveStepUp: already at top or inactive.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int myIndex = (int)myPT - 1;     // 0-based
        int aboveIndex = myIndex - 1;

        // Swap content (not PT) between this slot and slot above
        SwapStepContent(allSteps[myIndex], allSteps[aboveIndex]);

        success = true;
        Log.Info(LogCategory, $"MoveStepUp: swapped position {(int)myPT} with {(int)myPT - 1}.");
    }

    /// <summary>
    /// Move this step one position down by swapping all data with the step below.
    /// Not allowed on last active step or inactive steps (PT=0).
    /// </summary>
    [ExportMethod]
    public void MoveStepDown(out bool success)
    {
        success = false;

        var myStep = ResolveMyStep();
        if (myStep == null) { Log.Error(LogCategory, "MoveStepDown: cannot resolve step node."); return; }

        var target = myStep.Owner;
        if (target == null) { Log.Error(LogCategory, "MoveStepDown: cannot resolve target node."); return; }

        float myPT = GetPhaseType(myStep);
        if (myPT == 0f)
        {
            Log.Warning(LogCategory, "MoveStepDown: not allowed on inactive step.");
            return;
        }

        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);

        if (myPT >= activeCount)
        {
            Log.Warning(LogCategory, "MoveStepDown: already at bottom active position.");
            return;
        }

        int myIndex = (int)myPT - 1;     // 0-based
        int belowIndex = myIndex + 1;

        // Swap content (not PT) between this slot and slot below
        SwapStepContent(allSteps[myIndex], allSteps[belowIndex]);

        success = true;
        Log.Info(LogCategory, $"MoveStepDown: swapped position {(int)myPT} with {(int)myPT + 1}.");
    }

    /// <summary>
    /// Delete this step: shift data from slots after this one LEFT, deactivate last active slot.
    /// Not allowed on inactive steps (PT=0).
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

        var allSteps = GetAllStepNodes(target);
        int activeCount = CountActive(allSteps);
        int deletePos = (int)myPT; // 1-based

        // Shift data LEFT: from slot deletePos+1 to N, copy slot[i] -> slot[i-1]
        for (int i = deletePos; i < activeCount; i++)
        {
            CopyStepContent(allSteps[i], allSteps[i - 1]);
        }

        // Clear and deactivate last active slot
        ClearStepContent(allSteps[activeCount - 1]);
        SetPhaseType(allSteps[activeCount - 1], 0f);

        // Reassign PTs for remaining active slots
        for (int i = 0; i < activeCount - 1; i++)
        {
            SetPhaseType(allSteps[i], (float)(i + 1));
        }

        success = true;
        Log.Info(LogCategory, $"DeleteStep: removed position {deletePos}. Active steps: {activeCount - 1}.");
    }

    #endregion

    #region Data Movement Helpers

    /// <summary>
    /// Swap all content (StepName, StepEnabled, parameters) between two step nodes.
    /// PhaseType is NOT swapped — it always matches physical slot index.
    /// </summary>
    private void SwapStepContent(IUANode a, IUANode b)
    {
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
    /// Copy all content (StepName, StepEnabled, parameters) from src to dst.
    /// PhaseType is NOT copied.
    /// </summary>
    private void CopyStepContent(IUANode src, IUANode dst)
    {
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
    /// PhaseType is NOT cleared here.
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
    /// Get all step nodes (RecipeStepRSA1..MaxSteps) from the target node.
    /// Returns list indexed 0-based: index 0 = physical slot 1 (RecipeStepRSA1).
    /// </summary>
    private List<IUANode> GetAllStepNodes(IUANode target)
    {
        var steps = new List<IUANode>();
        for (int i = 1; i <= MaxSteps; i++)
        {
            var step = target.GetObject($"RecipeStepRSA{i}");
            if (step != null) steps.Add(step);
        }
        return steps;
    }

    /// <summary>
    /// Read PhaseType from a step node. Returns 0 if not found.
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
    /// Count active steps (PhaseType > 0).
    /// </summary>
    private int CountActive(List<IUANode> steps)
    {
        return steps.Count(s => GetPhaseType(s) > 0f);
    }

    #endregion
}
