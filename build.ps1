<#
.SYNOPSIS
  SideBySide 배포 빌드. 결과물은 artifacts\ 폴더.
.EXAMPLE
  ./build.ps1                 # .NET 8 Desktop Runtime 필요, 작은 단일 exe
  ./build.ps1 -SelfContained  # 런타임 포함, 설치 없이 실행
#>
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet test tests/SideBySide.Core.Tests -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "테스트 실패" }

$out = Join-Path "artifacts" ($(if ($SelfContained) { "$Runtime-self-contained" } else { $Runtime }))
dotnet publish src/SideBySide.App -c $Configuration -r $Runtime -o $out `
    --self-contained $($SelfContained.IsPresent.ToString().ToLower()) `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "게시 실패" }

Write-Host "완료: $out\SideBySide.exe"
