# Installer checksum (Phase 4, Plan 03; REL-01).
#
# Writes <name>.sha256 next to every *.exe in a folder, in the format `sha256sum -c` accepts: one line,
# "<lowercase sha256 hex> *<file name>" (binary-mode asterisk), ASCII without BOM and without a trailing newline.
# The hash comes from Get-FileHash when the cmdlet resolves (pwsh 7, most Windows PowerShell 5.1 installs) and
# from `certutil -hashfile <file> SHA256` otherwise, so both shells produce byte-identical files. Prints one
# "CHECKSUM OK file=<name> sha256=<hash>" line per exe (exit 0) or "CHECKSUM FAIL: <reason>" (exit 1) when the
# folder is missing, holds no exe, or any hash / write step throws. The same command runs locally and in the
# release workflow, followed there by `sha256sum -c` from Git Bash as the independent check.
#
#   -Dir   folder to scan, relative to the repository root or absolute (default dist).
#
#   pwsh -NoProfile -File tools/checksum.ps1 -Dir dist
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/checksum.ps1 -Dir dist

param(
    [string]$Dir = 'dist'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Fail([string]$reason) {
    Write-Host "CHECKSUM FAIL: $reason"
    exit 1
}

# ---- 1. inputs -------------------------------------------------------------------------------------------

if ([System.IO.Path]::IsPathRooted($Dir)) {
    $dirAbs = [System.IO.Path]::GetFullPath($Dir)
} else {
    $dirAbs = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Dir))
}
if (-not (Test-Path -LiteralPath $dirAbs -PathType Container)) { Fail "folder not found: $dirAbs" }

$exes = @(Get-ChildItem -LiteralPath $dirAbs -Filter *.exe -File)
if ($exes.Count -eq 0) { Fail "no .exe file in $dirAbs" }

# ---- 2. hash ---------------------------------------------------------------------------------------------

$useGetFileHash = $null -ne (Get-Command Get-FileHash -ErrorAction SilentlyContinue)

function Get-Sha256Hex([string]$path) {
    if ($useGetFileHash) {
        $hex = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    } else {
        # certutil prints: "SHA256 hash of <file>:", then the hex (spaced on older builds), then "CertUtil: ... completed successfully."
        $lines = @(& certutil -hashfile $path SHA256 2>&1 | ForEach-Object { [string]$_ })
        if ($LASTEXITCODE -ne 0 -or $lines.Count -lt 2) { throw "certutil failed for $path (exit $LASTEXITCODE)" }
        $hex = ($lines[1] -replace '\s', '')
    }
    $hex = $hex.ToLowerInvariant()
    if ($hex -notmatch '^[0-9a-f]{64}$') { throw "unexpected SHA-256 text '$hex' for $path" }
    return $hex
}

# ---- 3. write --------------------------------------------------------------------------------------------

foreach ($exe in $exes) {
    try {
        $hash = Get-Sha256Hex $exe.FullName
        $line = "$hash *$($exe.Name)"
        $target = "$($exe.FullName).sha256"
        [System.IO.File]::WriteAllText($target, $line, [System.Text.Encoding]::ASCII)
        $written = [System.IO.File]::ReadAllText($target, [System.Text.Encoding]::ASCII)
        if ($written -cne $line) { throw "read-back mismatch for $target" }
    } catch {
        Fail "$($exe.Name): $($_.Exception.Message)"
    }
    Write-Host "CHECKSUM OK file=$($exe.Name) sha256=$hash"
}

exit 0
