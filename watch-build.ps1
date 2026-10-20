# Rebuilds the mod by itself whenever its source changes, so you never have to
# run "dotnet build". Leave this window open while working on the mod.
# The result of every build goes to build-status.txt.
$proj   = Split-Path -Parent $MyInvocation.MyCommand.Path
$status = Join-Path $proj "build-status.txt"
$last   = Get-Date 0
$waiting = $false
Write-Host "Watching $proj for changes. Close this window to stop."
while ($true) {
    $newest = (Get-ChildItem (Join-Path $proj "src\*.cs"), (Join-Path $proj "*.csproj") |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
    if ($newest -gt $last) {
        if (Get-Process DaveTheDiver -ErrorAction SilentlyContinue) {
            if (-not $waiting) {
                Write-Host "$(Get-Date -Format T)  change seen - waiting for the game to close before building"
                Set-Content $status "$(Get-Date -Format s) WAITING: close the game so the new build can be installed"
                $waiting = $true
            }
        } else {
            $waiting = $false
            Start-Sleep -Seconds 2   # let a batch of edits finish
            $newest = (Get-ChildItem (Join-Path $proj "src\*.cs"), (Join-Path $proj "*.csproj") |
                       Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
            Write-Host "$(Get-Date -Format T)  building..."
            $out = & dotnet build $proj -c Release 2>&1 | Out-String
            $ok  = ($LASTEXITCODE -eq 0)
            $word = if ($ok) { "BUILD OK" } else { "BUILD FAILED" }
            Set-Content $status ("{0} {1}`r`n{2}" -f (Get-Date -Format s), $word, $out)
            Write-Host "$(Get-Date -Format T)  $word"
            $last = $newest
        }
    }
    Start-Sleep -Seconds 3
}
