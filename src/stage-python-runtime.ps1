<#
Stages one architecture of the embeddable CPython package into the shared
build cache (ArchDir), enables site-packages, bootstraps pip, and installs
the declared package list - all inside a named, cross-process mutex so two
projects building in parallel (e.g. HexManiac.WPF and HexManiac.Tests under
`dotnet build /m`, which both stage into the same cache dir) can't race on
the same files. Only writes ArchDir\.complete after every requested package
actually imports, so a transient failure (e.g. no network) leaves the cache
retry-able on the next build instead of permanently "complete" with a
broken/partial runtime.
#>
param(
   [Parameter(Mandatory=$true)] [string]$ArchDir,
   [Parameter(Mandatory=$true)] [string]$EmbedZipUrl,
   [Parameter(Mandatory=$true)] [string]$MajorMinor,
   [Parameter(Mandatory=$true)] [string]$MutexName,
   # comma-separated, not string[]: powershell.exe -File doesn't bind multiple bare
   # tokens into an array parameter the way calling a function would - each becomes
   # a separate (unbound) positional argument instead, so a single delimited string
   # is the robust way to pass a list across that boundary.
   [string]$PackagesCsv = ''
)
$Packages = @($PackagesCsv -split ',' | Where-Object { $_ -ne '' })

# Deliberately NOT $ErrorActionPreference = 'Stop': under that setting, a native exe
# merely writing to stderr (pip's normal progress chatter included) gets wrapped as a
# terminating PowerShell error regardless of its actual exit code. Native failures are
# instead detected explicitly via $LASTEXITCODE below.
$mutex = New-Object System.Threading.Mutex($false, $MutexName)
if (-not $mutex.WaitOne([TimeSpan]::FromMinutes(15))) {
   Write-Warning "Timed out waiting for the python runtime staging lock ($MutexName); skipping this build."
   exit 0
}
try {
   # Top-level catch-all: this script must never fail the editor's build, no matter what
   # goes wrong (missing network, a package with no matching wheel, anything unforeseen).
   # Worst case it warns and leaves .complete unwritten, so the next build retries.
   try {
      $completeMarker = Join-Path $ArchDir '.complete'
      if (Test-Path $completeMarker) { exit 0 } # another (or the previous) build already finished this

      New-Item -ItemType Directory -Force -Path $ArchDir | Out-Null
      $zipPath = Join-Path (Split-Path $ArchDir -Parent) (Split-Path $EmbedZipUrl -Leaf)
      if (-not (Test-Path $zipPath)) {
         Invoke-WebRequest -Uri $EmbedZipUrl -OutFile $zipPath
      }
      Expand-Archive -Path $zipPath -DestinationPath $ArchDir -Force

      $pthPath = Join-Path $ArchDir "python$MajorMinor._pth"
      @("python$MajorMinor.zip", '.', 'Lib\site-packages', 'import site') | Set-Content -Path $pthPath -Encoding ascii
      New-Item -ItemType Directory -Force -Path (Join-Path $ArchDir 'Lib\site-packages') | Out-Null

      $packagesOk = $true
      if ($Packages.Count -gt 0) {
         $packagesOk = $false
         $pythonExe = Join-Path $ArchDir 'python.exe'
         $getPipPath = Join-Path $ArchDir 'get-pip.py'
         Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/get-pip.py' -OutFile $getPipPath
         & $pythonExe $getPipPath --no-warn-script-location
         if ($LASTEXITCODE -ne 0) { throw "get-pip.py exited with code $LASTEXITCODE" }

         & $pythonExe -m pip install --no-warn-script-location @Packages
         if ($LASTEXITCODE -ne 0) { throw "pip install exited with code $LASTEXITCODE" }

         & $pythonExe -c ('import ' + ($Packages -join ', '))
         if ($LASTEXITCODE -ne 0) { throw "import verification failed with code $LASTEXITCODE" }

         $packagesOk = $true
      }

      if ($packagesOk) {
         New-Item -ItemType File -Force -Path $completeMarker | Out-Null
      }
   } catch {
      Write-Warning "Python runtime staging for $ArchDir did not fully complete (will retry on next build): $_"
   }
} finally {
   $mutex.ReleaseMutex()
}
exit 0
