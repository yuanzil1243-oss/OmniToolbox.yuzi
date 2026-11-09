param(
    [string]$Destination = 'C:\Users\24496\Documents\ChatGPT\本地模块dll',
    [string]$OmniDir = '',
    [string]$DalamudDir = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $PSScriptRoot 'KeyRepeater.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$version = [string]$xml.Project.PropertyGroup.Version
$name = [string]$xml.Project.PropertyGroup.AssemblyName
$label = "按键连点_${name}_${version}"
$dllName = "$label.dll"
$docName = "${label}_使用说明.txt"
$stage = Join-Path $root "outputs\KeyRepeater-$version-local"
foreach ($target in @((Join-Path $Destination $dllName), (Join-Path $Destination $docName), $stage)) {
    if (Test-Path -LiteralPath $target) { throw "版本交付已存在，禁止覆盖：$target" }
}
$build = @('build', $project, '-c', 'Release', '--nologo')
if ($OmniDir) { $build += "-p:OmniDir=$OmniDir"; $env:KEY_REPEATER_OMNI_DIR = $OmniDir }
if ($DalamudDir) { $build += "-p:DalamudDir=$DalamudDir"; $env:KEY_REPEATER_DALAMUD_DIR = $DalamudDir }
& dotnet @build
if ($LASTEXITCODE -ne 0) { throw 'Release 构建失败' }
& dotnet run --project (Join-Path $root 'tests\KeyRepeater.Tests\KeyRepeater.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '回归检查失败' }
$binary = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows7.0\KeyRepeater.dll'
$identity = [Reflection.AssemblyName]::GetAssemblyName($binary)
$file = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
if ($identity.Name -ne $name -or $identity.Version.ToString() -ne "$version.0" -or
    $file.FileVersion -ne "$version.0" -or $file.ProductVersion -ne $version) { throw '版本或程序集身份不匹配' }
$manifestPath = Join-Path $root 'TreeHouseModules.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entries = @($manifest.Modules | Where-Object InternalName -eq $name)
if ($entries.Count -ne 1 -or $entries[0].File -ne 'Modules/KeyRepeater.dll' -or $entries[0].ApiVersion -ne 1) { throw '清单身份或路径不匹配' }
$entry = $entries[0]
if ([version]$entry.Version -ge [version]$version) { throw '清单版本必须小于本次交付的新版本' }
[void][IO.Directory]::CreateDirectory($stage)
[void][IO.Directory]::CreateDirectory($Destination)
Copy-Item -LiteralPath $binary -Destination (Join-Path $root 'Modules\KeyRepeater.dll')
Copy-Item -LiteralPath $binary -Destination (Join-Path $stage $dllName)
Copy-Item -LiteralPath (Join-Path $root "docs\KeyRepeater-$version.md") -Destination (Join-Path $stage $docName)
$hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$entry.Version = $version
$entry.Sha256 = $hash
$entry.Localization.Source.Description = '自动读取技能栏主、副快捷键；支持原生“·”键及鼠标连点，并自动适配热键栏工坊的独立键盘快捷键。'
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12) + "`n", $utf8)
Copy-Item -LiteralPath (Join-Path $stage $dllName) -Destination (Join-Path $Destination $dllName)
Copy-Item -LiteralPath (Join-Path $stage $docName) -Destination (Join-Path $Destination $docName)
$sumPath = Join-Path $root "docs\KeyRepeater-$version.sha256"
[IO.File]::WriteAllText($sumPath, "$hash  Modules/KeyRepeater.dll`n", $utf8)
foreach ($copy in @((Join-Path $root 'Modules\KeyRepeater.dll'), (Join-Path $Destination $dllName))) {
    if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $hash) { throw "最终文件摘要不匹配：$copy" }
}
$saved = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).Modules | Where-Object InternalName -eq $name
if ($saved.Version -ne $version -or $saved.Sha256 -ne $hash) { throw '写入清单后校验失败' }
[pscustomobject]@{ Version=$version; AssemblyVersion=$identity.Version.ToString(); FileVersion=$file.FileVersion;
    File=(Join-Path $Destination $dllName); SHA256=$hash } | ConvertTo-Json
