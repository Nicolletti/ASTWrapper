# ASTWrapper

# Formal Verification AST Wrapper

Simple wrapper to parse Lean (.lean) and Coq/Rocq (.v) files and produce a unified JSON AST output.
Simple wrapper to parse Lean (.lean), Coq/Rocq (.v) and Agda (.agda) files and produce a unified JSON AST output.

## Overview

This program scans a repository folder, finds `.lean` and `.v` files, runs the appropriate parser for each file, and writes a single `output.json` with results. Each result contains a processing status, the file path, detected language, a unified AST node tree, an error (if any), and the processing time in milliseconds.
This program scans a repository folder, finds `.lean`, `.v` and `.agda` files, runs the appropriate parser for each file, and writes a single `output.json` with results. Each result contains a processing status, the file path, detected language, a unified AST node tree, optional raw AST JSON (for Agda), an error (if any), and the processing time in milliseconds.

Two ways to use this project are provided in the repository:
- the compiled executable (in `publish/` or `release` folder)
- the source solution (Visual Studio / dotnet project)

## Requirements

- .NET 10 runtime (or SDK to build)
- For Rocq (.v) files: WSL with Coq serapi installed if you want full-serapi mode (the project includes a fast local heuristic mode by default)
- For Lean (.lean) files: no extra runtime required (parsing uses local code in `LeanAST.cs`)
 - For Rocq (.v) files: WSL with Coq serapi installed if you want full-serapi mode (the project includes a fast local heuristic mode by default).
 - For Lean (.lean) files: no extra runtime required (parsing uses local code in `LeanAST.cs`).
 - For Agda (.agda) files: an `agda` executable must be available. You can point the wrapper to a custom wrapper or executable using the AGDA_EXECUTABLE environment variable. The project includes a small helper `AgdaAstWrapper.cs` which can be compiled and used as the AGDA executable if desired.

Note: The executable may still rely on external tools if you enable full SerAPI parsing. Include instructions when sharing the executable.

## Files of interest

- `Program.cs` — entry point. Accepts a repository path and runs the orchestrator.
- `Orchestrator.cs` — finds files, runs parsers in parallel, writes `output.json` incrementally.
- `ParserExecutor.cs` — chooses the parser for each file and converts parser output to `UnifiedASTNode`.
 - `ParserExecutor.cs` — chooses the parser for each file and converts parser output to `UnifiedASTNode` (invokes `agda --print-ast-json` for .agda files and captures the JSON AST).
- `RocqAST.cs` — Coq/Rocq parser logic. By default uses a fast local heuristic parser. Can be adapted to call SerAPI.
- `LeanAST.cs` — Lean parser and utilities.
- `ProcessingResult.cs` — data models (UnifiedASTNode, ProcessingResult, NodeType).
- `FormalVerificationWrapper.csproj` — the project file.

## Quick start — run the executable

1. Unzip or put the published folder on target machine (the folder contains the single exe or multiple files depending on publish options).
2. Open a terminal and run:

```powershell
Wrapper.exe "C:\path\to\repository"
```

3. Wait until processing finishes. The program prints simple progress messages.
4. Check `output.json` in the repository root. It is a pretty indented JSON array of results.

Example result item (simplified):

```json
{
  "success": true,
  "fileName": "C:\\repo\\foo.v",
  "language": "Rocq",
  "ast": { "nodeType": "File", "name": "foo.v", "children": [...] },
  "error": null,
  "processingTime": 12
}
```

## Quick start — from source (build)

1. Open PowerShell in repository root.
2. Build the project:

```powershell
dotnet build Wrapper\FormalVerificationWrapper.csproj -c Release
```

3. Run the project (folder path as argument):

```powershell
dotnet run --project Wrapper -- "C:\path\to\repository"
```

4. Inspect `output.json` in the repository root.

Notes for Agda
- If `agda` is not on PATH, set AGDA_EXECUTABLE to the full path of your `agda` binary or to a helper executable that runs `agda --print-ast-json` and writes the AST to stdout.
- You can pass include paths to the wrapper using `--include-path` (repeatable) or by setting the AGDA_INCLUDE_PATH environment variable. Example:

```powershell
$env:AGDA_EXECUTABLE = 'C:\path\to\agda.exe'
$env:AGDA_INCLUDE_PATH = 'C:\path\to\repo;C:\other\includes'
Wrapper.exe "C:\path\to\repository" --include-path="C:\path\to\repo"
```

 - The wrapper captures Agda's `--print-ast-json` output and stores it in `ProcessingResult.RawAstJson`. When available a pretty-printed `.ast.pretty.json` file is also written next to the source file.

## Output format

The program writes `output.json` as an array of `ProcessingResult` objects. Each object includes:

- `success`: boolean
- `fileName`: full path to the file
- `language`: `Rocq` or `Lean`
- `ast`: `UnifiedASTNode` with fields:
  - `nodeType`: one of Definition/Theorem/Lemma/Fixpoint/Inductive/Other
  - `name`: extracted name or command snippet
  - `language`: same as above
  - `children`: array of nested UnifiedASTNode objects
  - `metadata`: raw parser data
- `error`: error string if failed
- `processingTime`: milliseconds to process file
 - `rawAstJson`: when available (Agda) the raw AST JSON string produced by the tool

The JSON is written incrementally and pretty-printed.

## Performance notes

- The orchestrator limits parallel work to avoid overload. You can tune `MaxDegreeOfParallelism` in `Orchestrator.cs`.
- For speed, Rocq parsing currently uses a fast local heuristic. If you need full, precise ASTs you can enable SerAPI calls (requires WSL + serapi) but it is slower.
- Consider running on a machine with enough CPU and memory for large repos.
 - The orchestrator limits parallel work to avoid overload. Runtime overrides are available via environment variables:
  - WRAPPER_MAX_PARALLELISM — cap concurrency (integer)
  - WRAPPER_PARSER_TIMEOUT_SECONDS — parser timeout in seconds (integer, default higher than before)
 - For speed, Rocq parsing currently uses a fast local heuristic. If you need full, precise ASTs you can enable SerAPI calls (requires WSL + serapi) but it is slower.
 - Agda parsing invokes an external process; use fewer concurrent Agda processes (lower WRAPPER_MAX_PARALLELISM) if the machine becomes IO/CPU bound.

## Troubleshooting

- If `output.json` is not pretty: update to the latest code and rebuild; or pretty-print with PowerShell:

```powershell
(Get-Content .\output.json -Raw) | ConvertFrom-Json | ConvertTo-Json -Depth 200 | Set-Content .\output_pretty.json
```

- If Rocq parsing needs SerAPI: install WSL and Coq/serapi and ensure `wsl.exe` can run `opam exec -- sertop`.

- If the executable is blocked by AV: code-sign the binary or share via a zip file.


