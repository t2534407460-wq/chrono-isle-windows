# Default Reminder Title Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a reminder titled “提醒” when the parsed reminder has a valid time but no explicit title.

**Architecture:** Apply the deterministic default while normalizing the model's `create_reminder` JSON into the trusted local command envelope. Remove only the corresponding model-reported `title` omission so the existing local execution policy can still reject every other missing required field.

**Tech Stack:** .NET 8, C#, xUnit, WPF application services

---

### Task 1: Default an omitted reminder title

**Files:**
- Modify: `src/ChronoIsle.App/Services/Commanding/OperationArgumentParserV2.cs`
- Test: `tests/ChronoIsle.Tests/AssistantConversationPlanningV2Tests.cs`

- [x] **Step 1: Write the failing regression test**

Add an xUnit test that normalizes this model response:

```json
{
  "arguments": {
    "title": null,
    "remind": {
      "relativeExpression": "一分钟后",
      "originalText": "一分钟后"
    }
  },
  "missingFields": ["title"],
  "ambiguityReasons": []
}
```

Assert that the resulting `CreateReminderArgumentsV1.Title` is `提醒`, that `title` is absent from `MissingFields`, and that `AssistantExecutionPolicy.Evaluate` selects `AutoExecute`.

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~OperationParser_DefaultsMissingReminderTitle" --verbosity minimal
```

Expected: FAIL because the normalized title is currently `null`.

- [x] **Step 3: Implement the minimal normalization rule**

In `OperationArgumentParserV2.TryNormalizeAndParse`, normalize arguments and missing fields into local variables. For `ConversationOperationV2.CreateReminder` only, when normalized `title` is null or whitespace:

```csharp
arguments["title"] = "提醒";
```

Remove case-insensitive `title` entries from the normalized missing-fields array. Preserve all other missing fields and ambiguity reasons.

- [x] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: PASS.

- [x] **Step 5: Run regression tests and build**

Run sequentially with `MSBUILDDISABLENODEREUSE=1`:

```powershell
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -c Debug --no-restore --verbosity minimal
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Debug --no-restore --verbosity minimal
dotnet build ChronoIsle.sln -c Release --no-restore -p:NuGetAudit=false --verbosity minimal
```

Expected: all tests and the Release build pass.

- [x] **Step 6: Publish and restart the application**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-restart.ps1
```

Verify that the running `ChronoIsle.exe` resolves to the current repository's Release publish directory and is responding.
