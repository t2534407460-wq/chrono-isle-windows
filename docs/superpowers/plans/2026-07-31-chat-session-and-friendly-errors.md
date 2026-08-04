# Chat Session And Friendly Errors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep empty chats out of recent conversations until the first user message and replace technical assistant failures with natural Chinese prompts.

**Architecture:** `LifeViewModel` owns a transient selected session until a message is sent. `LifeDataService.Message` atomically creates a missing session before inserting its first message, while `Sessions()` excludes sessions without user messages. `AssistantActionService` and `LifeViewModel` map internal failures to user-safe text without changing internal command validation.

**Tech Stack:** .NET 8, C#, WPF, SQLite, xUnit

---

### Task 1: Delay conversation persistence

**Files:**
- Modify: `tests/ChronoIsle.Tests/QuickAskConversationTests.cs`
- Modify: `src/ChronoIsle.App/Services/StorageV2/LifeDataService.cs`
- Modify: `src/ChronoIsle.App/ViewModels/LifeViewModel.cs`

- [x] **Step 1: Write failing storage and view-model tests**

Replace the existing empty-conversation expectation with tests that assert:

```csharp
var first = viewModel.BeginQuickAskConversation();
var second = viewModel.BeginQuickAskConversation();

Assert.NotEqual(first.Id, second.Id);
Assert.Empty(data.Sessions());
Assert.Empty(viewModel.Sessions);
Assert.Equal(second.Id, viewModel.SelectedSession?.Id);
```

Add a storage test:

```csharp
var now = DateTime.Now;
var session = new ChatSession(Guid.NewGuid().ToString("N"), "新对话", now, now);
data.Message(session.Id, "user", "一分钟后提醒我");

var persisted = Assert.Single(data.Sessions());
Assert.Equal(session.Id, persisted.Id);
Assert.Equal("一分钟后提醒我", persisted.Title);
```

- [x] **Step 2: Run focused tests and verify RED**

Run:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -c Debug --no-restore -m:1 -nodeReuse:false --filter "FullyQualifiedName~QuickAskConversationTests" --verbosity minimal
```

Expected: FAIL because empty sessions are currently inserted immediately and messages cannot persist a transient session.

- [x] **Step 3: Implement transient sessions and first-message upsert**

Change `Sessions()` to require an associated user message. Prepend this statement to the existing `Message` transaction:

```sql
INSERT OR IGNORE INTO chat_sessions(id,title,created_at,updated_at)
VALUES($session,$newTitle,$now,$now);
```

Change `LifeViewModel.BeginQuickAskConversation()` to create and select an in-memory `ChatSession` without refreshing `Sessions`. After saving the first user message in `Send`, refresh the list only when the selected session was transient and reselect the persisted row.

- [x] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: all `QuickAskConversationTests` pass.

### Task 2: Replace technical replies

**Files:**
- Modify: `tests/ChronoIsle.Tests/AssistantActionServiceV2Tests.cs`
- Modify: `tests/ChronoIsle.Tests/LifeAssistantTests.cs`
- Modify: `src/ChronoIsle.App/Services/AssistantActionService.cs`
- Modify: `src/ChronoIsle.App/ViewModels/LifeViewModel.cs`

- [x] **Step 1: Write failing reply tests**

Extend the missing-time test to require:

```csharp
Assert.Equal("想在什么时候提醒你？", first.Reply);
Assert.DoesNotContain("required_fields_missing", first.Reply);
Assert.DoesNotContain("remind", first.Reply);
```

Update the planner-exception test to require a natural failure message and assert that the injected exception detail is absent.

- [x] **Step 2: Run focused tests and verify RED**

Run:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -c Debug --no-restore -m:1 -nodeReuse:false --filter "FullyQualifiedName~Missing_time_is_saved_as_turn_context|FullyQualifiedName~Planner_exception" --verbosity normal
```

Expected: FAIL because current replies expose `required_fields_missing`, `remind`, and exception text.

- [x] **Step 3: Implement user-safe reply mapping**

Add a command-aware clarification prompt helper in `AssistantActionService` for common missing fields. Remove internal codes and exception details from planner, parser, plan submission, local summary, confirmation, and time-selection replies. Change the unexpected exception reply in `LifeViewModel.Send` to:

```text
这次没有处理成功，请稍后重试。你的事项没有被修改。
```

- [x] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: both focused reply tests pass.

### Task 3: Verify and deploy

**Files:**
- Verify all modified source and test files

- [x] **Step 1: Run all tests**

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -c Debug --no-restore -m:1 -nodeReuse:false --verbosity minimal
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Debug --no-restore -m:1 -nodeReuse:false --verbosity minimal
```

Expected: all tests pass.

- [x] **Step 2: Build Release**

```powershell
dotnet build ChronoIsle.sln -c Release --no-restore -m:1 -nodeReuse:false -p:NuGetAudit=false --verbosity minimal
```

Expected: build succeeds with zero errors.

- [x] **Step 3: Publish and restart**

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-restart.ps1
```

Expected: the current repository's Release publish executable starts and responds.
