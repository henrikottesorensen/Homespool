<#
.SYNOPSIS
    Configures .env on Windows, by running setup-env.sh inside the Homespool image.

.DESCRIPTION
    Windows has no bash and Docker Desktop does not bring one, so the wizard runs in a container.
    This script supplies the one thing a container cannot obtain and then gets out of the way.

    THE ONE THING is this machine's LAN address. It exists only on the Windows host: a container
    sees its own 172.x address, and WSL2 is no better - it sits behind its own NAT'd virtual switch,
    so asking there returns the VM's address exactly as a container would. Windows is the only place
    the answer lives, which is why this file exists at all.

    Everything else stays in setup-env.sh. This script does not read .env, does not know what
    settings exist, does not decide which address to use, and does not validate anything - it hands
    over a list of addresses and lets the wizard filter, rank, check and write. That division is the
    point: a second implementation of any of those would drift from the one that is tested.

    IF DOCKER DESKTOP WILL NOT INSTALL, you do not need this script at all. Windows 10 LTSC 2021 is
    the case that provokes it: Docker Desktop wants build 19045 and LTSC 2021 is pinned to 19044 for
    its whole support life, so no edition change helps. The arrangement there is WSL2 with Docker
    Engine inside it - WSL2 only needs build 18362 - and in that world bash is already present, so
    run `./setup-env.sh` directly in the distro. It recognises WSL and asks Windows for the
    addresses itself, through the same Windows-binary interop, so nothing is lost by skipping this.

.PARAMETER Arguments
    Passed through to setup-env.sh unchanged - --no-prompt, --no-overwrite, --dry-run, --set, --help.

    PREFER setup-env.cmd, which launches this. PowerShell refuses to run script files under the
    default execution policy, so invoking this one directly fails with "running scripts is disabled
    on this system" on a machine nobody has configured; the launcher passes -ExecutionPolicy Bypass
    for its own process and changes nothing on the machine.

.EXAMPLE
    .\setup-env.ps1
    Walks through the settings, then writes .env.

.EXAMPLE
    .\setup-env.ps1 --dry-run
    Says what it would change and writes nothing.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$image = 'homespool'

function Fail($message) {
    Write-Host $message -ForegroundColor Red
    exit 1
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Fail @'
setup-env.ps1: docker was not found.

This needs Docker Desktop, which is what runs Homespool itself - so if it is not installed yet,
install that first and come back.
'@
}

# ------------------------------------------------------------------------------------------------
# The image
#
# The wizard runs in Homespool's own image rather than pulling a general-purpose one: it is Debian
# underneath and carries bash, awk, sed, coreutils and openssl, so nothing extra is downloaded onto
# a machine that is about to download this anyway.
#
# The catch is ordering. Homespool's images are not published to a registry yet, so on a machine
# that has never built them there is nothing to run the wizard IN. `docker compose pull` is tried
# because it becomes the fast path the moment there is a tagged release, and it fails harmlessly
# until then - an unqualified name resolves into Docker's curated library namespace, where it simply
# does not exist.
#
# Building first is not wasted work: it has to happen before `up` regardless, and the printer
# certificate is minted on the first RUN rather than at build time - so configuring between the
# build and the first `up` is still "before the first start", which is the thing that matters.
# ------------------------------------------------------------------------------------------------
# Tested on the exit code rather than on whether anything was captured. Both work - a native command
# that fails silently really does yield $null, checked rather than assumed - but the exit code says
# what is meant, and does not quietly depend on inspect continuing to print something on success.
function Test-Image($name) {
    docker image inspect $name 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

if (-not (Test-Image $image)) {
    Write-Host "The $image image is not here yet - trying to pull it."
    docker compose pull $image 2>&1 | Out-Null
}

$haveImage = Test-Image $image

if (-not $haveImage) {
    Fail @"
setup-env.ps1: no $image image, and nothing to pull.

Homespool's images are not published to a registry yet, so build them first:

    docker compose build

Then run this again. That order is fine - the printer certificate is issued the first time the
stack RUNS, not when it is built, so configuring in between still counts as before the first start.
"@
}

# ------------------------------------------------------------------------------------------------
# The addresses
#
# Find-NetRoute answers the same question `ip route get 1.1.1.1` answers on Linux - which source
# address this machine would actually send from - so it leads the list. Everything else follows, and
# the wizard sorts out which are usable.
#
# Deliberately NOT ipconfig: its output is localised, so "IPv4 Address" is translated on a German or
# Danish install and a parser built against an English one silently finds nothing. These cmdlets
# return objects and do not care what language Windows is in.
#
# Nothing is filtered here. The vEthernet addresses that WSL and Hyper-V add are exactly the sort of
# thing that must not be offered to a printer, and the wizard already removes them by the same rule
# it uses for Docker's own ranges - applied there, once, where it is tested.
# ------------------------------------------------------------------------------------------------
$addresses = @()

try {
    $addresses += (Find-NetRoute -RemoteIPAddress 1.1.1.1 -ErrorAction Stop |
        Select-Object -ExpandProperty IPAddress)
} catch {
    # No default route is an ordinary state for a print server on an isolated network, and the
    # broader list below still answers. Not worth a warning.
}

# Adapters that are UP, and not a virtual switch. Both filters come from a real machine offering
# two addresses no printer could reach: a ProtonVPN adapter that was not even connected still
# reported 10.2.0.2, and "vEthernet (WSL)" contributed 192.168.80.1. Neither is rejectable by the
# shape of the address - 10.2.0.0/24 is an ordinary LAN - so the adapter has to be the judge.
try {
    $usable = Get-NetAdapter -ErrorAction Stop |
        Where-Object { $_.Status -eq 'Up' -and $_.Name -notlike 'vEthernet*' } |
        Select-Object -ExpandProperty ifIndex

    $addresses += (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $usable -contains $_.InterfaceIndex } |
        Select-Object -ExpandProperty IPAddress)
} catch {
    Write-Host "Could not list this machine's addresses - you will be asked to type one." `
        -ForegroundColor Yellow
}

$addresses = $addresses | Where-Object { $_ } | Select-Object -Unique

# Degrade to "too many choices" rather than "none" if the filtering above left nothing - an unusual
# adapter arrangement should not stop the wizard offering anything at all.
if (-not $addresses) {
    try {
        $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Select-Object -ExpandProperty IPAddress | Select-Object -Unique
    } catch {
        $addresses = @()
    }
}

# ------------------------------------------------------------------------------------------------
# Hand over
#
# --entrypoint bash because the image's own entrypoint starts the application. -it so the questions
# work; the wizard refuses to run interactively without a terminal rather than hanging, so losing
# this would be noisy rather than silent.
#
# The repository is mounted at /work and the container writes .env there. Note that `chmod 600`,
# which the wizard applies because the file can hold an SMTP password, is a no-op on a Windows bind
# mount - the file is not protected by mode bits here, and that is worth knowing rather than
# assuming otherwise.
# ------------------------------------------------------------------------------------------------
$dockerArgs = @(
    'run', '--rm', '-it',
    '--entrypoint', 'bash',
    '-v', "$($repoRoot):/work",
    '-w', '/work',
    '-e', "HOMESPOOL_ADDRESSES=$($addresses -join ' ')",
    $image,
    '/work/setup-env.sh'
) + $Arguments

& docker @dockerArgs
exit $LASTEXITCODE
