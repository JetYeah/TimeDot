$dotnet = "$env:LOCALAPPDATA\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
Write-Output "=== build+run tests ==="
& $dotnet build d:\ClaudeCode\TimeDot-main\TimeDot.Tests\TimeDot.Tests.csproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Output "BUILD FAIL"; exit 1 }
& $dotnet run --project d:\ClaudeCode\TimeDot-main\TimeDot.Tests\TimeDot.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { Write-Output "TESTS FAIL"; exit 1 }
Write-Output "=== publish ==="
Get-Process TimeDot -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Milliseconds 1000
& $dotnet publish d:\ClaudeCode\TimeDot-main\TimeDot\TimeDot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o d:\ClaudeCode\TimeDot-main\dist --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Output "PUBLISH FAIL"; exit 1 }
Write-Output "=== start app with --todo-uitest ==="
$p = Start-Process 'd:\ClaudeCode\TimeDot-main\dist\TimeDot.exe' -ArgumentList '--todo-uitest' -PassThru
Write-Output ("started pid {0}" -f $p.Id)
Start-Sleep -Seconds 12
if ($p.HasExited) { Write-Output "APP EXITED EARLY - FAIL"; exit 1 }
Write-Output "=== uitest result ==="
$res = Join-Path $env:APPDATA 'TimeDot\uitest_result.txt'
$resultText = [IO.File]::ReadAllText($res, [Text.Encoding]::UTF8)
[Console]::WriteLine($resultText)
if ($resultText -notmatch '== RESULT: PASS') { Write-Output "UITEST FAIL"; exit 1 }
$q = Get-Process TimeDot -ErrorAction SilentlyContinue
if ($q) { Write-Output ("app running pid {0}, responding={1}" -f $q.Id, $q.Responding) }
