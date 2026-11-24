# api_push.ps1 — 通过 GitHub API 把本地最终状态推送到 JetYeah/TimeDot main
# (github.com 的 git 端点在本机网络不可达,api.github.com 可达——用 Git Database API 逐文件推送)
# -MessageFile 指定提交信息文件(UTF-8);省略则用下方内置信息(仅对应 2026-09-08 那一版,勿再用于新推送)
param([string]$MessageFile = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$token = ([IO.File]::ReadAllText('D:\ClaudeCode\TimeDot-main\tools\.gh_token', [Text.Encoding]::UTF8)).Trim()
$h = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' }
$api = 'https://api.github.com/repos/JetYeah/TimeDot'
$root = 'D:\ClaudeCode\TimeDot-main'

# 1. 远端 main 当前状态
$ref = Invoke-RestMethod -Uri "$api/git/ref/heads/main" -Headers $h -TimeoutSec 30
$remoteHead = $ref.object.sha
$rc = Invoke-RestMethod -Uri "$api/git/commits/$remoteHead" -Headers $h -TimeoutSec 30
$baseTree = $rc.tree.sha
Write-Host "远端 main=$remoteHead baseTree=$baseTree"

# 2. 本地文件清单(git 管理的)
$git = "$env:USERPROFILE\MinGit\cmd\git.exe"
$files = (& $git -C $root ls-files) | Where-Object { $_ -ne '' }
Write-Host "本地文件数: $($files.Count)"

# 3. 远端 tree 的文件清单(recursive)——做差集,远端多余的删除
$rt = Invoke-RestMethod -Uri "$api/git/trees/$($baseTree)?recursive=1" -Headers $h -TimeoutSec 30
$remoteFiles = @($rt.tree | Where-Object { $_.type -eq 'blob' } | ForEach-Object { $_.path })
Write-Host "远端文件数: $($remoteFiles.Count)"
$toDelete = @() # 保留远端独有文件(如 LICENSE/NOTICE.md 法律文件),不做删除
Write-Host ("远端有本地无(保留不动): {0}" -f ((($remoteFiles | Where-Object { $files -notcontains $_ }) -join ', ')))

# 4. 逐文件建 blob
$entries = @()
foreach ($f in $files) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $f))
    $b64 = [Convert]::ToBase64String($bytes)
    $body = @{ content = $b64; encoding = 'base64' } | ConvertTo-Json -Compress
    $blob = Invoke-RestMethod -Uri "$api/git/blobs" -Method Post -Headers $h -Body $body -ContentType 'application/json' -TimeoutSec 60
    $entries += @{ path = ($f -replace '\\', '/'); mode = '100644'; type = 'blob'; sha = $blob.sha }
    Write-Host "  blob: $f"
}
foreach ($d in $toDelete) {
    $entries += @{ path = $d; mode = '100644'; type = 'blob'; sha = $null }
    Write-Host "  删除: $d"
}

# 5. 建 tree(基于远端 base_tree,叠加本地全部文件)
$treeBody = @{ base_tree = $baseTree; tree = $entries } | ConvertTo-Json -Depth 5 -Compress
$newTree = Invoke-RestMethod -Uri "$api/git/trees" -Method Post -Headers $h -Body $treeBody -ContentType 'application/json' -TimeoutSec 60
Write-Host "新 tree: $($newTree.sha)"

# 6. 建 commit(parent=远端 HEAD,干净的追加提交)
#
$msg = if ($MessageFile -ne '') { [IO.File]::ReadAllText($MessageFile, [Text.Encoding]::UTF8).TrimStart([char]0xFEFF) } else { @"
TimeDot: Runtime 151 固定 + 数据层修复 + 残影问题终局修复与调试档案

- WebView2 Runtime 固定到 151.0.4129.101(Evergreen 自动更新引入输入回归的规避)
- 数据层: ScheduleSave 竞态修复、Todo/QuickReply 原子写、测试去 flaky
- 右下角残影根因定位(下层应用不重绘被显露区域)与修复(收起不缩窗,消灭显露区域)
- GHOST_DEBUG.md: 两轮调试的完整档案(手段-实证-结论)
- tools/: 残影复现/取证/值守工具集
"@ }
$commitBody = @{ message = $msg; tree = $newTree.sha; parents = @($remoteHead) } | ConvertTo-Json -Depth 5 -Compress
$newCommit = Invoke-RestMethod -Uri "$api/git/commits" -Method Post -Headers $h -Body $commitBody -ContentType 'application/json' -TimeoutSec 60
Write-Host "新 commit: $($newCommit.sha)"

# 7. 更新 main 引用
$updateBody = @{ sha = $newCommit.sha; force = $false } | ConvertTo-Json -Compress
$updated = Invoke-RestMethod -Uri "$api/git/refs/heads/main" -Method Patch -Headers $h -Body $updateBody -ContentType 'application/json' -TimeoutSec 30
Write-Host ("推送完成! main → {0}" -f $updated.object.sha)
