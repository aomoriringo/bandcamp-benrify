# install.ps1 で入れた Bandcamp Benrify を取り除く。
#   - スタートメニュー / デスクトップのショートカットと、%LOCALAPPDATA%\Programs\BandcampBenrify を削除
#   - 設定・キュー (%APPDATA%\BandcampBenrify) とキャッシュ (%LOCALAPPDATA%\BandcampBenrify) は残す。
#     -RemoveData を付けるとそれらも削除する
# 使い方: pwsh -File uninstall.ps1 [-RemoveData]
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'

$appName = 'Bandcamp Benrify'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\BandcampBenrify'

if (Get-Process BandcampBenrify -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installDir*" }) {
    throw "$appName が起動中です。終了してからもう一度実行してください。"
}

foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"))) {
    if (Test-Path $lnk) {
        Remove-Item $lnk
        Write-Host "削除しました: $lnk"
    }
}

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse
    Write-Host "削除しました: $installDir"
}

if ($RemoveData) {
    foreach ($dir in @((Join-Path $env:APPDATA 'BandcampBenrify'), (Join-Path $env:LOCALAPPDATA 'BandcampBenrify'))) {
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse
            Write-Host "削除しました: $dir"
        }
    }
}
else {
    Write-Host "設定・キュー・キャッシュは残しています (消す場合は -RemoveData)。"
}
