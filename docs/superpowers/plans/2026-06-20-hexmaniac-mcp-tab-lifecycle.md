# MCP ROM/Tab Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the MCP open a `.gba` into the running GUI as a new tab, and close a tab or a whole ROM (all its tabs), with unsaved-change protection.

**Architecture:** A small Core helper (`EditorViewModel.OpenFileAsTab`) provides a deterministic synchronous "open as new tab" (the existing `Open` command is `async void` and may import into the selected tab). New pipe handlers (`open_rom`, `close_tab`, `close_rom`) run on the WPF Dispatcher thread against `EditorViewModel`'s tabs; the matching MCP tools route through the existing `Dispatch` (live→GUI pipe, else headless) and carry `mode`. Close refuses on unsaved changes (`ChangeHistory.HasDataChange`) unless `force`, and never triggers a GUI save dialog (`TagAsSaved()` before `Close` when forcing).

**Tech Stack:** C# / .NET (Core net6.0, WPF net6.0, MCP net8.0), `System.Text.Json`, ModelContextProtocol 1.4.0, xUnit, bash + jq gates.

## Global Constraints

- Headless must keep working unchanged; `test/mcp-smoke.sh` stays ALL GREEN every task; `open_rom` headless behavior is unchanged.
- GUI model/tab access ONLY on the WPF Dispatcher thread (pipe `Handle` already runs inside `Application.Current.Dispatcher.Invoke`).
- Every MCP tool result includes `"mode": "live" | "headless"` via the existing `Stamp`/`Dispatch`. Live errors surface as `mode:"live"`.
- `close_tab`/`close_rom` are live-only → clear `{error}` in headless. Close refuses if `ChangeHistory.HasDataChange` and `force` is false; with `force` it discards (no disk write) via `ChangeHistory.TagAsSaved()` then `Close`, and NEVER shows a save dialog.
- `close_rom` closes every tab whose `Model` is reference-equal to the resolved tab's `Model`.
- Errors return `{error}`; never crash. Closing the last tab is allowed.
- Existing reusable helpers — reuse, don't reinvent: `RomTools.Dispatch`/`Stamp`; `AutomationPipeServer` `ResolveTab`, `Ok`, `NoTab`, `Str`, `StrOrNull`, `Int`, `GuiFileSystem()`, `ListTabs`, and the `editor` field. The headless gate tool-count assertion is currently `17`.
- Build: GUI `dotnet build -c Release -p:PlatformTarget=x64`; MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; unit tests `(cd src/HexManiac.Tests && dotnet test --filter ...)`. Run the headless gate with NO GUI running (`taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`).

---

## File Structure

- **Modify `src/HexManiac.Core/ViewModels/EditorViewModel.cs`** — extract `OpenFileAsTab(LoadedFile) : IViewPort` from `ExecuteOpen`'s new-tab tail; `ExecuteOpen` calls it.
- **Create `src/HexManiac.Tests/EditorOpenFileAsTabTests.cs`** — unit test the helper.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — `open_rom`, `close_tab`, `close_rom` pipe cases + a `Bool` param helper.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — route `open_rom` via `Dispatch`; add `close_tab`/`close_rom` tools.
- **Modify `test/mcp-smoke.sh`** — tool count 17→19; close tools live-only error.
- **Modify `test/mcp-live-smoke.sh`** — open second ROM, close it; unsaved+force.
- **Modify `BUILD.md`** — document the lifecycle tools.

---

## Task 1: Core `EditorViewModel.OpenFileAsTab` helper

**Files:**
- Modify: `src/HexManiac.Core/ViewModels/EditorViewModel.cs`
- Create: `src/HexManiac.Tests/EditorOpenFileAsTabTests.cs`

**Interfaces:**
- Consumes: existing `ExecuteOpen` body (lines ~781–806): `fileSystem`, `allowLoadingMetadata`, `Singletons`, `workDispatcher`, `MapTutorials`, `FileSystem`, `PythonTool`, `Add(ITabContent)`. `LoadedFile`, `HardcodeTablesModel`, `PokemonModel`, `ViewPort`, `StoredMetadata` are already in scope in this file.
- Produces: `public IViewPort OpenFileAsTab(LoadedFile file)` — builds the model+ViewPort for `file` and `Add`s it as a new tab; returns the added `ViewPort` (or `null` if `file` is null). Always opens a NEW tab (no TryImport-into-selected behavior).

- [ ] **Step 1: Write the failing test.** Create `src/HexManiac.Tests/EditorOpenFileAsTabTests.cs`:

```csharp
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class EditorOpenFileAsTabTests : BaseViewModelTestClass {
      [Fact] public void OpenFileAsTab_AddsANewTab() {
         var editor = New.EditorViewModel();
         int before = editor.Count;
         // A non-.gba name builds a PokemonModel from arbitrary bytes (no ROM/Singletons tables needed).
         var tab = editor.OpenFileAsTab(new LoadedFile("scratch.bin", new byte[0x200]));
         Assert.NotNull(tab);
         Assert.Equal(before + 1, editor.Count);
         Assert.Same(tab, editor[editor.Count - 1]);
      }
      [Fact] public void OpenFileAsTab_NullFileReturnsNull() {
         var editor = New.EditorViewModel();
         int before = editor.Count;
         Assert.Null(editor.OpenFileAsTab(null));
         Assert.Equal(before, editor.Count);
      }
   }
}
```

(`New.EditorViewModel()` is the existing test factory; `editor.Count` and `editor[i]` index the tabs — confirm against `EditorViewModel`'s indexer/`Count` used elsewhere in tests, e.g. `GeneralAppTests`.)

- [ ] **Step 2: Run the tests to verify they fail.** Run: `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~EditorOpenFileAsTabTests")` — Expected: FAIL (`OpenFileAsTab` not defined).

- [ ] **Step 3: Extract the helper.** In `src/HexManiac.Core/ViewModels/EditorViewModel.cs`, add the method (place it next to `ExecuteOpen`), moving the new-tab tail of `ExecuteOpen` into it:

```csharp
public IViewPort OpenFileAsTab(LoadedFile file) {
   if (file == null) return null;
   UpdateRecentFiles(file.Name);
   string[] metadataText = new string[0];
   if (allowLoadingMetadata) metadataText = fileSystem.MetadataFor(file.Name) ?? new string[0];
   StoredMetadata metadata;
   try {
      metadata = new StoredMetadata(metadataText);
   } catch (System.ArgumentNullException nullEx) { ErrorMessage = nullEx.Message; return null; }
     catch (System.ArgumentOutOfRangeException rangeEx) { ErrorMessage = rangeEx.Message; return null; }
   IDataModel model = file.Name.ToLower().EndsWith(".gba")
      ? new HardcodeTablesModel(Singletons, file.Contents, metadata)
      : new PokemonModel(file.Contents, metadata, Singletons);
   var viewPort = new ViewPort(file.Name, model, workDispatcher, Singletons, MapTutorials, FileSystem, PythonTool);
   if (metadata.IsEmpty || StoredMetadata.NeedVersionUpdate(metadata.Version, Singletons.MetadataInfo.VersionNumber)) {
      _ = viewPort.Model.InitializationWorkload.ContinueWith(task => {
         if (!Singletons.GameReferenceTables.TryGetValue(model.GetGameCode(), out var refTable)) refTable = null;
         fileSystem.SaveMetadata(file.Name, viewPort.Model.ExportMetadata(refTable, Singletons.MetadataInfo).Serialize());
      }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
   }
   Add(viewPort);
   return viewPort;
}
```

Then replace those same lines inside `ExecuteOpen` (the block from `UpdateRecentFiles(file.Name);` through `Add(viewPort);`) with `OpenFileAsTab(file);` so `ExecuteOpen`'s new-tab path delegates to the helper (its earlier TryImport-into-selected branch stays unchanged).

- [ ] **Step 4: Run the tests to verify they pass.** Run: `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~EditorOpenFileAsTabTests")` — Expected: 2/2 pass.

- [ ] **Step 5: Build MCP + GUI.** `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` and `dotnet build -c Release -p:PlatformTarget=x64`. Expected: 0 errors.

- [ ] **Step 6: Headless gate (regression).** No GUI running. Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN` (unchanged; only an internal refactor + new tool-less code).

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Core/ViewModels/EditorViewModel.cs src/HexManiac.Tests/EditorOpenFileAsTabTests.cs
git commit -m "Core: extract EditorViewModel.OpenFileAsTab from ExecuteOpen"
```

---

## Task 2: Live `open_rom` (open a new tab in the running GUI)

**Files:**
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `test/mcp-live-smoke.sh`

**Interfaces:**
- Consumes: `EditorViewModel.OpenFileAsTab(LoadedFile)` (Task 1); `GuiFileSystem().LoadFile(string)` (`IFileSystem.LoadFile` → `LoadedFile`); `editor` tab collection (`Count`, indexer, `IndexOf`-like scan); existing `RomSession.Load`. `Dispatch`/`Stamp`.
- Produces: pipe method `open_rom` (live new-tab open); MCP `open_rom` routed via `Dispatch` (live result `{ ok, path, index, file, mode:"live" }`; headless unchanged).

- [ ] **Step 1: Write the failing test — extend `test/mcp-live-smoke.sh`.** After the last existing live driver line, add (open a second copy of the test ROM, then list tabs):

```bash
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$ROMW\"}}}"; sleep 8
  printf '%s\n' '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"list_open_roms","arguments":{}}}'; sleep 1
```

(The gate already defines `ROMW` = the Windows path to `test/roms/firered.gba`; reuse it.) Add assertions:

```bash
[ "$(rt 13 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "open_rom mode=live" || bad "open_rom not live"
[ "$(rt 13 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "open_rom ok" || bad "open_rom ok"
[ "$(rt 14 | jq -r '.tabs | length' 2>/dev/null)" -ge 2 ] && ok "two tabs open after open_rom" || bad "open_rom did not add a tab"
```

- [ ] **Step 2: Run the live gate to verify it fails.** Run: `bash test/mcp-live-smoke.sh` — Expected: FAIL — `open_rom` is currently headless-only, so against the live GUI it does not add a tab (`list_open_roms` still shows 1).

- [ ] **Step 3: Add the pipe handler.** In `AutomationPipeServer.cs`, before `default:`:

```csharp
            case "open_rom": {
               var path = Str(p, "path");
               if (string.IsNullOrEmpty(path)) return new AutoResponse(false, null, "Provide an absolute 'path' to a .gba file.");
               var full = System.IO.Path.GetFullPath(path);
               if (!System.IO.File.Exists(full)) return new AutoResponse(false, null, $"File not found: {full}");
               var loaded = GuiFileSystem().LoadFile(full);
               if (loaded == null) return new AutoResponse(false, null, $"Could not load: {full}");
               var vp = editor.OpenFileAsTab(loaded);
               if (vp == null) return new AutoResponse(false, null, editor.ErrorMessage is { Length: > 0 } e ? e : "Open failed.");
               int index = -1, i = 0;
               foreach (var t in editor) { if (ReferenceEquals(t, vp)) { index = i; break; } i++; }
               return Ok(new { ok = true, path = full, index, file = vp.FullFileName ?? vp.Name });
            }
```

- [ ] **Step 4: Route `open_rom` through `Dispatch` in `RomTools.cs`.** Replace the existing `OpenRom` body:

```csharp
   [McpServerTool(Name = "open_rom")]
   [Description("Open a GBA Pokémon ROM from an absolute path. Live: opens it as a new tab in the running GUI. Headless: loads it as the single session ROM.")]
   public string OpenRom(RomSession session, [Description("Absolute path to a .gba ROM file")] string path) {
      var p = new Dictionary<string, object?> { ["path"] = path };
      return Dispatch("open_rom", p, null, null, () => {
         session.Load(path);
         var model = session.Require();
         return new { ok = true, path, length = model.Count, anchorCount = model.Anchors.Count };
      });
   }
```

(The headless lambda returns the same object the old `Headless(...)` wrapper stamped; `Dispatch` stamps `mode:"headless"` when no GUI. When a GUI is reachable, the pipe opens a new tab and the result is stamped `mode:"live"`.)

- [ ] **Step 5: Build GUI + MCP.** Both → 0 errors.

- [ ] **Step 6: Run the live gate to verify it passes.** Run: `bash test/mcp-live-smoke.sh` — Expected: `LIVE GREEN`, including `open_rom mode=live`, `open_rom ok`, `two tabs open after open_rom`. (If only the first-run `list_open_roms` timing flake appears, re-run once after `taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`.)

- [ ] **Step 7: Headless gate (regression).** No GUI. Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN` — `open_rom` headless still loads the session (the open/read/save/reload assertions pass; result still `mode:"headless"`).

- [ ] **Step 8: Commit.**
```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs src/HexManiac.Mcp/RomTools.cs test/mcp-live-smoke.sh
git commit -m "Live open_rom: open a .gba as a new GUI tab"
```

---

## Task 3: `close_tab` + `close_rom` (live-only)

**Files:**
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `test/mcp-smoke.sh` (live-only errors + count 17→19), `test/mcp-live-smoke.sh` (close round-trips)

**Interfaces:**
- Consumes: `ResolveTab`, `Ok`, `NoTab`, `Str`, `Int`, `GuiFileSystem()`, `editor`; `ViewPort.ChangeHistory.HasDataChange`, `ViewPort.ChangeHistory.TagAsSaved()`, `ViewPort.Close` (`ICommand`, parameter `IFileSystem`), `ViewPort.Model`, `ViewPort.FullFileName`/`Name`; `Dispatch`.
- Produces: pipe methods `close_tab`, `close_rom`; private `static bool Bool(JsonElement p, string key, bool fallback)`; MCP tools `close_tab(tab?, tabFile?, force=false)`, `close_rom(tab?, tabFile?, force=false)` (live-only). Results `{ ok, closed|closedCount, remaining, mode }` or `{ error }`.

- [ ] **Step 1: Write the failing tests.** In `test/mcp-smoke.sh`: bump tool count `17`→`19`; after the last driver line add:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":33,"method":"tools/call","params":{"name":"close_tab","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":34,"method":"tools/call","params":{"name":"close_rom","arguments":{}}}'; sleep 1
```
Assertions (headless = live-only error):
```bash
[ "$(result_text 33 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "close_tab live-only in headless" || bad "close_tab headless error"
[ "$(result_text 34 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "close_rom live-only in headless" || bad "close_rom headless error"
```
Bump count line to `19`.

In `test/mcp-live-smoke.sh`, after the id:14 `list_open_roms` (two tabs) add — close the second tab (index 1), then confirm one remains:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"close_tab","arguments":{"tab":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":16,"method":"tools/call","params":{"name":"list_open_roms","arguments":{}}}'; sleep 1
```
Assertions:
```bash
[ "$(rt 15 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "close_tab ok" || bad "close_tab"
[ "$(rt 15 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "close_tab mode=live" || bad "close_tab not live"
[ "$(rt 16 | jq -r '.tabs | length' 2>/dev/null)" = "1" ] && ok "one tab after close_tab" || bad "close_tab did not remove tab"
```
(Both tabs opened from the same clean ROM are unsaved-free, so `close_tab` without `force` succeeds. The second tab is index 1.)

- [ ] **Step 2: Run both gates to verify failure.** `bash test/mcp-smoke.sh` (no GUI) FAILs (close tools unknown; count 17≠19). `bash test/mcp-live-smoke.sh` FAILs (close_tab unknown → headless fallback error, not `mode:live`; tab not removed).

- [ ] **Step 3: Add the pipe handlers + `Bool` helper.** In `AutomationPipeServer.cs`, before `default:`:

```csharp
            case "close_tab": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               bool force = Bool(p, "force", false);
               if (vp.ChangeHistory.HasDataChange && !force)
                  return new AutoResponse(false, null, $"Tab '{vp.FullFileName ?? vp.Name}' has unsaved changes; pass force=true to discard.");
               var file = vp.FullFileName ?? vp.Name;
               CloseTabNoPrompt(vp);
               return Ok(new { ok = true, closed = file, remaining = CountTabs() });
            }
            case "close_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               bool force = Bool(p, "force", false);
               var group = new List<ViewPort>();
               foreach (var t in editor) if (t is ViewPort v && ReferenceEquals(v.Model, vp.Model)) group.Add(v);
               if (!force) {
                  var dirty = group.FirstOrDefault(v => v.ChangeHistory.HasDataChange);
                  if (dirty != null) return new AutoResponse(false, null, $"ROM has unsaved changes in tab '{dirty.FullFileName ?? dirty.Name}'; pass force=true to discard.");
               }
               foreach (var v in group) CloseTabNoPrompt(v);
               return Ok(new { ok = true, closedCount = group.Count, remaining = CountTabs() });
            }
```

And the helpers (next to the other private helpers):

```csharp
      // Close a tab without ever showing a save dialog: tag the history saved (so
      // CloseExecuted skips its TrySavePrompt) then run the tab's Close. Discards
      // unsaved edits (no disk write). Caller already enforced the force gate.
      private void CloseTabNoPrompt(ViewPort vp) {
         if (vp.ChangeHistory.HasDataChange) vp.ChangeHistory.TagAsSaved();
         vp.Close.Execute(GuiFileSystem());
      }

      private int CountTabs() {
         int n = 0;
         foreach (var t in editor) if (t is ViewPort) n++;
         return n;
      }

      private static bool Bool(JsonElement p, string key, bool fallback) {
         if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(key, out var v)) return fallback;
         return v.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
            _ => fallback,
         };
      }
```

(`List`/`FirstOrDefault` need `System.Collections.Generic` and `System.Linq`, already imported in this file.)

- [ ] **Step 4: Add the MCP tools.** In `RomTools.cs`:

```csharp
   [McpServerTool(Name = "close_tab")]
   [Description("Close one tab in the live GUI (default: active tab). Refuses if the tab has unsaved changes unless force=true (which discards them). Live GUI only.")]
   public string CloseTab(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Discard unsaved changes")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("close_tab", p, tab, tabFile, () => RomAutomation.Err("close_tab requires the live GUI."));
   }

   [McpServerTool(Name = "close_rom")]
   [Description("Close ALL tabs showing the resolved tab's ROM in the live GUI. Refuses if any of those tabs has unsaved changes unless force=true (which discards them). Live GUI only.")]
   public string CloseRom(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Discard unsaved changes")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("close_rom", p, tab, tabFile, () => RomAutomation.Err("close_rom requires the live GUI."));
   }
```

- [ ] **Step 5: Build GUI + MCP.** Both → 0 errors.

- [ ] **Step 6: Run both gates.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` (incl. `close_tab live-only in headless`, `close_rom live-only in headless`, `tools/list shows 19 tools`). `bash test/mcp-live-smoke.sh` → `LIVE GREEN` (incl. `close_tab ok`, `close_tab mode=live`, `one tab after close_tab`). Re-run the live gate once if only the first-run `list_open_roms` timing flake appears.

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh test/mcp-live-smoke.sh
git commit -m "MCP close_tab/close_rom tools (live-only, unsaved-change guard)"
```

---

## Task 4: Docs

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1: Document the lifecycle tools.** In `BUILD.md`, near the other tool docs, add:

```markdown
### ROM/tab lifecycle

- `open_rom` — live: opens the `.gba` as a new tab in the running GUI; headless:
  loads it as the single session ROM.
- `save_rom` — saves the resolved tab to its file (live) / the loaded ROM (headless).
- `close_tab` — close one tab (default: active). **Live only.** Refuses on unsaved
  changes unless `force=true` (which discards them; no disk write, no dialog).
- `close_rom` — close ALL tabs showing the resolved tab's ROM. **Live only.** Same
  unsaved-change guard / `force` semantics.
```

- [ ] **Step 2: Commit.**
```bash
git add BUILD.md
git commit -m "Docs: ROM/tab lifecycle tools in BUILD.md"
```

---

## Self-Review notes

- **Spec coverage:** live `open_rom` as new tab (T1 helper + T2 wiring); `save_rom` unchanged (documented T4); `close_tab` (T3); `close_rom` closing all tabs of a model (T3); unsaved guard via `HasDataChange` + `force` + `TagAsSaved`-then-`Close` no-prompt (T3 `CloseTabNoPrompt`); live-only errors headless (T3); `mode` on all (Dispatch); live + headless gates (T2, T3); docs (T4). All covered.
- **Headless invariant:** `test/mcp-smoke.sh` run in T1, T2, T3.
- **Type consistency:** `OpenFileAsTab(LoadedFile) : IViewPort` defined T1, consumed by the pipe `open_rom` (T2). Pipe `close_tab`/`close_rom` use `CloseTabNoPrompt`/`CountTabs`/`Bool` defined in T3. `open_rom` headless lambda returns the same `{ok,path,length,anchorCount}` the old tool emitted. Tool count 17→19 (close_tab, close_rom); `open_rom`/`save_rom` add no tools.
- **Spec deviation (justified):** the spec said "no Core changes expected"; T1 adds `EditorViewModel.OpenFileAsTab` because the existing `Open` command is `async void` and can import into the selected tab, so it can't deterministically return a new tab — and the WPF layer can't replicate the open (it needs `EditorViewModel`'s private `Singletons`/`workDispatcher`/etc.). The change is a pure refactor (ExecuteOpen delegates to it), gated by the unchanged headless smoke run.
