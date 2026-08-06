# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

GtkSharp is a C# wrapper for Gtk 3.22+ and its companion libraries (glib, gio, cairo, pango, atk, gdk, gtksourceview, webkit2gtk). It is a hard fork of mono/gtk-sharp, targeting `net8.0` + `netstandard2.0` and requiring **no glue libraries** — all native calls go through runtime symbol lookup rather than `DllImport` of a fixed library name.

Most of the C# in `Source/Libs/*` is **generated at build time from XML API descriptions and is not checked in** (`Generated/` is gitignored). Understanding the codegen pipeline is a prerequisite for changing almost anything.

## Build

Cake drives everything; `dotnet build` on the solution alone will fail because the generated sources won't exist yet.

```sh
dotnet tool restore                 # installs cake.tool 2.0.0 (see .config/dotnet-tools.json)
dotnet cake build.cake              # Default: Build + PackageNuGet + PackageWorkload + PackageTemplates
```

Useful arguments (Cake style, `--Name=Value`):

```sh
dotnet cake build.cake --BuildTarget=Build           # build only, skip packaging
dotnet cake build.cake --BuildTarget=Prepare         # build tools + regenerate code only
dotnet cake build.cake --BuildTarget=RunSamples      # build and launch the Samples app
dotnet cake build.cake --BuildTarget=FullClean       # also removes BuildOutput/
dotnet cake build.cake --Assembly=GtkSharp           # restrict Clean/Prepare/Build/Pack to one assembly
dotnet cake build.cake --Configuration=Debug
dotnet cake build.cake --BuildVersion=3.24.24.1
```

Workload targets (`InstallWorkload` / `UninstallWorkload`) install the `gtk` workload into the local .NET SDK directory — they mutate the machine's SDK install, so don't run them casually.

All build output lands in `BuildOutput/` (`BuildOutput/Tools`, `BuildOutput/$(Configuration)`, `BuildOutput/NugetPackages`).

## Tests

There is no test project or test runner in this repository. Verification is manual: `--BuildTarget=RunSamples` runs `Source/Samples`, a gallery app exercising the widget bindings (`Source/Samples/Sections/*`). CI (`.github/workflows/main.yml`, ubuntu-22.04, .NET 8) only runs `dotnet cake build.cake` and pushes the resulting packages.

## Code generation pipeline

Per-assembly, `CakeScripts/GAssembly.cake` `Prepare()` does:

1. Copy `Source/Libs/<Name>/<Name>-api.xml` → `Source/Libs/<Name>/Generated/<Name>-api.xml`.
2. If `<Name>.metadata` exists, run `BuildOutput/Tools/GapiFixup.dll` to apply the metadata (and optional `<Name>-symbols.xml`) as XPath-driven edits **to the copy in `Generated/`**.
3. Run `BuildOutput/Tools/GapiCodegen.dll --outdir=Generated --schema=Source/Libs/Shared/Gapi.xsd --assembly-name=<Name> --generate=<fixed-up api.xml>`, plus `--include=` for each dependency's fixed-up api.xml so cross-assembly types resolve.

The `.csproj` files rely on the SDK's implicit glob, so `Generated/*.cs` is compiled alongside the hand-written files in the same directory.

Consequences to internalize:

- **Never edit files under `Generated/`.** They are deleted by `Clean` and rewritten by `Prepare`.
- To change the shape of a binding (rename, hide, change a parameter's direction/type, mark deprecated, make a boxed type non-opaque), edit `Source/Libs/<Name>/<Name>.metadata` — XPath `attr` / `remove-attr` / `remove-node` rules against the api.xml tree.
- To add behaviour that codegen can't express, add a hand-written **`partial class` in `Source/Libs/<Name>/`** (e.g. `Source/Libs/GtkSharp/Button.cs`, `Widget.cs`). This is the dominant customization mechanism.
- To change how *all* bindings of a given kind are emitted, edit the generator in `Source/Tools/GapiCodegen/` (`Method.cs`, `Parameter.cs`, `SymbolTable.cs`, `ObjectGen.cs`, `StructBase.cs`, …). `SymbolTable.cs` is the C-type → C#-type mapping; `LPGen`/`LPUGen` handle native-sized ints.
- `GLibSharp` and `CairoSharp` have no `.metadata` file, so nothing is generated for them — they are entirely hand-written.

`Source/Libs/Shared/Gapi.xsd` is the schema the api.xml files are validated against.

## Assembly graph

`CakeScripts/Settings.cake` is the authoritative list of wrapper assemblies and their dependency order — build order, `--include=` flags for codegen, and per-assembly codegen flags (`--abi-cs-usings=…`) all come from it:

`GLibSharp` → `GioSharp`, `AtkSharp`, `PangoSharp` (also on `CairoSharp`) → `GdkSharp` → `GtkSharp` → `GtkSourceSharp`, `WebkitGtkSharp`. `CairoSharp` is standalone.

Adding a new wrapper assembly means touching all of: `Settings.cake`, `Source/GtkSharp.sln`, a new `Source/Libs/<Name>/` with `<Name>.csproj` + `<Name>-api.xml` (+ `.metadata`), and — if it binds a new native library — the `Library` enum and `_libraryDefinitions` table in `Source/Libs/Shared/`.

## Native interop

`Source/Libs/Shared/{Library.cs,GLibrary.cs,FuncLoader.cs}` are linked into every wrapper `.csproj` and are the only place platform-specific loading lives. `GLibrary` maps each `Library` enum value to a list of candidate filenames across Windows/Linux/macOS; `FuncLoader` wraps `LoadLibrary`/`dlopen` + `GetProcAddress`/`dlsym`.

Generated and hand-written code both follow the same pattern — a `[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate d_<c_name>` plus a static field initialized via `FuncLoader.LoadFunction<…>(FuncLoader.GetProcAddress(GLibrary.Load(Library.X), "<c_name>"))`. Do not introduce plain `[DllImport("libgtk-3-0.dll")]` calls; they break the cross-platform, glue-free design.

Supporting a new native library requires adding the enum value plus its per-platform filename list in `GLibrary`'s static constructor.

## Project layout beyond the wrappers

- `Source/Tools/` — `GapiFixup` and `GapiCodegen` (built first by the `Prepare` task into `BuildOutput/Tools`).
- `Source/Workload/` — the .NET `gtk` workload: `GtkSharp.Ref`, `GtkSharp.Runtime`, `GtkSharp.Sdk`, and `GtkSharp.NET.Sdk.Gtk` (the workload manifest, packed once per SDK feature band listed in `supportedVersionBands` in `build.cake`), plus workload-flavoured C#/F#/VB templates.
- `Source/Templates/` — the standalone `dotnet new gtkapp` template packages (C#/F#/VB), independent of the workload.
- `Source/Addins/MonoDevelop.GtkSharp.Addin` — restored by `Prepare` but not part of the main build/pack targets.
- `Source/OldStuff/` — legacy mono/gtk-sharp material (docs, old parser, gtkdotnet). Not built; treat as reference only.

## Build conventions

`Source/Libs/Directory.Build.props` applies to every wrapper: `net8.0;netstandard2.0`, `LangVersion 9`, `AllowUnsafeBlocks`, output redirected to `BuildOutput/$(Configuration)`, and **strong-name signing with `Source/Libs/GtkSharp.snk`** (required by `Microsoft.DotNet.SharedFramework.Sdk` for the workload ref pack). Keep new code within C# 9 and both target frameworks.

`Source/Libs/GtkSharp/GtkSharp.targets` ships in the GtkSharp NuGet package and, on Windows, downloads and unzips a Gtk 3.24.24 runtime into `%LOCALAPPDATA%\Gtk\3.24.24` before build unless `SkipGtkInstall=True`. This runs for consumers of the package, including the Samples project.
