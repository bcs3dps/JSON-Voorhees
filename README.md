# Voorhees 2.5.0

**A hierarchical editor for JSON, INI, Klipper-family config and registry (.reg) files, for Windows.**

*"A really killer JSON app."*

Copyright (c) 2026 B. C. Services. Licensed under the GNU General Public License, version 2 or (at your option) any later version -- see [LICENSE](LICENSE) and [GPL-2.0.txt](GPL-2.0.txt).

---

## What it is

Voorhees opens a structured text file as a **tree** on the left, with an **edit panel** on the right for the selected entry. It is built around one promise: **your file stays your file.** Loading and saving a file without editing it writes back exactly the same bytes -- every space, comment, line ending, encoding and byte order mark kept -- and an edit changes only the text it touches. Undo all the way back and the file is byte-for-byte the original again.

It is a single, self-contained `Voorhees.exe` (WinForms, C# 5) with no installer and no dependencies beyond the .NET Framework 4.x that ships with Windows.

## File formats

| Format | Opened by name | What it shows |
|---|---|---|
| **JSON** (and JSONC: `//` and `/* */` comments) | `.json`, `.jsonc` | Objects and arrays as branches, values with their types (string, number, boolean, null). Strict: a file that is not valid JSON is refused with the line and column. |
| **INI** | `.ini`, `.inf` | Sections at the top level with their keys; keys before the first section; `;` / `#` comment lines. Keys and section names compare without case, as Windows does. |
| **Klipper** printer configs | `.cfg` | Sections, options (`:` or `=`), inline comments, multi-line G-code macros, `[include ...]` lines (right-click > Open Included File) and the `SAVE_CONFIG` block Klipper writes at the end. |
| **Moonraker** configs | `moonraker.conf`, other `.conf` | As Klipper, with Moonraker's rules (inline comments after spaces, `\#` / `\;` escapes). |
| **KlipperScreen** configs | `KlipperScreen.conf` | As Klipper, with KlipperScreen's rules and its `#~#` auto-saved block. |
| **Registry files** | `.reg` | Keys as a flat list of full paths (`[HKEY_...]`, `[-HKEY_...]` to delete), typed values: strings, expandable strings, multi-strings, DWORD and QWORD (shown in hex and decimal), binary. Saved in regedit's own forms, long hex values wrapped as regedit wraps them. New .reg files are UTF-16 with a byte order mark, as regedit writes them. Voorhees never imports a .reg into the registry. |

A file with no telling name is recognised by its contents. **File > Reopen As** reads the current file again as another format, and the Open dialog can force a format.

## Requirements

- **Windows** 7 SP1 or later (Windows 10 / 11 recommended).
- **.NET Framework 4.x** (4.5 or later) -- part of every current Windows. Nothing else.

## Building from source

Everything needed is in this folder:

```
compile.bat           the build script
src\                  the C# source (every file is listed in compile.bat)
src\lib\              the progress dialog
res\Voorhees.ico      the program icon (embedded in the exe)
LICENSE, GPL-2.0.txt  the license
README.md             this file
```

To build, run `compile.bat` -- double-click it, or from a command prompt:

```
compile.bat            release build (the default)
compile.bat DEBUG      debug build (also writes Voorhees.pdb)
```

The script uses the C# compiler that comes with the .NET Framework (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, or the 32-bit one); no Visual Studio, MSBuild or NuGet is needed. Every path is relative to the script's own folder, so it works from anywhere, and **`Voorhees.exe` is written into the same folder as `compile.bat`**. The version is fixed in `src\Version.cs` (2.5.0).

## Running

Run `Voorhees.exe`, then open a file from the File menu, by dragging it onto the window, or by naming it on the command line: `Voorhees.exe settings.ini`. There is nothing to install; the program keeps its settings in the registry (below).

## Using the editor

**The tree.** Each entry is labelled `key: value` (`key = value` in INI and .reg files); containers show their name in brackets (`{object}`, `[array]`, `[section]`, `[HKEY_...]`). Comment entries show in grey as `comment: words`, and a comment at the end of an entry's line shows as a grey tail after it. A key that appears more than once in the same place is shown in red with `(duplicate)` -- Voorhees keeps such files as they are, warns when it opens one, and never creates a new duplicate itself.

**The edit panel** shows the selected entry: **Key**, **Type**, **Value** (a checkbox for booleans), **Comment** (the comment on the entry's line) and **Raw** -- the entry's text exactly as it is in the file, which you can edit directly for anything the other fields cannot express. Press **Save** (or Discard) to apply panel changes.

**In-place editing:** double-click an entry or press **F2** to edit its value right in the tree.

**Right-click** an entry for: Add Child (each type, or a comment), Add Top-Level, Rename Key, Copy, Paste, Delete -- and Open Included File on a Klipper `[include]`. Adding or renaming a registry key asks for the hive from a drop-down and the path under it. **Shift+F10** opens the menu from the keyboard.

**Menus:**
- **File** -- New (Ctrl+N), New As (a document of any format), Open (Ctrl+O), Reopen As, Close (Ctrl+W), Save (Ctrl+S), Save As (Ctrl+Shift+S), Exit.
- **Edit** -- Undo (Ctrl+Z) and Redo (Ctrl+Y), back to the last save.
- **Search** -- Find (Ctrl+F) and Find + Replace (Ctrl+H) across keys, values and comments; Replace All is a single undo step.
- **View** -- Font Size (Ctrl++, Ctrl+-, Ctrl+0), Show Raw Text in Tree, **Color Keys** (names coloured by kind and values by type), **Colors...** (choose those colours; each row has a preview).
- **Tools** -- Register Explorer Context Menu, Set Voorhees as Default..., Unregister Explorer Context Menu.
- **Help** -- About Voorhees.

**Comments** are kept wherever they are. Adding the first comment to a plain JSON file warns once that it is now JSONC (JSON with comments); editing inside a Klipper `SAVE_CONFIG` block warns once that Klipper rewrites that block.

Anything that takes more than a moment (opening a very large file, Replace All) shows a progress window.

## How to...

**Open a file.** File > Open (Ctrl+O), drag it onto the window, or right-click it in Explorer > Edit with Voorhees (once registered). The status bar shows the format it was read as. If that is wrong -- a Klipper file opened as Moonraker, say -- use File > Reopen As and pick the right one.

**Change a value.** Select the entry, then either double-click it (or press F2) and type the new value in place, Enter to keep it and Escape to cancel; or type in the panel's Value box and press Save. A value keeps its type: a JSON number stays a number, a registry DWORD accepts `0x1f` or `31`. If the text is not valid for the type, Voorhees says why and changes nothing.

**Change a type.** Pick another entry in the Type box (for example String to Number in JSON, or Text to Key Only in INI), adjust the value, and press Save.

**Rename a key.** Right-click > Rename Key, or edit the panel's Key box and press Save. A name the container already has is refused, so renaming never creates a duplicate.

**Add something.** Right-click the container (an object, a section, a registry key) > Add Child, and choose the kind; the new entry gets a free placeholder name (`newKey`, `new_option`, `NewValue` ...) with the Key box ready to type over. Right-click any top-level entry, or empty space below the tree, for Add Top-Level (a new INI or Klipper section, a registry key, a Klipper include). New registry keys ask for the hive and the path first; new includes ask for the file.

**Add or edit a comment.** For a comment on an entry's own line, type in the panel's Comment box and press Save (clear it to remove the comment). For a comment on a line of its own, right-click > Add Child > Comment... (or Add Top-Level > Comment...). Comment entries are edited like values: double-click, or the Value box.

**Edit the raw text.** The panel's Raw box shows the selected entry exactly as it is in the file -- key, spacing, value, comments. Edit it and press Save to replace the entry with your text, for anything the other fields cannot express (an unusual layout, a comment in an odd place). It must still be one valid entry of the same shape; if not, Voorhees shows the line and column of the problem. View > Show Raw Text in Tree shows every entry's raw first line in the tree itself.

**Move or copy an entry.** Right-click > Copy, then right-click the destination container > Paste; a pasted key that is already taken there gets a free variant. Single values and comments can also be pasted between documents of different formats (a JSON string becomes INI text, and so on); whole objects and sections cannot.

**Find and replace.** Search > Find (Ctrl+F) looks through keys, values and comments and selects each match in turn. Search > Find + Replace (Ctrl+H) replaces one match at a time or all of them; Replace All is one step, so a single Undo takes it all back.

**Undo.** Edit > Undo (Ctrl+Z) and Redo (Ctrl+Y), as far back as the last save. Undoing every edit returns the document to exactly the file as it was opened. While typing in a panel box, Ctrl+Z undoes the typing instead.

**Save.** File > Save (Ctrl+S) writes the file in its own format, encoding and line endings. File > Save As keeps the format; a file name whose extension belongs to another format asks first (converting between formats is not offered).

**Check a file without opening the editor.** `Voorhees --validate settings.ini` reports whether the file is valid in its format, with the line and column of the first problem, and warns about repeated keys; the exit code says the result (see below).

**Colour the tree.** View > Color Keys turns the colours on; View > Colors... chooses them: one for each kind of name (top-level sections and keys, nested containers, plain entries) and one for each type of value (text, numbers, true / false, no value, binary). Each row shows a sample; Reset restores the defaults (dark blue names, black values).

## Command line

```
Voorhees [--debug] [--format NAME] [file]
                                         open the editor, optionally with a file
Voorhees --validate file [--format NAME] [--verbose]
                                         check that a file is valid in its format
Voorhees --register [--verbose]          add "Edit with Voorhees" and Open With
                                         for .json .ini .cfg .conf .reg
Voorhees --set-default EXT[,EXT...] [--verbose]
                                         make Voorhees the default for them
Voorhees --unset-default EXT[,EXT...] [--verbose]
                                         put their previous defaults back
Voorhees --unregister [--verbose]        remove it all again
Voorhees --version                       print the version
Voorhees --help                          print this help

Options:
  --debug    write load, save and tree timings to
             log\Voorhees_debug.log beside Voorhees.exe
  --verbose  report success as well as failure
  --format   read the file as json, ini, klipper, moonraker,
             klipperscreen or reg; without it the format comes
             from the file's name, else its contents

Exit codes:
  0  success
  1  --validate: the file is not valid in its format
  2  the command line could not be understood
  3  the file could not be read
  4  the registry could not be changed
```

## Explorer integration

**Tools > Register Explorer Context Menu** (or `--register`) adds **Edit with Voorhees** to the right-click menu of `.json`, `.ini`, `.cfg`, `.conf` and `.reg` files, and Voorhees to their Open With lists. It changes no default program.

**Tools > Set Voorhees as Default...** (or `--set-default`) chooses, per file type, whether double-clicking opens it in Voorhees; the previous default is remembered and put back when the box is cleared, by `--unset-default`, or by Unregister. (Making Voorhees the default for `.reg` means double-clicking a registry file opens it for editing instead of merging it.) On Windows 10 and 11 a default chosen in Settings > Default apps still wins.

All of this is written under `HKEY_CURRENT_USER\Software\Classes` only -- no administrator rights are needed. **Unregister** removes it all.

## Settings

Voorhees remembers, under `HKEY_CURRENT_USER\Software\Voorhees`: the window's position and size, the splitter position, the font size, the Show Raw Text and Color Keys options, and the colours chosen in View > Colors. Nothing else is stored anywhere.

## Source overview

| File | Contents |
|---|---|
| `src\Voorhees.cs` | The program entry point (command line first), the main window and its dialogs, Explorer integration and settings. |
| `src\VoorheesDocument.cs` | The document model: entries with all their surrounding text, changes, undo and redo, search and replace, every editing action. |
| `src\VoorheesFormat.cs` | The format dispatcher: every format-dependent question, answered by the format's module. |
| `src\VoorheesText.cs` | File bytes to text and back: encodings, byte order marks, line endings. |
| `src\VoorheesJson.cs` | The lossless JSON reader and writer. |
| `src\VoorheesLines.cs` | The line engine shared by INI, the Klipper family and .reg files. |
| `src\VoorheesIni.cs`, `src\VoorheesCfg.cs`, `src\VoorheesReg.cs` | The INI, Klipper-family and registry-file rules. |
| `src\VoorheesTreeView.cs` | The tree control: built lazily, updated in place, labels drawn in colour runs. |
| `src\Version.cs` | The version string. |
| `src\lib\BusyModalForm.cs` | The progress window. |

The main window's class is `partial`, with a few partial-method hooks that let a separate test harness drive the editor in-process; the harness is not part of this release, and in a normal build the hooks compile to nothing.

## License

Voorhees is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 2 of the License or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](LICENSE) and [GPL-2.0.txt](GPL-2.0.txt).

Copyright (c) 2026 B. C. Services.
