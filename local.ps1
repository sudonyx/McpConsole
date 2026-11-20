param(
    [Parameter(Position=0)]
    [ValidateSet('setup','start','status','stop','connect','check','self-test','help')]
    [string]$Command = 'start',
    [Alias('Profile')]
    [string]$LocalProfile = 'demo',
    [string]$Root,
    [string]$Capability,
    [switch]$NoChat,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
$tool = Join-Path $PSScriptRoot 'LocalSetup\LocalStack\LocalStack.csproj'
$toolArguments = @($Command, '--profile', $LocalProfile)
if ($Command -eq 'setup') { $toolArguments += @('--root', $Root) }
if ($Capability) { $toolArguments += @('--capability', $Capability) }
if ($NoChat) { $toolArguments += '--no-chat' }
if ($NoBuild) { $toolArguments += '--no-build' }
& dotnet run --project $tool -- @toolArguments
exit $LASTEXITCODE
