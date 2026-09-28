# Copies unity-src into the Unity project, rebuilds the demo scene and the Windows player
# (retrying when OneDrive blocks one of Unity's tools), and optionally runs the traffic test.
#   powershell -File tools\build.ps1 [-SkipScene] [-SimTest] [-Capture] [-Open]
# -Capture records a 75 s drive with screenshots into Build\captures; -Open launches the demo at the end.
param([switch]$SkipScene, [switch]$SimTest, [switch]$Capture, [switch]$Open)

Get-Process RobotaxiSim -ErrorAction SilentlyContinue | Stop-Process -Force   # the running demo locks the .exe

$root = "C:\Users\ASUS\SelfDrivingSim"
$proj = "$root\UnityProject"
$unity = "C:\Users\ASUS\OneDrive\Desktop\6000.6.2f1\Editor\Unity.exe"

Copy-Item "$root\unity-src\Scripts\*.cs" "$proj\Assets\Scripts\" -Force
Copy-Item "$root\unity-src\Editor\*.cs" "$proj\Assets\Editor\" -Force

function Run-Unity($method, $log, [switch]$quit) {
    $args = @('-batchmode', '-projectPath', "`"$proj`"", '-executeMethod', $method, '-logFile', "`"$log`"")
    if ($quit) { $args = @('-quit') + $args }
    $p = Start-Process $unity -ArgumentList $args -PassThru
    if (-not $p.WaitForExit(1500000)) {
        Get-Process Unity, bee_backend -ErrorAction SilentlyContinue | Stop-Process -Force
        return $false
    }
    return $p.ExitCode -eq 0
}

if (-not $SkipScene) {
    $ok = Run-Unity 'SceneBuilder.BuildBatch' "$root\unity-build.log" -quit
    $errors = Select-String -Path "$root\unity-build.log" -Pattern "error CS" | ForEach-Object { $_.Line } | Select-Object -Unique
    if ($errors) { "SCENE: compile errors"; $errors; exit 1 }
    Select-String -Path "$root\unity-build.log" -Pattern "SceneBuilder: |came out|no car named" | ForEach-Object { "SCENE: " + $_.Line }
}

$built = $false
for ($i = 1; $i -le 3 -and -not $built; $i++) {
    $null = Run-Unity 'BuildTools.BuildWindows' "$root\unity-player-build.log"
    $built = [bool](Select-String -Path "$root\unity-player-build.log" -Pattern "BuildTools: Succeeded" -Quiet)
    if (-not $built) {
        $errors = Select-String -Path "$root\unity-player-build.log" -Pattern "error CS" | ForEach-Object { $_.Line } | Select-Object -Unique
        if ($errors) { "PLAYER: compile errors"; $errors; exit 1 }
        "PLAYER: attempt $i failed, retrying"
    }
}
if (-not $built) { "PLAYER: build failed"; exit 1 }
"PLAYER: built"

if ($SimTest) {
    $out = "$root\Build\simtest"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    $g = Start-Process "$root\Build\RobotaxiSim.exe" -ArgumentList @('-batchmode', '-nographics', '-simtest', "`"$out`"", '-logFile', "`"$root\Build\simtest.log`"") -PassThru
    if (-not $g.WaitForExit(600000)) { Stop-Process -Id $g.Id -Force; "SIMTEST: timed out" }
    if (Test-Path "$out\simtest.txt") { Get-Content "$out\simtest.txt" } else { "SIMTEST: no report"; Select-String -Path "$root\Build\simtest.log" -Pattern "Exception|Crash" | Select-Object -First 5 | ForEach-Object { $_.Line } }
}

if ($Capture) {
    $cap = "$root\Build\captures"
    Remove-Item $cap -Recurse -Force -ErrorAction SilentlyContinue
    $g = Start-Process "$root\Build\RobotaxiSim.exe" -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1600', '-screen-height', '900', '-autocapture', "`"$cap`"", '-logFile', "`"$root\Build\autocapture.log`"") -PassThru
    if (-not $g.WaitForExit(200000)) { Stop-Process -Id $g.Id -Force; "CAPTURE: timed out" }
    "CAPTURE: " + ((Get-ChildItem $cap -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ", ")
}

if ($Open) {
    Start-Process "$root\Build\RobotaxiSim.exe" -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1600', '-screen-height', '900')
    "OPENED: RobotaxiSim.exe"
}
