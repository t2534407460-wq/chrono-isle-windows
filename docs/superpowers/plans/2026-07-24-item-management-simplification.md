# 事项管理简化与中文语义 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让灵动岛与事项管理显示清晰的中文枚举，并收敛事项管理的默认编辑项。

**Architecture:** 在现有视图代码中为枚举定义显示文本映射，持久化仍使用原枚举。事项管理将附加字段置于单条事项内的可展开区域；灵动岛按钮复用既有管理窗口服务。

**Tech Stack:** WPF、C#、xUnit。

---

### Task 1: 中文枚举显示

**Files:**
- Modify: `src/OpenIsland.App/Views/LifeIslandWindow.xaml.cs`
- Modify: `src/OpenIsland.App/Views/LifeManagementWindow.xaml.cs`

- [ ] **Step 1: 用中文显示项替代直接绑定枚举**

```csharp
new ComboBoxItem { Content = "低精力", Tag = EnergyLevel.Low }
```

- [ ] **Step 2: 保持选择结果映射回原始 `EnergyLevel` 与 `LifePriority` 值**

```csharp
if (energy.SelectedItem is ComboBoxItem { Tag: EnergyLevel value }) recommendationEnergy = value;
```

### Task 2: 收敛事项管理编辑区

**Files:**
- Modify: `src/OpenIsland.App/Views/LifeManagementWindow.xaml.cs`

- [ ] **Step 1: 保留优先级与超时宽限作为默认显示控件**

```csharp
editor.Children.Add(priority);
editor.Children.Add(overdueGrace);
```

- [ ] **Step 2: 将分类、预计时长、能量放入“更多设置”可展开区域，保存时继续写入原有 `TaskAttributes`**

```csharp
details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
```

### Task 3: 灵动岛管理入口与验证

**Files:**
- Modify: `src/OpenIsland.App/Views/LifeIslandWindow.xaml.cs`
- Test: `tests/OpenIsland.Tests/ProductivityServicesTests.cs`

- [ ] **Step 1: 添加“事项管理”快捷按钮，位于“问 AI”后并复用 `LifeManagementWindow` 打开逻辑。**

```csharp
new Button { Content = "事项管理", Style = (Style)FindResource("IslandQuick") }
```

- [ ] **Step 2: 为属性保存的中文控件所使用的原始枚举值增加回归测试。**

```csharp
Assert.Equal(LifePriority.High, service.Get("todo")!.Priority);
```

- [ ] **Step 3: 运行完整测试与差异检查。**

```powershell
dotnet test tests\OpenIsland.Tests\OpenIsland.Tests.csproj --no-restore
git diff --check
```
