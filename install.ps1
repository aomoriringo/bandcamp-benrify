# Bandcamp Benrify をこの PC にインストールする (管理者権限は不要)。
#   - ビルドした exe を %LOCALAPPDATA%\Programs\BandcampBenrify に置く
#   - スタートメニューに「Bandcamp Benrify」を登録する (検索して起動できる)
#   - -Desktop を付けるとデスクトップにもショートカットを作る
# 使い方: pwsh -File install.ps1 [-Desktop]
# 更新するときも同じコマンドを実行してください (設定・キュー・キャッシュはそのまま残ります)。
param([switch]$Desktop)
$ErrorActionPreference = 'Stop'

$appName = 'Bandcamp Benrify'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\BandcampBenrify'
$exe = Join-Path $installDir 'BandcampBenrify.exe'

$running = Get-Process BandcampBenrify -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    throw "インストール先の $appName が起動中です。終了してからもう一度実行してください。"
}

Write-Host "ビルドしています…"
dotnet publish "$PSScriptRoot\src\BandcampBenrify" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o "$PSScriptRoot\publish" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました" }

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item "$PSScriptRoot\publish\BandcampBenrify.exe" $exe -Force

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $installDir
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Bandcamp の曲をキューに入れて聴く'
    $lnk.Save()
}

$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
New-Shortcut $startMenu
Write-Host "スタートメニューに登録しました: $startMenu"

if ($Desktop) {
    $desk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
    New-Shortcut $desk
    Write-Host "デスクトップにショートカットを作りました: $desk"
}

Write-Host "インストールしました: $exe"
Write-Host "スタートメニューで「Bandcamp」と検索して起動できます。"
