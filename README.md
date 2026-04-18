# Dev Utils — Visual Studio 2022 Extension

[![Visual Studio Marketplace](https://img.shields.io/visual-studio-marketplace/v/DiegoMartins.DevUtils.ExtensionVS?label=VS%20Marketplace)](https://marketplace.visualstudio.com/items?itemName=DiegoMartins.DevUtils.ExtensionVS)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

A productivity extension for **Visual Studio 2022** that automates common development tasks — interface extraction, DI registration, dependency injection analysis and code formatting — directly from the Solution Explorer context menu.

---

## Features

All commands are available via right-click on a **project**, **folder**, **file** or **solution node** under the **Dev Utils** submenu.

### Batch Extract Interfaces
Scans all public, non-static classes in the selected project and generates `IClassName` interfaces in bulk. Adds the interface to the class's base list automatically and registers the new files in the project.

### Generate DI Registration
Finds all `IInterface → Class` pairs in the project and generates a static extension class with a single `IServiceCollection` extension method containing all `AddScoped<I, C>()` calls.

### DI Injection Analyzer
Analyzes the entire solution to find classes that use registered implementations via direct `new ClassName()` instantiation instead of constructor injection. Shows a dialog with status indicators:

| Status | Meaning |
|--------|---------|
| ✔ Safe | Can be injected without issues |
| ⚠ Warning | Multiple constructors or edge cases detected |
| ✖ Error | Direct `new` found in production code, static class, or primitive params |
| ✔ Done | Already injected |

Clicking **Inject Selected** will:
- Add a `private readonly IInterface _field;`
- Add or update the constructor parameter and assignment
- Replace all `new ClassName(...)` usages with the field reference
- Remove redundant local variable declarations
- Unwrap `using (var x = new ClassName())` blocks

### Format All Files
Formats all `.cs` files in the selected project or solution.

- **Auto-detects strategy:**
  - If **no `.editorconfig`** is found → runs `dotnet format` in the background (fast, no file opening)
  - If **`.editorconfig` is found** → uses Visual Studio's built-in `Edit.FormatDocument` command per file

All operations are logged to the **Dev Utils** pane in the Output window.

---

## Requirements

- Visual Studio 2022 (17.x)
- .NET Framework 4.7.2+
- For `dotnet format` fast path: [.NET SDK](https://dotnet.microsoft.com/download) installed

---

## Installation

Install directly from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=DiegoMartins.DevUtils.ExtensionVS) or search for **Dev Utils** in **Extensions → Manage Extensions** inside Visual Studio.

---

## Building from Source

```bash
git clone https://github.com/DiegoSDeveloper/DevUtilsExtensionVS.git
cd DevUtilsExtensionVS
```

Open `DevUtils.ExtensionVS.sln` in Visual Studio 2022 and press **F5** to launch the experimental instance.

---

## License

MIT — see [LICENSE.txt](LICENSE.txt)
