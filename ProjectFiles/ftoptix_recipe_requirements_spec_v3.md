# FT Optix RecipeX NetLogic Requirements and Functional Specification

## 1. Purpose

Develop a clear and maintainable FT Optix NetLogic solution for managing RecipeX recipes in a pharmaceutical machine context.

The solution must provide:

- Runtime-safe recipe lifecycle management.
- Controlled recipe versioning.
- Validation of recipe step ordering before recipe usage.
- Recipe duplication and archival instead of destructive deletion.
- A separate ListView edit-model logic to support future UI development.
- Runtime-only test utilities to generate and bulk-delete test recipes.
- Design-time utility logic to generate a YAML configuration template.

The specification is written to be clear for both human developers and AI coding agents.

---

## 2. Scope

### 2.1 In scope

Implement the following NetLogic components:

1. `RecipeSchemaNetLogic`
   - Added as a child of the RecipeSchema node.
   - Responsible for recipe lifecycle operations and business rules.

2. `RecipeListViewEditModelNetLogic`
   - Added as a child of the ListView used to create/edit recipe steps.
   - Responsible for UI edit-model operations such as adding, removing, reordering, and normalizing recipe steps.

3. `RecipeRuntimeTestToolsNetLogic`
   - Runtime-only NetLogic used for testing purposes.
   - Responsible for generating a variable number of test recipes.
   - Responsible for bulk-deleting/archiving recipes generated for tests.
   - Must not be part of the validated production recipe workflow.

4. `RecipeYamlTemplateGeneratorNetLogic`
   - Design-time NetLogic or design-time utility.
   - Responsible for generating a template YAML configuration file in the FT Optix `ProjectFiles` directory.

### 2.2 Out of scope for the first implementation

The following items are not part of the first implementation:

- Final UI screens and layouts.
- Electronic signature workflow.
- Approval comment requirement.
- Audit-trail integration.
- Dedicated logging/audit requirement.
- PLC communication implementation.
- Database schema changes outside RecipeX capabilities.

---

## 3. Domain assumptions

The system is used in a pharmaceutical environment. Therefore, the implementation must prioritize:

- Controlled recipe lifecycle.
- Non-destructive delete behavior.
- Explicit validation before recipe release or PLC usage.
- Clear method results for UI feedback.
- Prevention of accidental edits on non-editable recipes.

A recipe marked as `released` is considered locked and must never be modified in place.

Only recipes with `Status = draft` are directly editable.

Runtime test utilities are for development/testing only and must be clearly separated from production recipe operations.

---

## 4. Recipe schema overview

There is one recipe schema only.

The recipe schema represents:

- Generic machine parameters.
- A fixed number of recipe steps.
- A fixed number of parameters for each step.

Production limits:

- Number of recipe steps: `20`.
- Number of parameters per step: `20`.

Reference model node to inspect:

```text
Model/RSA/RSAMachine1
```

---

## 5. Recipe schema structure

### 5.1 Recipe metadata

Each recipe has the following properties.

| Property | Type | Storage | Description |
|---|---|---|---|
| `Version` | String | Native `RecipeId.Version` field (maps to `Recipes.Version` DB column) | Recipe version in `major.minor` format, for example `1.0`, `1.1`, `2.0`. Not a custom metadata field. |
| `Status` | `RecipeStatuses` enum | RecipeX custom metadata | Current recipe lifecycle status. |

### 5.2 RecipeStatuses enum

The `RecipeStatuses` enumeration is defined in the FTOptix Model at:

```text
Model/RecipeStatuses
```

It is the single source of truth. The C# code maintains a mirror enum for type safety, validated at startup against the model node.

Values:

| Status | Meaning | Directly editable |
|---|---|---|
| `template` | Recipe used as a starting point for new recipes. | Yes |
| `draft` | Recipe under creation or modification. | Yes |
| `prepared` | Recipe prepared and ready for review. | No. A new recipe revision must be created if changes are required. |
| `approved` | Recipe approved but not yet released. | No. A new recipe revision must be created if changes are required. |
| `released` | Recipe released for production use. | No. A new major revision must be created if changes are required. |
| `archived` | Recipe no longer active. Used as historical record. | No. |

### 5.3 Allowed lifecycle sequence

The standard forward lifecycle is:

```text
draft -> prepared -> approved -> released -> archived
```

Additional rule:

```text
template -> draft
```

Backward transitions are not allowed.

Examples of rejected backward transitions:

```text
prepared -> draft
approved -> prepared
released -> approved
archived -> released
```

---

## 6. Machine model structure

The recipe content is based on an object named:

```text
RSAMachine1
```

`RSAMachine1` is an object of type:

```text
RSAMachine
```

It contains:

1. `Parameters1`, object of type `Parameters`.
2. A fixed number of `RecipeStepRSA` step objects.

### 6.1 Parameters1

Required variable:

| Variable | Type | Description |
|---|---|---|
| `RecipeFamily` | Float | Identifies the recipe family and drives which phase types and step parameters are allowed/enabled. |

### 6.2 RecipeStepRSA

Each `RecipeStepRSA` contains:

| Member | Type | Description |
|---|---|---|
| `PhaseType` | Float | Ordering index of the step. `0` means unused tail step. |
| `StepName` | LocalizedText | Human-readable step name. |
| `StepEnabled` | Boolean | Indicates whether the step is active/visible/usable. |
| Step parameter children | `StepParameter` | Fixed set of 20 step parameters. |

### 6.3 StepParameter

Each `StepParameter` contains:

| Member | Type | Description |
|---|---|---|
| `ParameterValue` | Numeric value with `EURange` | The parameter value used by the PLC when the related parameter is enabled. |
| `ParameterEnabled` | Boolean | Indicates whether the parameter is applicable for the current step `PhaseType`. |

---

## 7. Step ordering and PLC acceptance rules

Each recipe always physically contains 20 steps.

Only steps with:

```text
PhaseType != 0
```

are considered active and meaningful for the PLC.

Steps with:

```text
PhaseType == 0
```

are unused and must be placed only at the end of the step list.

`PhaseType` is only an ordering index.

Therefore:

- Active steps must be normalized as `1, 2, 3, ... N`.
- Unused tail steps must be normalized as `0`.
- Automatic renumbering is allowed and required after add/delete operations.

Validation rule:

```text
Once a step with PhaseType == 0 is found, all following steps must also have PhaseType == 0.
```

Invalid example:

| Step | PhaseType | Meaning |
|---|---:|---|
| Step 1 | 1 | Active |
| Step 2 | 0 | Unused step placed before an active step |
| Step 3 | 2 | Active step after unused step |

This sequence must be rejected.

---

## 8. PhaseType and parameter enablement rules

### 8.1 YAML configuration

The enabled `PhaseType` values depend on `RecipeFamily`.

A YAML configuration file must be used to avoid hardcoding the matrix in NetLogic code.

The YAML configuration file must be stored in the FT Optix `ProjectFiles` directory.

### 8.2 Recommended YAML structure

```yaml
recipeFamilies:
  1:
    name: "Default family"
    allowedPhaseTypes: [1, 2, 3, 4, 5]
    phaseTypes:
      1:
        name: "Step 1"
        enabledStepParameters: [1, 2, 3, 4]
      2:
        name: "Step 2"
        enabledStepParameters: [1, 3, 5, 6]
      3:
        name: "Step 3"
        enabledStepParameters: [2, 4, 7, 8]
```

### 8.3 Configuration validation

At startup, the NetLogic must validate the configuration file.

Validation must check:

- Every `RecipeFamily` key is valid and can be parsed.
- Every `PhaseType` is valid and greater than `0`.
- Every enabled step parameter index is within the supported range: `1..20`.
- Each `allowedPhaseTypes` entry has a corresponding `phaseTypes` configuration.
- No duplicate phase type values exist in the same family.
- No duplicate parameter indexes exist for a phase type.

If the configuration is invalid, the NetLogic must fail safely:

- Do not allow recipe creation or update.
- Return a clear error result.
- Expose a diagnostic status to the UI if possible.

### 8.4 Runtime enablement behavior

For each step:

- If `PhaseType == 0`:
  - `StepEnabled = false`.
  - All child `StepParameter.ParameterEnabled = false`.

- If `PhaseType != 0`:
  - The `PhaseType` must be allowed for the current `RecipeFamily`.
  - `StepEnabled = true`.
  - Only the configured step parameters for that `PhaseType` are enabled.
  - All other step parameters are disabled.

---

## 9. Versioning rules

Only recipes with:

```text
Status = draft
```

are directly editable.

Recipes with any other status must not be edited in place. If changes are required, a new recipe revision must be created.

When a new revision is created:

| Source recipe status | New recipe version | Rule |
|---|---|---|
| Not `released` | `major.minor + 1` | Minor version is incremented. |
| `released` | `major + 1.0` | Major version is incremented and minor is reset to `0`. |

Examples:

| Current version | Current status | New version |
|---|---|---|
| `1.0` | `prepared` | `1.1` |
| `1.1` | `approved` | `1.2` |
| `1.2` | `released` | `2.0` |

---

## 10. Required NetLogic: RecipeSchemaNetLogic

`RecipeSchemaNetLogic` must be created as a child of the RecipeSchema node.

It owns recipe lifecycle and business operations.

### 11.1 Required NetLogic variables

The NetLogic node must have the following variables configured in the FTOptix IDE:

| Variable | DataType | Points to | Purpose |
|---|---|---|---|
| `RecipeStatuses` | NodeId | `Model/RecipeStatuses` enumeration | Startup validation that C# enum matches model. |

The variable is resolved at `Start()`. If `RecipeStatuses` is missing, startup validation is skipped with a warning.

### 11.2 Public methods

```text
CreateRecipe
UpdateRecipe
DeleteRecipe
UpdateRecipeStatus
DuplicateRecipe
ValidateRecipe
ApplyRecipeEnablementRules
```

---

## 12. Method specification: CreateRecipe

### 12.1 Purpose

Create a new recipe using either:

- A blank/default model.
- A template recipe.
- An existing recipe used as source.

### 12.2 Inputs

| Input | Type | Required | Description |
|---|---|---:|---|
| `recipeName` | String | Yes | Name of the new recipe. |
| `recipeFamily` | Float | Yes | Recipe family used to determine allowed phase types and parameter enablement. |
| `sourceRecipeName` | String | No | Optional template/source recipe to copy from. |
| `initialStatus` | `RecipeStatuses` | No | Default must be `draft`. |

### 12.3 Behavior

The method must:

1. Validate input values.
2. Reject duplicate recipe names.
3. Create the recipe using the configured RecipeX schema.
4. Initialize recipe identity and metadata:
   - `RecipeId.Version = "1.0"` (native DB column), unless inherited from a controlled duplication/update operation.
   - `Status = draft` (custom metadata), unless a different allowed initial status is explicitly requested.
5. If `sourceRecipeName` is provided, copy recipe values from the source recipe.
6. If no source recipe is provided, initialize one visible/editable empty step and set remaining steps to unused tail steps.
7. Apply `RecipeFamily` / `PhaseType` enablement rules.
8. Validate the resulting recipe.
9. Persist the recipe only if validation succeeds.

---

## 13. Method specification: UpdateRecipe

### 13.1 Purpose

Update a draft recipe or create a new revision from a non-draft recipe.

### 13.2 Inputs

| Input | Type | Required | Description |
|---|---|---:|---|
| `sourceRecipeName` | String | Yes | Existing recipe to edit or revise. |
| `newRecipeName` | String | Conditional | Name of the new recipe revision. Required when source recipe is not `draft`. |
| `updatedModelRoot` | NodeId or structured object | Yes | Source of the edited recipe values. |

### 13.3 Behavior

The method must:

1. Load the source recipe.
2. Read the source recipe `Status` and `Version`.
3. If source `Status == draft`:
   - Apply changes to the draft recipe.
   - Keep the same version unless project rules require a minor revision also for draft saves.
4. If source `Status != draft`:
   - Create a new recipe record with `newRecipeName`.
   - If source `Status != released`, increment minor version.
   - If source `Status == released`, increment major version and reset minor to `0`.
   - Set the new recipe `Status = draft`.
5. Copy values from `updatedModelRoot`.
6. Apply enablement rules.
7. Validate the recipe.
8. Persist only if validation succeeds.

The original non-draft recipe must remain unchanged.

---

## 14. Method specification: DeleteRecipe

Delete is logical only. The recipe must not be physically removed.

Inputs:

| Input | Type | Required | Description |
|---|---|---:|---|
| `recipeName` | String | Yes | Recipe to archive. |

Behavior:

1. Load the recipe.
2. Reject the operation if the recipe is already `archived`.
3. Set `Status = archived`.

---

## 15. Method specification: UpdateRecipeStatus

Inputs:

| Input | Type | Required | Description |
|---|---|---:|---|
| `recipeName` | String | Yes | Target recipe. |
| `newStatus` | `RecipeStatuses` | Yes | Requested new status. |

Approval comments and electronic signatures are not required in the first implementation.

Behavior:

1. Load the recipe.
2. Validate the requested status transition.
3. Reject backward transitions.
4. If `newStatus == released`, run full recipe validation and reject release if validation fails.
5. Set the new status.

Invalid examples:

```text
draft -> released
prepared -> draft
approved -> prepared
released -> draft
archived -> approved
archived -> released
```

---

## 16. Method specification: DuplicateRecipe

Inputs:

| Input | Type | Required | Description |
|---|---|---:|---|
| `sourceRecipeName` | String | Yes | Recipe to duplicate. |
| `newRecipeName` | String | Yes | Name of the duplicated recipe. |
| `newRecipeFamily` | Float | No | Optional override for `RecipeFamily`. |

Behavior:

1. Load the source recipe.
2. Create a new recipe with `newRecipeName`.
3. Copy all recipe values.
4. Set `Version = 1.0` unless this method is being used internally by `UpdateRecipe`.
5. Set `Status = draft`.
6. If `newRecipeFamily` is provided, update `RecipeFamily`.
7. Recompute enablement rules.
8. Validate the new recipe.
9. Persist only if valid.

---

## 18. Method specification: ValidateRecipe

Validation must check:

1. Recipe exists.
2. `Version` (from `RecipeId.Version`) has valid `major.minor` format.
3. `Status` metadata exists and is a valid `RecipeStatuses` value.
4. `RecipeFamily` exists and is configured.
5. Exactly 20 steps exist.
6. Each step contains exactly 20 step parameters.
7. All active steps have `PhaseType != 0`.
8. All active steps use sequential `PhaseType` values starting from `1`.
9. All unused steps are tail steps with `PhaseType == 0`.
10. No active step appears after an unused step.
11. Each active `PhaseType` is allowed for the selected `RecipeFamily`.
12. Step parameter enablement matches the configured matrix.
13. All enabled `ParameterValue` values are within their `EURange`, when `EURange` is available.

Return value:

```text
IsValid: Boolean
Errors: String[]
Warnings: String[]
```

---

## 19. Method specification: ApplyRecipeEnablementRules

For each step:

1. Read `PhaseType`.
2. If `PhaseType == 0`:
   - Set `StepEnabled = false`.
   - Set all `ParameterEnabled = false`.
3. If `PhaseType != 0`:
   - Verify that the phase is allowed for the current `RecipeFamily`.
   - Set `StepEnabled = true`.
   - Enable only the configured step parameters.
   - Disable all other step parameters.

This method must be idempotent.

---

## 20. Required NetLogic: RecipeListViewEditModelNetLogic

`RecipeListViewEditModelNetLogic` must be added as a child of the ListView used to edit recipe steps.

Public methods:

```text
InitializeEditModel
AddStepBefore
AddStepAfter
DeleteStep
NormalizeSteps
ValidateEditModel
ApplyEditModelEnablementRules
```

### 20.1 Edit-model rules

- Under the hood, the model always contains 20 steps.
- The UI may show only active steps and the current editable empty step.
- Unused tail steps remain present in the model with `PhaseType = 0` and `StepEnabled = false`.
- Add-step operation must preserve the fixed 20-step structure.
- Delete-step operation must preserve the fixed 20-step structure.
- After add/delete operations, active steps remain contiguous and unused steps are at the tail.
- Adding the 21st active step must be rejected.

Because `PhaseType` is only an ordering index, the active sequence must be normalized as:

```text
Active steps use PhaseType values 1, 2, 3, ... N
Unused tail steps use PhaseType 0
```

---

## 21. Recipe edit protection

Both production NetLogics must enforce these rules:

```text
Only draft recipes are directly editable.
Released recipes must never be edited in place.
```

UI controls alone are not sufficient.

The backend NetLogic methods must reject edit operations targeting a non-draft recipe unless the operation creates a new recipe revision.

---

## 22. Required NetLogic: RecipeRuntimeTestToolsNetLogic

### 22.1 Purpose

Provide runtime scripts for testing purposes only.

The logic must support:

- Generation of a variable number of recipes.
- Bulk deletion/archival of generated test recipes.

This NetLogic is intended to help developers and testers quickly populate the RecipeX storage with test data and clean it afterward.

It must not be used as part of the production recipe workflow.

### 22.2 Placement

Recommended placement:

```text
NetLogic/Testing/RecipeRuntimeTestToolsNetLogic
```

or as a child of a dedicated testing-only object.

It should not be added as a child of the production RecipeSchema unless the project has a clear separation between production methods and test methods.

### 22.3 Public methods

The NetLogic must expose the following methods:

```text
GenerateTestRecipes
BulkArchiveTestRecipes
BulkDeleteTestRecipes
```

`BulkArchiveTestRecipes` is the preferred method because normal recipe deletion is logical archival.

`BulkDeleteTestRecipes` may physically delete only recipes that are clearly identified as test recipes, if physical deletion is supported and explicitly allowed in the project.

### 22.4 Method specification: GenerateTestRecipes

#### Purpose

Create a variable number of test recipes for runtime testing.

#### Inputs

| Input | Type | Required | Description |
|---|---|---:|---|
| `count` | Integer | Yes | Number of test recipes to generate. |
| `namePrefix` | String | No | Prefix for generated recipe names. Default: `TEST_RECIPE_`. |
| `recipeFamily` | Float | Yes | Recipe family to use for generated recipes. |
| `activeStepCount` | Integer | No | Number of active steps to generate. Must be `1..20`. |
| `status` | `RecipeStatuses` | No | Initial status. Default: `draft`. |
| `overwriteExistingTestRecipes` | Boolean | No | If true, archive/delete existing matching test recipes before generation. Default: false. |

#### Behavior

The method must:

1. Validate all inputs.
2. Reject `count <= 0`.
3. Reject `activeStepCount < 1` or `activeStepCount > 20`.
4. Generate deterministic and clearly identifiable recipe names.
5. Ensure generated recipe names cannot collide with production recipes.
6. Create each recipe with metadata:
   - `Version = 1.0`.
   - `Status = draft`, unless another allowed test status is requested.
7. Generate active steps with sequential `PhaseType` values from `1` to `activeStepCount`.
8. Set all remaining steps to `PhaseType = 0`.
9. Apply enablement rules.
10. Validate each generated recipe.
11. Persist only valid recipes.
12. Return a structured result containing:
   - Total requested count.
   - Created count.
   - Skipped count.
   - Failed count.
   - Generated recipe names.
   - Validation errors, if any.

#### Naming rule

Generated test recipes must be clearly identifiable.

Recommended naming format:

```text
TEST_RECIPE_yyyyMMdd_HHmmss_0001
TEST_RECIPE_yyyyMMdd_HHmmss_0002
TEST_RECIPE_yyyyMMdd_HHmmss_0003
```

The prefix must be configurable, but the default prefix must clearly indicate that the recipe is for testing.

### 22.5 Method specification: BulkArchiveTestRecipes

#### Purpose

Archive all generated test recipes matching a prefix or selection rule.

This is the preferred cleanup method.

#### Inputs

| Input | Type | Required | Description |
|---|---|---:|---|
| `namePrefix` | String | No | Prefix used to identify test recipes. Default: `TEST_RECIPE_`. |
| `onlyCreatedByThisTool` | Boolean | No | If true, archive only recipes marked as generated by this tool. Default: true. |
| `dryRun` | Boolean | No | If true, return the recipes that would be archived without changing them. Default: true. |

#### Behavior

The method must:

1. Find recipes matching the configured test prefix and/or generated-by marker.
2. Exclude production recipes.
3. If `dryRun == true`, return the candidate list without changing recipe status.
4. If `dryRun == false`, set matching recipes to `Status = archived`.
5. Return a structured result containing:
   - Candidate count.
   - Archived count.
   - Skipped count.
   - Failed count.
   - Affected recipe names.

### 22.6 Method specification: BulkDeleteTestRecipes

#### Purpose

Physically delete generated test recipes only if the project explicitly allows physical removal of test data.

#### Safety requirement

Physical deletion must be disabled by default.

The method must not physically delete recipes unless all of the following are true:

1. The recipe name matches the configured test prefix.
2. The recipe is marked as generated by the test tool, where such marker is available.
3. The caller explicitly requests physical deletion.
4. `dryRun == false`.

If physical deletion is not supported or not allowed, this method must return a clear `PhysicalDeleteNotAllowed` result.

### 22.7 Test data marker

When possible, generated test recipes should contain a metadata marker or machine-level marker such as:

```text
GeneratedBy = RecipeRuntimeTestToolsNetLogic
GeneratedAt = yyyy-MM-ddTHH:mm:ss
```

If RecipeX does not support custom metadata, the marker must be encoded in the recipe name prefix.

### 22.8 Safety

The runtime test tool must be protected.

Recommended options:

- Expose it only in development/test builds.
- Disable it by configuration in production.

The test tool must never bypass core validation rules.

Generated recipes must still pass `ValidateRecipe` before being persisted.

---

## 23. Required NetLogic: RecipeYamlTemplateGeneratorNetLogic

### 23.1 Purpose

Provide a design-time logic to generate a template YAML configuration file.

The generated template is intended to help developers configure:

- Recipe families.
- Allowed phase types.
- Enabled step parameters per phase type.

### 23.2 Placement

Recommended placement:

```text
NetLogic/DesignTime/RecipeYamlTemplateGeneratorNetLogic
```

The logic should be available at design time only, or clearly marked as a development utility.

### 23.3 Public methods

The NetLogic must expose the following method:

```text
GenerateYamlConfigurationTemplate
```

### 23.4 Method specification: GenerateYamlConfigurationTemplate

#### Inputs

| Input | Type | Required | Description |
|---|---|---:|---|
| `outputFileName` | String | No | YAML file name. Default: `recipe_configuration_template.yaml`. |
| `familyCount` | Integer | No | Number of sample recipe families to generate. Default: `1`. |
| `phaseTypeCount` | Integer | No | Number of sample phase types per family. Default: `20`. |
| `stepParameterCount` | Integer | No | Number of step parameters per phase type. Default: `20`. |
| `overwriteExistingFile` | Boolean | No | If false and the file exists, return an error. Default: false. |

#### Behavior

The method must:

1. Resolve the FT Optix `ProjectFiles` directory.
2. Build a YAML configuration template with the requested number of sample families and phase types.
3. Generate valid `allowedPhaseTypes` values.
4. Generate valid `enabledStepParameters` indexes in the range `1..20`.
5. Write the YAML file into the FT Optix `ProjectFiles` directory.
6. Refuse to overwrite an existing file unless `overwriteExistingFile == true`.
7. Return the generated file path and a clear success/error result.

### 23.5 Generated YAML template requirements

The generated YAML must:

- Be syntactically valid YAML.
- Be valid against the configuration validation rules defined in this document.
- Include comments where useful, if supported by the YAML writer implementation.
- Use `1..20` as default phase type indexes.
- Use `1..20` as default step parameter indexes.

Example generated template:

```yaml
recipeFamilies:
  1:
    name: "Recipe family 1"
    allowedPhaseTypes: [1, 2, 3, 4, 5]
    phaseTypes:
      1:
        name: "Phase type 1"
        enabledStepParameters: [1, 2, 3]
      2:
        name: "Phase type 2"
        enabledStepParameters: [1, 2, 3]
      3:
        name: "Phase type 3"
        enabledStepParameters: [1, 2, 3]
```

---

## 24. Error handling requirements

All public methods must return a clear result to the caller.

Recommended result shape:

```text
Success: Boolean
ErrorCode: String
Message: String
ValidationErrors: String[]
```

Examples of error codes:

```text
RecipeAlreadyExists
RecipeNotFound
InvalidRecipeName
InvalidVersionFormat
InvalidStatusTransition
RecipeIsNotDirectlyEditable
ReleasedRecipeIsImmutable
InvalidStepSequence
InvalidPhaseTypeForRecipeFamily
ConfigurationInvalid
MaximumStepCountReached
InvalidTestRecipeCount
InvalidTestRecipePrefix
PhysicalDeleteNotAllowed
YamlTemplateAlreadyExists
ProjectFilesDirectoryNotFound
```

---

## 25. Idempotency and partial-failure requirements

The implementation should be robust against partial failures.

Recommended rules:

- Validation must occur before persistence whenever possible.
- Create/update operations should build a complete candidate recipe first, then persist only when valid.
- Re-running `ApplyRecipeEnablementRules` must be safe.
- Archiving an already archived recipe should return a controlled error or no-op result, according to the selected project convention.
- Test recipe generation must be safe to retry when deterministic names are used.
- Bulk archive/delete operations must support `dryRun` mode.
- Bulk physical delete must be disabled by default.

---

## 26. Suggested implementation sequence

1. Create YAML configuration model for `RecipeFamily`, `PhaseType`, and enabled parameters.
2. Implement `RecipeYamlTemplateGeneratorNetLogic` to generate a valid starter YAML file in `ProjectFiles`.
3. Implement configuration loading and validation.
4. Implement common helpers:
   - Version parser.
   - Version incrementer.
   - Status transition validator.
   - Step sequence validator.
   - Enablement-rule applier.
5. Implement `ValidateRecipe`.
6. Implement `CreateRecipe`.
7. Implement `DuplicateRecipe`.
8. Implement `UpdateRecipe` as direct draft update or revision creation for non-draft recipes.
9. Implement `DeleteRecipe` as archive operation.
10. Implement `UpdateRecipeStatus`.
11. Implement ListView edit-model NetLogic.
12. Implement `RecipeRuntimeTestToolsNetLogic` for test recipe generation and cleanup.
13. Add UI bindings after the production logic is stable.

---

## 27. Acceptance criteria

### 27.1 Recipe creation

- A new blank recipe can be created.
- A recipe can be created from a template.
- The created recipe has valid metadata.
- The created recipe has exactly 20 steps.
- Each step has exactly 20 parameters.
- Unused steps are placed only at the tail.

### 27.2 Versioning

- Draft recipes are directly editable.
- Updating a non-draft, non-released recipe creates a new recipe with minor version incremented.
- Updating a released recipe creates a new recipe with major version incremented and minor set to `0`.
- The source non-draft recipe is never modified by update operations.

### 27.3 Status management

- Invalid and backward status transitions are rejected.
- Releasing a recipe requires successful validation.
- Electronic signature and approval comment are not required in the first implementation.

### 27.4 Recipe edit protection

- Only draft recipes are directly editable.
- Released recipes cannot be edited in place.
- Any change to a released recipe creates a new major version.

### 27.5 Step sequence validation

- Active steps cannot appear after unused steps.
- Active steps are sequentially numbered using `PhaseType = 1..N`.
- Recipes with invalid step ordering cannot be released.
- Recipes with invalid step ordering cannot be applied to the PLC.

### 27.6 Enablement rules

- `RecipeFamily` determines the allowed `PhaseType` values.
- `PhaseType` determines the enabled step parameters.
- Disabled step parameters are not used by the PLC and should not be shown as editable by the UI.

### 27.7 Edit model

- Add-step operation preserves the fixed 20-step structure.
- Delete-step operation preserves the fixed 20-step structure.
- After add/delete operations, active steps remain contiguous and unused steps are at the tail.
- Adding the 21st active step is rejected.

### 27.8 Runtime test recipe generation

- A variable number of test recipes can be generated.
- Generated test recipes are clearly identifiable by prefix and/or metadata marker.
- Generated test recipes pass standard recipe validation.
- Generation rejects invalid count and invalid active step count.
- Generation does not overwrite existing recipes unless explicitly requested.

### 27.9 Runtime bulk cleanup

- Test recipes can be found by prefix and/or generated-by marker.
- Bulk archive supports `dryRun` mode.
- Bulk archive does not affect production recipes.
- Physical deletion is disabled by default.
- Physical deletion, if implemented, affects only recipes clearly identified as test recipes.

### 27.10 Design-time YAML template generation

- A YAML configuration template can be generated in the FT Optix `ProjectFiles` directory.
- The generated YAML is syntactically valid.
- The generated YAML passes the configuration validation rules.
- Existing files are not overwritten unless explicitly requested.

---

## 28. Confirmed decisions

The following decisions are confirmed for the first implementation.

1. `PhaseType` is only an ordering index.
2. `template` recipes are editable.
3. Backward status transitions are not allowed.
4. Only `draft` recipes are directly editable.
5. The YAML configuration file must be stored in the FT Optix `ProjectFiles` directory.
6. Electronic signature and approval comment are not required in the first implementation.
7. Runtime recipe generation and bulk cleanup are testing utilities only.
8. Design-time YAML template generation is required to help initialize configuration.

---

## 29. Notes for the future UI implementation

The UI should be built on top of the NetLogic methods, not around them.

Recommended UI behavior:

- Hide or disable edit controls for non-draft recipes, unless the command creates a new revision.
- Display validation errors returned by `ValidateRecipe`.
- Display only enabled step parameters for the selected `PhaseType`.
- Provide explicit add-before and add-after commands.
- Show a clear warning when the maximum step count is reached.
- Show recipe version and status prominently.
- Never rely only on UI restrictions for critical rules.
- Hide runtime test tools in production.

---

## 30. Summary of required deliverables

The implementation must deliver:

1. `RecipeSchemaNetLogic`
   - `CreateRecipe`
   - `UpdateRecipe`
   - `DeleteRecipe`
   - `UpdateRecipeStatus`
   - `DuplicateRecipe`
   - `ValidateRecipe`
   - `ApplyRecipeEnablementRules`

2. `RecipeListViewEditModelNetLogic`
   - `InitializeEditModel`
   - `AddStepBefore`
   - `AddStepAfter`
   - `DeleteStep`
   - `NormalizeSteps`
   - `ValidateEditModel`
   - `ApplyEditModelEnablementRules`

3. `RecipeRuntimeTestToolsNetLogic`
   - `GenerateTestRecipes`
   - `BulkArchiveTestRecipes`
   - `BulkDeleteTestRecipes`, only if physical deletion is explicitly allowed.

4. `RecipeYamlTemplateGeneratorNetLogic`
   - `GenerateYamlConfigurationTemplate`

5. External YAML configuration stored in the FT Optix `ProjectFiles` directory for:
   - Recipe families.
   - Allowed phase types.
   - Enabled step parameters per phase type.

6. Structured method results.

7. Validation logic suitable for both runtime protection and UI feedback.
