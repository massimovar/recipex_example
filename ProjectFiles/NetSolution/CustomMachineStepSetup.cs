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
/// EditModel setup logic: applies PhaseType-based rules to a single step at startup,
/// and reacts at runtime when that step's PhaseType changes.
/// 
/// Rules are defined in two dictionaries:
///   - PhaseTypeParameterRules: controls ParameterEnabled and EURange per PhaseType/parameter.
///   - PhaseTypeStepNameMap: maps PhaseType -> default StepName.
/// 
/// Requires on LogicObject:
///   - "RowItem" (NodeId) → points to the specific RecipeStepRSA instance to manage.
///   - "RSAMachine" (NodeId) → points to RSAMachine type (for parameter discovery).
/// </summary>
public class CustomMachineStepSetup : BaseNetLogic
{
    private const string LogCategory = "RecipeEditModelSetup";

    private string[] ParameterObjects;
    private RecipeStepRSA _step;
    private (IUAVariable variable, EventHandler<VariableChangeEventArgs> handler)? _subscription;

    // Cross-row link: f0 lives on the RecipeGeneralParameters row (RecipeMainParameters
    // data node, sibling under the shared RSAMachine). We subscribe to it and drive dsp.
    private (IUAVariable variable, EventHandler<VariableChangeEventArgs> handler)? _f0Subscription;

    #region ═══════════════════════════════════════════════════════════════
    //  TEMPLATE RULES — EDIT HERE TO CHANGE BEHAVIOR
    #endregion

    /// <summary>
    /// PhaseType -> StepName mapping. When PhaseType changes, StepName auto-updates.
    /// Add/remove entries as needed.
    /// </summary>
    private static readonly Dictionary<int, string> PhaseTypeStepNameMap = new Dictionary<int, string>
    {
        { 0, "" },          // Inactive step — no name
        { 1, "SAA" },       // PhaseType 1 → StepName "SAA"
        { 2, "SBB" },       // PhaseType 2 → StepName "SBB"
        { 3, "SCC" },       // PhaseType 3 → StepName "SCC"
        { 4, "SDD" },       // PhaseType 4 → StepName "SDD"
        // Add more mappings here...
    };

    /// <summary>
    /// PhaseType -> per-parameter rules.
    /// Key: PhaseType value.
    /// Value: dictionary of parameter BrowseName -> rule (Enabled, EURange override).
    /// If a parameter is not listed for a PhaseType, it keeps defaults (enabled=true, no EURange change).
    /// If EURangeMin/Max are null, EURange is not modified (keeps model default).
    /// </summary>
    private static readonly Dictionary<int, Dictionary<string, ParameterRule>> PhaseTypeParameterRules =
        new Dictionary<int, Dictionary<string, ParameterRule>>
    {
        // PhaseType == 0: step ignored, no rules applied
        {
            0, new Dictionary<string, ParameterRule>()
        },

        // PhaseType == 1: dsp disabled, tsp enabled with custom EURange, psp enabled with custom EURange
        {
            1, new Dictionary<string, ParameterRule>
            {
                { "dsp", new ParameterRule(enabled: false) },
                { "tsp", new ParameterRule(enabled: true, euRangeMin: 10f, euRangeMax: 50f) },
                { "psp", new ParameterRule(enabled: true, euRangeMin: 20f, euRangeMax: 30f) },
            }
        },

        // PhaseType == 2: all disabled except tsp (default EURange)
        {
            2, new Dictionary<string, ParameterRule>
            {
                { "dsp", new ParameterRule(enabled: false) },
                { "tsp", new ParameterRule(enabled: true) },  // No EURange override → keeps default
                { "psp", new ParameterRule(enabled: false) },
            }
        },

        // PhaseType == 3: example — all enabled, custom ranges
        {
            3, new Dictionary<string, ParameterRule>
            {
                { "dsp", new ParameterRule(enabled: true, euRangeMin: 0f, euRangeMax: 100f) },
                { "tsp", new ParameterRule(enabled: true, euRangeMin: 5f, euRangeMax: 80f) },
                { "psp", new ParameterRule(enabled: true, euRangeMin: 0f, euRangeMax: 60f) },
            }
        },

        // Add more PhaseType rules here...
    };

    #region ═══════════════════════════════════════════════════════════════
    //  LIFECYCLE
    #endregion

    public override void Start()
    {
        ParameterObjects = Array.Empty<string>();

        // Resolve RSAMachine type for parameter discovery
        var rsaMachineVar = LogicObject.GetVariable("RSAMachine");
        if (rsaMachineVar == null)
        {
            Log.Error(LogCategory, "Start: RSAMachine variable not found on LogicObject.");
            return;
        }
        NodeId rsaTypeId = (NodeId)rsaMachineVar.Value;
        if (rsaTypeId == null || rsaTypeId == NodeId.Empty)
        {
            Log.Error(LogCategory, "Start: RSAMachine NodeId is empty.");
            return;
        }
        var rsaType = InformationModel.Get(rsaTypeId);
        if (rsaType == null)
        {
            Log.Error(LogCategory, "Start: cannot resolve RSAMachine type.");
            return;
        }

        // Discover parameter names from first step child of the type
        var firstTypeStep = rsaType.Children.OfType<RecipeStepRSA>().FirstOrDefault();
        if (firstTypeStep != null)
        {
            ParameterObjects = firstTypeStep.Children
                .OfType<RecipeStepParameter>()
                .Select(o => o.BrowseName)
                .ToArray();
        }

        // Resolve the target step from RowItem NodeId variable
        var rowItemVar = LogicObject.GetVariable("RowItem");
        if (rowItemVar == null)
        {
            Log.Error(LogCategory, "Start: RowItem variable not found on LogicObject.");
            return;
        }
        NodeId stepId = (NodeId)rowItemVar.Value;
        if (stepId == null || stepId == NodeId.Empty)
        {
            Log.Error(LogCategory, "Start: RowItem NodeId is empty.");
            return;
        }
        _step = InformationModel.Get<RecipeStepRSA>(stepId);
        if (_step == null)
        {
            Log.Error(LogCategory, "Start: cannot resolve RowItem as RecipeStepRSA.");
            return;
        }

        // Apply rules to this step at startup
        int pt = (int)_step.PhaseType;
        Log.Info(LogCategory, $"Start: {_step.BrowseName} PhaseType={pt}. Applying rules.");
        
        // Update dictionary with specific dynamic rules. 
        ApplyRulesToStep(_step, pt);

        // Subscribe to PhaseType changes on this step
        var ptVar = _step.PhaseTypeVariable;
        if (ptVar != null)
        {
            EventHandler<VariableChangeEventArgs> handler = (sender, e) =>
            {
                int newPT = Convert.ToInt32(e.NewValue.Value);
                Log.Info(LogCategory, $"PhaseType changed on {_step.BrowseName} -> {newPT}. Applying rules.");
                ApplyRulesToStep(_step, newPT);
            };
            ptVar.VariableChange += handler;
            _subscription = (ptVar, handler);
        }

        // Cross-row link: subscribe to f0 on the shared RecipeMainParameters data node.
        // Both rows (RecipeGeneralParameters and this RecipeStep) bind the same model,
        // so reading f0 from the model is the supported path (no dynamic-link needed).
        SetupF0Link();

        Log.Info(LogCategory, $"Start: setup complete. Step={_step.BrowseName}, Params=[{string.Join(", ", ParameterObjects)}]");
    }

    public override void Stop()
    {
        // Mandatory: unsubscribe PhaseType handler
        if (_subscription.HasValue)
        {
            _subscription.Value.variable.VariableChange -= _subscription.Value.handler;
            _subscription = null;
        }

        // Mandatory: unsubscribe f0 cross-row handler (prevents leaked subscriptions on row recycle)
        if (_f0Subscription.HasValue)
        {
            _f0Subscription.Value.variable.VariableChange -= _f0Subscription.Value.handler;
            _f0Subscription = null;
        }
    }

    #region ═══════════════════════════════════════════════════════════════
    //  CROSS-ROW LINK: f0 (RecipeGeneralParameters) -> dsp (RecipeStep)
    #endregion

    /// <summary>
    /// Wire f0 -> dsp. f0 is a variable on the RecipeMainParameters node (shown by the
    /// RecipeGeneralParameters row). This RecipeStep row shares the same parent (RSAMachine),
    /// so we reach f0 via the model, not via the other row's widget.
    /// On every f0 change, dsp.ParameterValue = f0 * 2 (proof of the link).
    /// </summary>
    private void SetupF0Link()
    {
        // _step.Owner is the shared RSAMachine instance both rows hang off of
        var owner = _step.Owner;
        if (owner == null)
        {
            Log.Error(LogCategory, "SetupF0Link: step Owner (RSAMachine) is null.");
            return;
        }

        // Locate the RecipeMainParameters sibling that carries f0
        var mainParams = owner.Children.OfType<RecipeMainParameters>().FirstOrDefault();
        if (mainParams == null)
        {
            Log.Error(LogCategory, "SetupF0Link: RecipeMainParameters node not found under owner.");
            return;
        }

        var f0Var = mainParams.f0Variable;
        if (f0Var == null)
        {
            Log.Error(LogCategory, "SetupF0Link: f0 variable not found on RecipeMainParameters.");
            return;
        }

        // Apply once with current f0 so dsp is correct at startup
        UpdateDspFromF0(Convert.ToSingle(f0Var.Value.Value));

        // React to runtime f0 edits
        EventHandler<VariableChangeEventArgs> handler = (sender, e) =>
        {
            float f0 = Convert.ToSingle(e.NewValue.Value);
            UpdateDspFromF0(f0);
        };
        f0Var.VariableChange += handler;
        _f0Subscription = (f0Var, handler);
    }

    /// <summary>
    /// Set this step's dsp ParameterValue = f0 * 2.
    /// </summary>
    private void UpdateDspFromF0(float f0)
    {
        var dsp = _step.dsp;
        if (dsp == null)
        {
            Log.Warning(LogCategory, "UpdateDspFromF0: dsp parameter not found on step.");
            return;
        }

        float newValue = f0 * 2f;
        dsp.ParameterValue = newValue;
        Log.Info(LogCategory, $"f0={f0} -> {_step.BrowseName}.dsp={newValue}");
    }

    #region ═══════════════════════════════════════════════════════════════
    //  RULE APPLICATION
    #endregion

    /// <summary>
    /// Apply ParameterEnabled, EURange, and StepName rules to step based on PhaseType.
    /// </summary>
    private void ApplyRulesToStep(RecipeStepRSA step, int phaseType)
    {
        // Skip inactive steps (PhaseType == 0)
        if (phaseType == 0)
            return;

        // Apply StepName rule
        if (PhaseTypeStepNameMap.TryGetValue(phaseType, out var name))
        {
            step.StepNameVariable.Value = name;
        }

        // Apply parameter rules if defined for this PhaseType
        if (PhaseTypeParameterRules.TryGetValue(phaseType, out var paramRules))
        {
            foreach (var paramName in ParameterObjects)
            {
                var paramObj = step.GetObject(paramName);
                if (paramObj == null) continue;

                if (paramRules.TryGetValue(paramName, out var rule))
                {
                    ApplyParameterRule(paramObj, rule);
                }
            }
        }
    }

    /// <summary>
    /// Apply a single ParameterRule: set ParameterEnabled and optionally override EURange.
    /// </summary>
    private void ApplyParameterRule(IUANode paramObj, ParameterRule rule)
    {
        var enabledVar = paramObj.GetVariable("ParameterEnabled");
        if (enabledVar != null)
            enabledVar.Value = rule.Enabled;

        if (rule.EURangeMin.HasValue && rule.EURangeMax.HasValue)
        {
            var paramValueVar = paramObj.GetVariable("ParameterValue");
            if (paramValueVar != null)
                SetEURange(paramValueVar, rule.EURangeMin.Value, rule.EURangeMax.Value);
        }
    }

    /// <summary>
    /// Set EURange Low/High on a variable.
    /// </summary>
    private void SetEURange(IUAVariable variable, float min, float max)
    {
        var euRange = variable.GetVariable("EURange");
        if (euRange == null)
        {
            Log.Warning(LogCategory, $"SetEURange: EURange not found on {variable.BrowseName}.");
            return;
        }

        var lowVar = euRange.GetVariable("Low");
        var highVar = euRange.GetVariable("High");

        if (lowVar != null && highVar != null)
        {
            lowVar.Value = (double)min;
            highVar.Value = (double)max;
        }
        else
        {
            Log.Warning(LogCategory, $"SetEURange: Low/High not found on EURange of {variable.BrowseName}.");
        }
    }

    #region ═══════════════════════════════════════════════════════════════
    //  RULE DEFINITION TYPE
    #endregion

    /// <summary>
    /// Defines a parameter rule: whether enabled, and optional EURange override.
    /// If EURangeMin/Max are null, EURange is not modified.
    /// </summary>
    private struct ParameterRule
    {
        public bool Enabled;
        public float? EURangeMin;
        public float? EURangeMax;

        public ParameterRule(bool enabled, float? euRangeMin = null, float? euRangeMax = null)
        {
            Enabled = enabled;
            EURangeMin = euRangeMin;
            EURangeMax = euRangeMax;
        }
    }
}
