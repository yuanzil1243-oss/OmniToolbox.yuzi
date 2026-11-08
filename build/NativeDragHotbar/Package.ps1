param(
    [string]$Destination = 'C:\Users\24496\Documents\ChatGPT\本地模块dll',
    [string]$OmniDir = '',
    [string]$DalamudDir = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $PSScriptRoot 'NativeDragHotbar.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$name = [string]$projectXml.Project.PropertyGroup.AssemblyName
$label = "拖放热键栏_${name}_${version}"
$dllName = "$label.dll"
$readmeName = "${label}_使用说明.txt"
$zipName = "${label}_本地安装包.zip"
$sumsName = "$label.sha256"
$stage = Join-Path $root "outputs\$name-$version-local"
foreach ($target in @($dllName, $readmeName)) {
    if (Test-Path -LiteralPath (Join-Path $Destination $target)) { throw "版本文件已经存在，不覆盖发布：$target。请先递增版本。" }
}
if (Test-Path -LiteralPath $stage) { throw "本版本打包目录已存在，请检查或递增版本：$stage" }
$buildArgs = @('build', $project, '-c', 'Release', '--nologo')
if ($OmniDir) {
    $OmniDir = [IO.Path]::GetFullPath($OmniDir)
    $buildArgs += "-p:OmniDir=$OmniDir"
    $env:NATIVE_DRAG_HOTBAR_OMNI_DIR = $OmniDir
}
if ($DalamudDir) {
    $DalamudDir = [IO.Path]::GetFullPath($DalamudDir)
    $buildArgs += "-p:DalamudDir=$DalamudDir"
    $env:NATIVE_DRAG_HOTBAR_DALAMUD_DIR = $DalamudDir
}
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
& dotnet run --project (Join-Path $root 'tests\NativeDragHotbar.Tests\NativeDragHotbar.Tests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Offline checks failed.' }
$binary = Join-Path $PSScriptRoot "bin\Release\net10.0-windows7.0\$name.dll"
$assembly = [Reflection.AssemblyName]::GetAssemblyName($binary)
$file = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
if ($assembly.Name -ne $name -or $assembly.Version.ToString() -ne "$version.0" -or $file.FileVersion -ne "$version.0" -or $file.ProductVersion -ne $version) {
    throw 'Assembly identity or actual file version does not match project version.'
}
[void][IO.Directory]::CreateDirectory($stage)
[void][IO.Directory]::CreateDirectory($Destination)
Copy-Item -LiteralPath $binary -Destination (Join-Path $stage $dllName)
Copy-Item -LiteralPath (Join-Path $root "docs\NativeDragHotbar-$version.md") -Destination (Join-Path $stage $readmeName)
$hash = (Get-FileHash -LiteralPath (Join-Path $stage $dllName) -Algorithm SHA256).Hash
$manifest = [ordered]@{
    SchemaVersion = 1
    Modules = @([ordered]@{
        InternalName = $name; Author = 'Yuzi'; RepositoryURL = 'https://github.com/yuanzil1243-oss/OmniToolbox.yuzi'; Version = $version; ApiVersion = 1
        MinimumOmniVersion = '1.1.8.9'; File = $dllName; Sha256 = $hash
        Localization = @{ Source = @{
            Title = '拖放热键栏'
            Description = '支持接收游戏原生技能与功能按钮，右键编辑图标、说明及自定义指令，支持自由布局和导入导出。'
        }}
    })
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stage 'TreeHouseModules.json'), ($manifest | ConvertTo-Json -Depth 8) + "`n", $utf8)
$stageChecksums = foreach ($entry in @($dllName, $readmeName, 'TreeHouseModules.json')) {
    "{0}  {1}" -f (Get-FileHash -LiteralPath (Join-Path $stage $entry) -Algorithm SHA256).Hash, $entry
}
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $stageChecksums, $utf8)
Copy-Item -LiteralPath (Join-Path $stage $dllName) -Destination (Join-Path $Destination $dllName)
Copy-Item -LiteralPath (Join-Path $stage $readmeName) -Destination (Join-Path $Destination $readmeName)
Compress-Archive -LiteralPath @((Join-Path $stage $dllName), (Join-Path $stage $readmeName), (Join-Path $stage 'TreeHouseModules.json'), (Join-Path $stage 'SHA256SUMS.txt')) -DestinationPath (Join-Path $stage $zipName) -CompressionLevel Optimal

# Read the delivered files and archived payload back, rather than trusting the copy operation.
$deliveredHash = (Get-FileHash -LiteralPath (Join-Path $Destination $dllName) -Algorithm SHA256).Hash
$deliveredManifest = Get-Content -LiteralPath (Join-Path $stage 'TreeHouseModules.json') -Raw | ConvertFrom-Json
if ($deliveredHash -ne $hash -or $deliveredManifest.Modules[0].Sha256 -ne $hash -or $deliveredManifest.Modules[0].File -ne $dllName -or $deliveredManifest.Modules[0].Version -ne $version -or $deliveredManifest.Modules[0].InternalName -ne $assembly.Name -or $deliveredManifest.Modules[0].Author -ne 'Yuzi') { throw 'Delivered manifest verification failed.' }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $stage $zipName))
try {
    $entry = $archive.GetEntry($dllName)
    if ($null -eq $entry) { throw 'Archive DLL missing.' }
    $stream = $entry.Open()
    try { $zipHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
    if ($zipHash -ne $hash -or $archive.Entries.Count -ne 4) { throw 'Archive verification failed.' }
} finally { $archive.Dispose() }
$checksums = foreach ($entry in @($dllName, $readmeName, 'TreeHouseModules.json', $zipName)) {
    "{0}  {1}" -f (Get-FileHash -LiteralPath (Join-Path $stage $entry) -Algorithm SHA256).Hash, $entry
}
[IO.File]::WriteAllLines((Join-Path $stage $sumsName), $checksums, $utf8)
if ((Get-FileHash -LiteralPath (Join-Path $Destination $readmeName) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stage $readmeName) -Algorithm SHA256).Hash) { throw 'Delivered instructions verification failed.' }
[pscustomobject]@{ Version = $version; InternalName = $name; AssemblyVersion = $assembly.Version.ToString(); FileVersion = $file.FileVersion; SHA256 = $hash; Destination = $Destination; Files = @($dllName, $readmeName); BuildArtifacts = $stage } | ConvertTo-Json -Depth 4
