# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [string]$Vm = 'default', [switch]$Exit, [switch]$DryRun, [switch]$NoDisk,
    [string]$LogSpec = 'info', [string]$Qemu = '',
    [ValidateSet('', '0', '1')][string]$PfIntercept = '', [ValidateSet('', '0', '1')][string]$MmioBql = '',
    [switch]$WhpxStats, [string]$Affinity = ''
)
$ErrorActionPreference = 'Stop'
if ($Vm -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid VM name' }
$Root = Split-Path $PSScriptRoot; $VmDir = "$Root\vm\$Vm"
$Dist = "$Root\build\port-dist"
if (!$Qemu) { $Qemu = "$Root\build\port-qemu\qemu-system-x86_64.exe" }
$c = Get-Content -Raw "$VmDir\state\config.json" | ConvertFrom-Json
$runtimePath = "$VmDir\state\runtime.json"

function Send-Qmp([int]$Port, [string]$Command) {
    $client = [Net.Sockets.TcpClient]::new('127.0.0.1', $Port)
    try {
        $s = $client.GetStream(); $r = [IO.StreamReader]::new($s); $w = [IO.StreamWriter]::new($s); $w.AutoFlush = $true; $w.NewLine = "`n"
        [void]$r.ReadLine(); $w.WriteLine('{"execute":"qmp_capabilities"}'); [void]$r.ReadLine()
        $w.WriteLine("{`"execute`":`"$Command`"}"); return $r.ReadLine()
    } finally { $client.Dispose() }
}

if ($Exit) {
    if (!(Test-Path $runtimePath)) { 'no runtime record'; return }
    $rt = Get-Content -Raw $runtimePath | ConvertFrom-Json
    $p = Get-Process -Id $rt.pid -ErrorAction SilentlyContinue
    if (!$p -or $p.Path -ne $rt.executable) { 'QEMU is not running'; return }
    try { [void](Send-Qmp $c.qmp_port 'quit') } catch { }
    if (!$p.WaitForExit(15000)) { throw "QEMU $($rt.pid) did not quit" }
    "QEMU $($rt.pid) quit"; return
}

if (!$env:WEIMS_OSK) { throw 'Set WEIMS_OSK from your own authorized installation' }
if (!$env:WEIMS_MSYS2) { throw 'Set WEIMS_MSYS2' }
function Q([string]$Path) { $Path.Replace('\', '/').Replace(',', ',,') }
$run = "$VmDir\logs\" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$gfx = "reims-graphics-pci,id=gfx,bus=pcie.0,addr=0x4,romfile=$(Q $c.rom),resolution=$($c.resolution)"
$a = @(
    '-L', (Q "$Root\sources\qemu\pc-bios"),
    '-name', "reims-$Vm", '-machine', 'q35,max-ram-below-4g=1G', '-accel', 'whpx,hyperv=off',
    '-cpu', $c.cpu, '-smp', "$($c.cpus),sockets=1,cores=$($c.cpus),threads=1", '-m', "$($c.memory_mib)M",
    '-global', 'ICH9-LPC.acpi-pci-hotplug-with-bridge-support=off',
    '-global', 'ICH9-LPC.disable_s3=1', '-global', 'ICH9-LPC.disable_s4=1',
    '-device', 'pcie-root-port,bus=pcie.0,addr=0x1,slot=1,x-speed=16,x-width=32',
    '-device', "isa-applesmc,osk=$env:WEIMS_OSK",
    '-drive', "if=pflash,format=raw,readonly=on,file=$(Q $c.firmware_code)",
    '-drive', "if=pflash,format=raw,file=$(Q $c.firmware_vars)", '-smbios', 'type=2',
    '-device', 'ich9-ahci,id=sata,addr=0x2'
)
if (!$NoDisk) {
    $a += @(
        '-drive', "id=opencore,if=none,format=raw,snapshot=on,file=$(Q $c.opencore)",
        '-device', 'ide-hd,bus=sata.2,drive=opencore,bootindex=1',
        '-drive', "id=disk,if=none,format=qcow2,file=$(Q $c.active_disk)",
        '-device', 'ide-hd,bus=sata.3,drive=disk,rotation_rate=1'
    )
}
$a += @(
    '-netdev', "user,id=net0,ipv6=off,hostfwd=tcp:127.0.0.1:$($c.ssh_port)-:22",
    '-device', "virtio-net-pci,netdev=net0,id=net0,mac=$($c.mac),addr=0x3", '-vga', 'none',
    '-device', $gfx,
    '-device', 'qemu-xhci,id=xhci,addr=0x5,p2=8,p3=6,conditional-intr-mapping=on',
    '-device', 'usb-kbd,id=kbd,bus=xhci.0', '-device', 'usb-tablet,id=tablet,bus=xhci.0',
    '-audiodev', 'none,id=snd0,in.voices=0', '-device', 'usb-audio,id=audio,audiodev=snd0,bus=xhci.0',
    '-display', 'none', '-monitor', 'none', '-serial', "file:$(Q "$run\serial.log")", '-msg', 'timestamp=on',
    '-qmp', "tcp:127.0.0.1:$($c.qmp_port),server=on,wait=off", '-no-shutdown'
)
if ($DryRun) { ($a -join ' ') -replace 'osk=[^ ]+', 'osk=<redacted>'; return }

if (Test-Path $runtimePath) {
    $rt = Get-Content -Raw $runtimePath | ConvertFrom-Json
    $p = Get-Process -Id $rt.pid -ErrorAction SilentlyContinue
    if ($p -and $p.Path -eq $rt.executable) { throw "This VM's QEMU is still running (PID $($rt.pid)); use -Exit first." }
}
foreach ($f in @($Qemu, $c.firmware_code, $c.firmware_vars, $c.rom) + $(if ($NoDisk) { @() } else { @($c.opencore, $c.active_disk) })) {
    if (!(Test-Path -LiteralPath $f -PathType Leaf)) { throw "Missing VM input: $f" }
}
New-Item -ItemType Directory -Force $run | Out-Null
# The library's DLL and MSYS2's runtime DLLs.
$env:PATH = "$Dist\bin;$env:WEIMS_MSYS2\ucrt64\bin;$env:PATH"

$env:VK_LOADER_LAYERS_DISABLE = '~implicit~'
foreach ($v in 'DISABLE_RTSS_LAYER', 'DISABLE_VULKAN_OW_OVERLAY_LAYER', 'DISABLE_VULKAN_OW_OBS_CAPTURE', 'DISABLE_VK_LAYER_VALVE_steam_overlay_1', 'DISABLE_VK_LAYER_VALVE_steam_fossilize_1', 'DISABLE_VULKAN_OBS_CAPTURE') { Set-Item "Env:$v" '1' }
$env:REIMS_VGPU_LOG = $LogSpec
Remove-Item Env:REIMS_VGPU_WINDOW -ErrorAction SilentlyContinue
foreach ($pair in @(@('REIMS_WHPX_PF_INTERCEPT', $PfIntercept), @('REIMS_WHPX_MMIO_BQL', $MmioBql))) {
    if ($pair[1]) { Set-Item "Env:$($pair[0])" $pair[1] } else { Remove-Item "Env:$($pair[0])" -ErrorAction SilentlyContinue }
}
if ($WhpxStats) { $env:REIMS_WHPX_STATS = '1' } else { Remove-Item Env:REIMS_WHPX_STATS -ErrorAction SilentlyContinue }
$quoted = ($a | ForEach-Object { '"' + ([regex]::Replace(([regex]::Replace($_, '(\\*)"', '$1$1\"')), '(\\+)$', '$1$1')) + '"' }) -join ' '
$safeArgs = $a | ForEach-Object { $_ -replace 'osk=.*', 'osk=<redacted>' }
@{ executable = $Qemu; arguments = $safeArgs; env = @{ REIMS_VGPU_LOG = $LogSpec; PF = $PfIntercept; BQL = $MmioBql } } | ConvertTo-Json -Depth 5 | Set-Content "$run\command.json" -Encoding utf8NoBOM
$proc = Start-Process -FilePath $Qemu -ArgumentList $quoted -WorkingDirectory $Root -WindowStyle Hidden -RedirectStandardOutput "$run\stdout.log" -RedirectStandardError "$run\stderr.log" -PassThru
if ($Affinity) { $proc.ProcessorAffinity = [IntPtr][Convert]::ToInt64($Affinity, 16) }
@{ pid = $proc.Id; executable = $Qemu; qmp_port = $c.qmp_port; run_directory = $run } | ConvertTo-Json | Set-Content $runtimePath -Encoding utf8NoBOM
Start-Sleep -Seconds 3
if ($proc.HasExited) { Get-Content "$run\stderr.log" -Tail 20; throw "QEMU exited with code $($proc.ExitCode)" }
"Started PID $($proc.Id), QMP 127.0.0.1:$($c.qmp_port). Logs: $run"
