# KH3D FSM Editor

An experimental editor for the **`.fsm` (Finite State Machine)** format used by
*Kingdom Hearts 3D: Dream Drop Distance* to drive entity AI — states, the
transitions between them, and the conditions that gate each one.

📖 **[Format documentation →](https://kite2810.github.io/KH3D-FSM-Editor/)**

## What it does

- Open a `.fsm` (drag-and-drop or file picker) and browse every transition in one list, with an **All / Attack** filter and search.
- Edit a selected transition in the details panel:
  - **Retarget** it to any state via a dropdown.
  - Change a condition's **variable**, **operator** (`< / <= / > / >= / is`), and **value** (flag or threshold).
  - Redirect a single transition — or all of them — to `Idle`.
- Every edit is a **size-preserving in-place patch**, so the file can't be structurally corrupted. A one-time `.bak` is written and **Revert** restores the original.
- **Save As copy** leaves the donor file untouched; **Save report** dumps a text summary.

## Format

The binary layout (`@FSM` v3 — header, symbol table, record table, transition
entries, condition details) is documented in full here:

**https://kite2810.github.io/KH3D-FSM-Editor/**

## Building

A .NET 8 WPF app (Windows). Open `FsmTool.sln` in Visual Studio and build, or:

```
dotnet build FsmTool.sln -c Release
```

## Status

Experimental. Edits are in-place only (no adding/removing transitions or
conditions, by design — those would shift every pointer). Unknown fields are
marked as such in the documentation.
