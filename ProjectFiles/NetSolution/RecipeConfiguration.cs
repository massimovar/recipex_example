using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.Alarm;

/// <summary>
/// YAML configuration model for recipe families, phase types, and parameter enablement.
/// Loaded at startup, validated before use. Drives all enablement logic.
/// </summary>
public class RecipeConfigurationModel
{
    [YamlMember(Alias = "recipeFamilies")]
    public Dictionary<int, RecipeFamilyConfig> RecipeFamilies { get; set; } = new Dictionary<int, RecipeFamilyConfig>();
}

/// <summary>
/// Configuration for a single recipe family.
/// </summary>
public class RecipeFamilyConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; }

    [YamlMember(Alias = "allowedPhaseTypes")]
    public List<int> AllowedPhaseTypes { get; set; } = new List<int>();

    [YamlMember(Alias = "phaseTypes")]
    public Dictionary<int, PhaseTypeConfig> PhaseTypes { get; set; } = new Dictionary<int, PhaseTypeConfig>();
}

/// <summary>
/// Configuration for a single phase type within a family.
/// </summary>
public class PhaseTypeConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; }

    [YamlMember(Alias = "enabledStepParameters")]
    public List<int> EnabledStepParameters { get; set; } = new List<int>();
}

/// <summary>
/// Loads, validates, and provides access to the recipe YAML configuration.
/// Thread-safe after initialization.
/// </summary>
public class RecipeConfigurationLoader
{
    private RecipeConfigurationModel _config;
    private bool _isValid;
    private List<string> _validationErrors = new List<string>();

    public bool IsValid => _isValid;
    public IReadOnlyList<string> ValidationErrors => _validationErrors.AsReadOnly();
    public RecipeConfigurationModel Config => _config;

    /// <summary>
    /// Load and validate YAML configuration from the given file path.
    /// Returns false if file missing or validation fails.
    /// </summary>
    public bool Load(string yamlFilePath)
    {
        _isValid = false;
        _validationErrors.Clear();
        _config = null;

        // Check file existence
        if (!File.Exists(yamlFilePath))
        {
            _validationErrors.Add($"Configuration file not found: {yamlFilePath}");
            return false;
        }

        // Parse YAML
        try
        {
            var yaml = File.ReadAllText(yamlFilePath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            _config = deserializer.Deserialize<RecipeConfigurationModel>(yaml);
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"YAML parse error: {ex.Message}");
            return false;
        }

        if (_config == null || _config.RecipeFamilies == null || _config.RecipeFamilies.Count == 0)
        {
            _validationErrors.Add("Configuration contains no recipe families.");
            return false;
        }

        // Validate structure
        _isValid = Validate();
        return _isValid;
    }

    /// <summary>
    /// Validate the loaded configuration against the spec rules.
    /// </summary>
    private bool Validate()
    {
        bool valid = true;

        foreach (var kvp in _config.RecipeFamilies)
        {
            int familyKey = kvp.Key;
            var family = kvp.Value;

            if (family == null)
            {
                _validationErrors.Add($"Family {familyKey}: null configuration.");
                valid = false;
                continue;
            }

            // Check allowedPhaseTypes not empty
            if (family.AllowedPhaseTypes == null || family.AllowedPhaseTypes.Count == 0)
            {
                _validationErrors.Add($"Family {familyKey}: allowedPhaseTypes is empty.");
                valid = false;
                continue;
            }

            // All PhaseType values must be > 0
            foreach (var pt in family.AllowedPhaseTypes)
            {
                if (pt <= 0)
                {
                    _validationErrors.Add($"Family {familyKey}: PhaseType {pt} must be > 0.");
                    valid = false;
                }
            }

            // No duplicate phase types in allowedPhaseTypes
            var duplicatePt = family.AllowedPhaseTypes.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicatePt.Count > 0)
            {
                _validationErrors.Add($"Family {familyKey}: duplicate PhaseType values: {string.Join(",", duplicatePt)}.");
                valid = false;
            }

            // Each allowedPhaseType must have a phaseTypes entry
            if (family.PhaseTypes == null)
            {
                _validationErrors.Add($"Family {familyKey}: phaseTypes section is missing.");
                valid = false;
                continue;
            }

            foreach (var pt in family.AllowedPhaseTypes)
            {
                if (!family.PhaseTypes.ContainsKey(pt))
                {
                    _validationErrors.Add($"Family {familyKey}: allowedPhaseType {pt} has no corresponding phaseTypes entry.");
                    valid = false;
                }
            }

            // Validate each phase type config
            foreach (var ptKvp in family.PhaseTypes)
            {
                int ptKey = ptKvp.Key;
                var ptConfig = ptKvp.Value;

                if (ptConfig == null)
                {
                    _validationErrors.Add($"Family {familyKey}, PhaseType {ptKey}: null configuration.");
                    valid = false;
                    continue;
                }

                if (ptConfig.EnabledStepParameters == null || ptConfig.EnabledStepParameters.Count == 0)
                {
                    _validationErrors.Add($"Family {familyKey}, PhaseType {ptKey}: enabledStepParameters is empty.");
                    valid = false;
                    continue;
                }

                // All parameter indexes must be 1..20
                foreach (var paramIdx in ptConfig.EnabledStepParameters)
                {
                    if (paramIdx < 1 || paramIdx > 20)
                    {
                        _validationErrors.Add($"Family {familyKey}, PhaseType {ptKey}: parameter index {paramIdx} out of range 1..20.");
                        valid = false;
                    }
                }

                // No duplicate parameter indexes
                var duplicateParams = ptConfig.EnabledStepParameters.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (duplicateParams.Count > 0)
                {
                    _validationErrors.Add($"Family {familyKey}, PhaseType {ptKey}: duplicate parameter indexes: {string.Join(",", duplicateParams)}.");
                    valid = false;
                }
            }
        }

        return valid;
    }

    /// <summary>
    /// Get the family config for a given recipe family key.
    /// Returns null if family not configured.
    /// </summary>
    public RecipeFamilyConfig GetFamily(int familyKey)
    {
        if (_config?.RecipeFamilies == null)
            return null;

        _config.RecipeFamilies.TryGetValue(familyKey, out var family);
        return family;
    }

    /// <summary>
    /// Check if a phase type is allowed for a given family.
    /// </summary>
    public bool IsPhaseTypeAllowed(int familyKey, int phaseType)
    {
        var family = GetFamily(familyKey);
        return family?.AllowedPhaseTypes?.Contains(phaseType) ?? false;
    }

    /// <summary>
    /// Get enabled step parameter indexes for a given family + phase type.
    /// Returns empty list if not configured.
    /// </summary>
    public List<int> GetEnabledParameters(int familyKey, int phaseType)
    {
        var family = GetFamily(familyKey);
        if (family?.PhaseTypes == null)
            return new List<int>();

        if (family.PhaseTypes.TryGetValue(phaseType, out var ptConfig))
            return ptConfig.EnabledStepParameters ?? new List<int>();

        return new List<int>();
    }
}
