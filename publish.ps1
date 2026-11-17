# 配布用の単体 exe を作る (.NET ランタイム同梱。インストール不要で動く)
# 使い方: .\publish.ps1   → publish\BandcampBenrify.exe
$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\src\BandcampBenrify" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o "$PSScriptRoot\publish"
Write-Host "出力: $PSScriptRoot\publish\BandcampBenrify.exe"
