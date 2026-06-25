# ASTWrapper — Quick Start Guide

## Requirements

- .NET 10 runtime (or SDK to build)
- For Rocq (.v) files: Coq serapi installed (optional, heuristic mode works without it)
- For Lean (.lean) files: no extra runtime required
- For Agda (.agda) files: `agda` executable must be available on PATH or via `AGDA_EXECUTABLE` env var

---

## Quick start — run the executable

1. Unzip or put the published folder on the target machine.
2. Open a terminal and run:

```powershell
Wrapper.exe "C:\path\to\repository"
```

3. Wait until processing finishes.
4. Check `output.json` in the repository root.

---

## Quick start — from source (build)

1. Open PowerShell in the repository root.
2. Build:

```powershell
dotnet build Wrapper\FormalVerificationWrapper.csproj -c Release
```

3. Run:

```powershell
dotnet run --project Wrapper -- "C:\path\to\repository"
```

---

## Quick start — Linux (Ubuntu)

**1. Install the .NET 10 SDK**
```bash
sudo apt update && sudo apt install -y dotnet-sdk-10.0
```

**2. Clone the repository**
```bash
git clone https://github.com/Nicolletti/ASTWrapper.git
cd ASTWrapper
```

**3. Build a self-contained executable**
```bash
dotnet publish Wrapper/FormalVerificationWrapper.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./output
```

**4. Run**
```bash
./output/Wrapper /path/to/repository
```

**5. Check results**
```bash
cat /path/to/repository/output.json
```

**Optional — Agda support**
```bash
# Install Haskell and Agda
sudo apt install -y haskell-platform
cabal install Agda

# Point the wrapper to your agda binary
export AGDA_EXECUTABLE=/path/to/agda
export AGDA_INCLUDE_PATH=/path/to/repo:/other/includes

./output/Wrapper /path/to/repository --include-path="/path/to/repo"
```

**Pretty-print output.json on Linux**
```bash
cat /path/to/repository/output.json | python3 -m json.tool > output_pretty.json
```

---

## Output format

Each entry in `output.json` contains:

- `success`: boolean
- `fileName`: full path to the file
- `language`: `Rocq`, `Lean` or `Agda`
- `ast`: unified AST node tree
- `error`: error string if failed
- `processingTime`: milliseconds
- `rawAstJson`: raw Agda AST (when available)

---

## Troubleshooting

- **Rocq/SerAPI on Linux:** install `opam` and run `opam install coq-serapi`, then ensure `sertop` is on PATH.
- **Agda not found:** set `AGDA_EXECUTABLE` to the full path of your `agda` binary.
- **Wrong architecture:** replace `linux-x64` with `linux-arm64` if on ARM (e.g. Raspberry Pi).
