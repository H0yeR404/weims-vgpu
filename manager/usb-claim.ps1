# SPDX-License-Identifier: GPL-2.0-or-later
# Gives selected USB devices to QEMU by binding Windows' inbox WinUSB driver, and puts the original
# driver back when the device is unchecked or QEMU exits. The process is elevated because changing a
# driver requires an administrator. It outlives Manager. Keyboard, mouse and Bluetooth devices are refused.
param([Parameter(Mandatory)][string]$Directory, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$rulesPath = Join-Path $Directory 'usb-rules.txt'
$readyPath = Join-Path $Directory 'usb-ready.txt'
$errorPath = Join-Path $Directory 'usb-error.txt'
$qemuFile = Join-Path $Directory 'usb-pid.txt'
$genPath = Join-Path $Directory 'usb-rules.gen'
$ackPath = Join-Path $Directory 'usb-rules.ack'
$holderPath = Join-Path $Directory 'usb-holder.txt'
$statePath = Join-Path $Directory 'usb-drivers.txt'
$logPath = Join-Path $Directory 'usb-claim.log'

function Log([string]$message) {
    [IO.File]::AppendAllText($logPath, ((Get-Date).ToString('o') + ' ' + $message + [Environment]::NewLine))
}

$source = @'
using System;
using System.Runtime.InteropServices;
public static class WeimsUsbClaim {
    [DllImport("mssign32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int SignerSign(IntPtr subject, IntPtr cert, IntPtr signature, IntPtr provider, string timestamp, IntPtr request, IntPtr sip);
    [StructLayout(LayoutKind.Sequential)] struct FileInfo { public uint cbSize; public IntPtr name; public IntPtr file; }
    [StructLayout(LayoutKind.Sequential)] struct SubjectInfo { public uint cbSize; public IntPtr index; public uint choice; public IntPtr info; }
    [StructLayout(LayoutKind.Sequential)] struct CertStore { public uint cbSize; public IntPtr cert; public uint policy; public IntPtr store; }
    [StructLayout(LayoutKind.Sequential)] struct CertInfo { public uint cbSize; public uint choice; public IntPtr info; public IntPtr hwnd; }
    [StructLayout(LayoutKind.Sequential)] struct SignatureInfo { public uint cbSize; public uint alg; public uint choice; public IntPtr attr; public IntPtr auth; public IntPtr unauth; }
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string inf, uint flags, out bool reboot);
    public static void SignCatalog(string cat, IntPtr cert) {
        var name = Marshal.StringToHGlobalUni(cat);
        var index = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(index, 0);
        var file = new FileInfo { cbSize = (uint)Marshal.SizeOf<FileInfo>(), name = name };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        Marshal.StructureToPtr(file, filePtr, false);
        var subject = new SubjectInfo { cbSize = (uint)Marshal.SizeOf<SubjectInfo>(), index = index, choice = 1, info = filePtr };
        var subjectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SubjectInfo>());
        Marshal.StructureToPtr(subject, subjectPtr, false);
        var store = new CertStore { cbSize = (uint)Marshal.SizeOf<CertStore>(), cert = cert, policy = 2 };
        var storePtr = Marshal.AllocHGlobal(Marshal.SizeOf<CertStore>());
        Marshal.StructureToPtr(store, storePtr, false);
        var certInfo = new CertInfo { cbSize = (uint)Marshal.SizeOf<CertInfo>(), choice = 2, info = storePtr };
        var certPtr = Marshal.AllocHGlobal(Marshal.SizeOf<CertInfo>());
        Marshal.StructureToPtr(certInfo, certPtr, false);
        var sig = new SignatureInfo { cbSize = (uint)Marshal.SizeOf<SignatureInfo>(), alg = 0x800c };
        var sigPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SignatureInfo>());
        Marshal.StructureToPtr(sig, sigPtr, false);
        var hr = SignerSign(subjectPtr, certPtr, sigPtr, IntPtr.Zero, null, IntPtr.Zero, IntPtr.Zero);
        if (hr != 0) throw new Exception("sign " + hr.ToString("X8"));
    }
    public static void InstallDriver(string hardwareId, string inf) {
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, inf, 1, out reboot))
            throw new Exception("driver " + Marshal.GetLastWin32Error());
    }
}
'@

function Ensure-Certificate {
    $subject = 'CN=Weims USB'
    $found = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $found) {
        $found = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\LocalMachine\My -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(10) -KeyExportPolicy NonExportable
    }
    foreach ($name in @('Root', 'TrustedPublisher')) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, 'LocalMachine')
        $store.Open('ReadWrite')
        if ($store.Certificates.Find('FindByThumbprint', $found.Thumbprint, $false).Count -eq 0) { $store.Add($found) }
        $store.Close()
    }
    return $found
}

function Publish-Package([string]$name, [string[]]$hardwareIds) {
    $inf = Join-Path $Directory ($name + '.inf')
    $cat = Join-Path $Directory ($name + '.cat')
    $models = ($hardwareIds | ForEach-Object { "%DeviceName%=USB_Install, $_" }) -join "`n"
    $body = @"
[Version]
Signature="`$Windows NT`$"
Class=USBDevice
ClassGUID={88BAE032-5A81-49f0-BC3D-A4FF138216D6}
Provider=%ProviderName%
CatalogFile=$name.cat
DriverVer=11/28/2026,10.0.0.0

[Manufacturer]
%ProviderName%=WeimsUsb,NTamd64

[WeimsUsb.NTamd64]
$models

[USB_Install]
Include=winusb.inf
Needs=WINUSB.NT

[USB_Install.Services]
Include=winusb.inf
Needs=WINUSB.NT.Services

[USB_Install.HW]
AddReg=Dev_AddReg

[Dev_AddReg]
HKR,,DeviceInterfaceGUIDs,0x10000,"{a5dcbf10-6530-11d2-901f-00c04fb951ed}"

[Strings]
ProviderName="Weims"
DeviceName="Weims USB device"
"@
    $text = (($body -replace "`r`n", "`n") -replace "`r", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($inf, $text, [Text.Encoding]::ASCII)
    if (Test-Path -LiteralPath $cat) { Remove-Item -LiteralPath $cat -Force }
    Log "catalog $name"
    New-FileCatalog -Path $inf -CatalogFilePath $cat -CatalogVersion 2 -WarningAction SilentlyContinue | Out-Null
    if (-not (Test-Path -LiteralPath $cat)) { throw "没有生成驱动目录。 / No catalog was created." }
    Log "sign $name"
    [WeimsUsbClaim]::SignCatalog($cat, $script:signingCert.Handle)
    $output = & "$env:SystemRoot\System32\pnputil.exe" /add-driver $inf /install 2>&1 | Out-String
    Log ("pnputil " + ($output -replace '\s+', ' ').Trim())
    $published = [regex]::Match($output, 'oem\d+\.inf', [Text.RegularExpressions.RegexOptions]::IgnoreCase).Value
    if (-not $published) {
        $listed = & "$env:SystemRoot\System32\pnputil.exe" /enum-drivers 2>&1 | Out-String
        $block = [regex]::Match($listed, "(?s)oem\d+\.inf.{0,400}?$([regex]::Escape($name + '.inf'))", [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($block.Success) { $published = [regex]::Match($block.Value, 'oem\d+\.inf').Value }
    }
    if (-not $published) { throw "驱动没有装上。 / The driver was not published. $output" }
    return $published
}

function Remove-Package([string]$published) {
    if (-not $published) { return }
    $output = & "$env:SystemRoot\System32\pnputil.exe" /delete-driver $published /uninstall /force 2>&1 | Out-String
    Log ("delete " + $published + ' ' + ($output -replace '\s+', ' ').Trim())
}

function Device-Nodes([string]$vendor, [string[]]$products) {
    $nodes = @()
    foreach ($product in $products) {
        $key = "HKLM:\SYSTEM\CurrentControlSet\Enum\USB\VID_$vendor&PID_$product"
        if (-not (Test-Path -LiteralPath $key)) { continue }
        foreach ($child in Get-ChildItem -LiteralPath $key) {
            $id = "USB\VID_$vendor&PID_$product\$($child.PSChildName)"
            if ($id -match '&MI_') { continue }
            $node = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
            if ($node -and $node.Status -eq 'OK') { $nodes += $node }
        }
    }
    return $nodes
}

function Child-Classes([string]$vendor, [string]$product) {
    $classes = @()
    $root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB'
    if (-not (Test-Path -LiteralPath $root)) { return $classes }
    foreach ($child in Get-ChildItem -LiteralPath $root) {
        if ($child.PSChildName -notmatch "^VID_$vendor&PID_$product&MI_") { continue }
        foreach ($inst in Get-ChildItem -LiteralPath $child.PSPath) {
            $class = (Get-ItemProperty -LiteralPath $inst.PSPath -ErrorAction SilentlyContinue).Class
            if ($class) { $classes += $class }
        }
    }
    return $classes
}

function Assert-Safe([string]$vendor, [string[]]$products) {
    foreach ($product in $products) {
        $nodes = @(Device-Nodes $vendor @($product))
        foreach ($node in $nodes) {
            $service = [string](Get-PnpDeviceProperty -InstanceId $node.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
            if ($service -match 'hub') { throw "拒绝 USB 集线器。 / Refusing a USB hub." }
            if ($node.Class -in @('Keyboard', 'Mouse', 'Bluetooth', 'HIDClass')) { throw "拒绝键盘、鼠标或蓝牙。 / Refusing a keyboard, mouse or Bluetooth device." }
            $kids = @(Child-Classes $vendor $product)
            if ($kids | Where-Object { $_ -in @('Keyboard', 'Mouse', 'Bluetooth') }) { throw "拒绝键盘、鼠标或蓝牙。 / Refusing a keyboard, mouse or Bluetooth device." }
            if ($kids.Count -gt 0 -and @($kids | Where-Object { $_ -ne 'HIDClass' }).Count -eq 0) { throw "拒绝键盘或鼠标接收器。 / Refusing a keyboard or mouse receiver." }
        }
    }
}

function Read-State {
    $rows = @()
    if (-not (Test-Path -LiteralPath $statePath)) { return $rows }
    foreach ($line in @(Get-Content -LiteralPath $statePath)) {
        $parts = $line.Split("`t")
        if ($parts.Count -ge 4) { $rows += [pscustomobject]@{ Rule = $parts[0]; Published = $parts[1]; Original = $parts[2]; Instance = $parts[3] } }
    }
    return $rows
}

function Write-State($rows) {
    $lines = @($rows | ForEach-Object { "$($_.Rule)`t$($_.Published)`t$($_.Original)`t$($_.Instance)" })
    [IO.File]::WriteAllLines($statePath, $lines)
}

function Hardware-Id([string]$instance) {
    if ($instance -match '^(USB\\VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4})') { return $Matches[1] }
    return $null
}

function Restore-Row($row) {
    Remove-Package $row.Published
    $hardware = Hardware-Id $row.Instance
    $node = $null
    if ($row.Instance) { $node = Get-PnpDevice -InstanceId $row.Instance -ErrorAction SilentlyContinue }
    if (-not ($node -and $node.Status -eq 'OK' -and $hardware -and $row.Original -and (Test-Path -LiteralPath $row.Original))) {
        Log "removed $($row.Published)"
        return
    }
    try { [WeimsUsbClaim]::InstallDriver($hardware, $row.Original) } catch { Log $_.Exception.Message }
    & "$env:SystemRoot\System32\pnputil.exe" /restart-device $row.Instance 2>&1 | Out-Null
    $deadline = (Get-Date).AddSeconds(6)
    do {
        $service = [string](Get-PnpDeviceProperty -InstanceId $row.Instance -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        if ($service -and $service -ne 'WINUSB') { Log "restored $($row.Instance) $service"; return }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    Log "restore queued $($row.Instance)"
}

function Present-Interfaces([string]$vendor, [string[]]$products) {
    $found = @()
    $root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB'
    if (-not (Test-Path -LiteralPath $root)) { return $found }
    foreach ($product in $products) {
        foreach ($child in Get-ChildItem -LiteralPath $root) {
            if ($child.PSChildName -notmatch "^VID_$vendor&PID_$product&MI_") { continue }
            foreach ($inst in Get-ChildItem -LiteralPath $child.PSPath) {
                $id = "USB\$($child.PSChildName)\$($inst.PSChildName)"
                $node = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
                if ($node -and $node.Status -eq 'OK') { $found += $id }
            }
        }
    }
    return $found
}

# A phone is a composite device. WinUSB on the parent still leaves the photo and mobile-device
# interfaces with Windows, so the guest receives a partial device and Finder never sees the phone.
function Release-Interfaces([string]$vendor, [string[]]$products, [string]$instance, [string]$hardware, [string]$inf) {
    $ids = @(Present-Interfaces $vendor $products)
    if ($ids.Count -eq 0) { return }
    foreach ($id in $ids) {
        $output = & "$env:SystemRoot\System32\pnputil.exe" /remove-device $id 2>&1 | Out-String
        Log ("remove " + $id + ' ' + ($output -replace '\s+', ' ').Trim())
    }
    $deadline = (Get-Date).AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 400
        $kids = @(Present-Interfaces $vendor $products)
        $node = Get-PnpDevice -InstanceId $instance -ErrorAction SilentlyContinue
        $service = ''
        if ($node -and $node.Status -eq 'OK') { $service = [string](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data }
        if ($kids.Count -eq 0 -and $service -eq 'WINUSB') { Log "whole $instance"; return }
        if ($node -and $node.Status -eq 'OK' -and $service -and $service -ne 'WINUSB' -and $hardware -and (Test-Path -LiteralPath $inf)) {
            try { [WeimsUsbClaim]::InstallDriver($hardware, $inf) } catch { Log $_.Exception.Message }
        }
    } while ((Get-Date) -lt $deadline)
    if (@(Present-Interfaces $vendor $products).Count -gt 0) { throw "Windows 还占着手机的接口。 / Windows is still holding the phone's interfaces." }
}

function Claim-Rule([string]$rule) {
    $parts = $rule.Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2) { throw "bad rule: $rule" }
    $vendor = $parts[0].ToUpperInvariant()
    $products = if ($parts[1] -eq '*') { @('12A8', '12AB') } else { @($parts[1].ToUpperInvariant()) }
    Assert-Safe $vendor $products
    $nodes = @(Device-Nodes $vendor $products)
    $instance = if ($nodes.Count -gt 0) { $nodes[0].InstanceId } else { '' }
    $original = ''
    $stem = if ($parts[1] -eq '*') { "weims-$vendor-iphone" } else { "weims-$vendor-$($products[0])" }
    $packageInf = Join-Path $Directory ($stem + '.inf')
    if ($instance) {
        $original = [string](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
        if ($original -and -not [IO.Path]::IsPathRooted($original)) { $original = Join-Path "$env:SystemRoot\INF" $original }
        $service = [string](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        if ($service -eq 'WINUSB') {
            Release-Interfaces $vendor $products $instance (Hardware-Id $instance) $packageInf
            Log "already winusb $instance"
            return [pscustomobject]@{ Rule = $rule; Published = ''; Original = ''; Instance = $instance }
        }
    }
    $ids = @($products | ForEach-Object { "USB\VID_$vendor&PID_$_" })
    $published = Publish-Package $stem $ids
    $row = [pscustomobject]@{ Rule = $rule; Published = $published; Original = $original; Instance = $instance }
    if ($instance) {
        $hardware = Hardware-Id $instance
        try { [WeimsUsbClaim]::InstallDriver($hardware, $packageInf) }
        catch { Restore-Row $row; throw }
        $deadline = (Get-Date).AddSeconds(20)
        do {
            $service = [string](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
            if ($service -eq 'WINUSB') {
                try { Release-Interfaces $vendor $products $instance $hardware $packageInf }
                catch { Restore-Row $row; throw }
                Log "claimed $instance"
                return $row
            }
            Start-Sleep -Milliseconds 400
        } while ((Get-Date) -lt $deadline)
        Restore-Row $row
        throw "设备没有换成 WinUSB。 / The device did not switch to WinUSB."
    }
    Log "package $published waiting for device"
    return $row
}

function Sync-Rules {
    if (-not (Test-Path -LiteralPath $genPath)) { return }
    $gen = (Get-Content -LiteralPath $genPath -Raw).Trim()
    if ($gen.Length -eq 0 -or $gen -eq $script:seenGen) { return }
    $desired = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $rulesPath) {
        foreach ($line in @(Get-Content -LiteralPath $rulesPath)) { if ($line.Trim().Length -gt 0) { [void]$desired.Add($line.Trim()) } }
    }
    $rows = @(Read-State)
    $kept = @()
    foreach ($row in $rows) {
        if ($desired.Contains($row.Rule)) { $kept += $row } else { Restore-Row $row }
    }
    foreach ($rule in $desired) {
        if (-not @($kept | Where-Object { $_.Rule -eq $rule })) { $kept += Claim-Rule $rule }
    }
    Write-State $kept
    Set-Content -LiteralPath $ackPath -Value $gen -Encoding ascii
    if (Test-Path -LiteralPath $errorPath) { Remove-Item -LiteralPath $errorPath -Force }
    $script:seenGen = $gen
    Log ("rules " + ($desired -join ', '))
}

function Restore-All {
    foreach ($row in @(Read-State)) { try { Restore-Row $row } catch { Log $_.Exception.Message } }
    if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force }
}

function Invoke-SelfTest {
    Log 'selftest'
    $script:signingCert = Ensure-Certificate
    $dummy = Publish-Package 'weims-selftest' @('USB\VID_0001&PID_0001', 'USB\VID_0001&PID_0002')
    Remove-Package $dummy
    Log "selftest package $dummy"
    $phone = @(Device-Nodes '05AC' @('12A8', '12AB'))
    if ($phone.Count -eq 0) { Log 'selftest no phone'; return }
    Assert-Safe '05AC' @('12A8', '12AB')
    $before = [string](Get-PnpDeviceProperty -InstanceId $phone[0].InstanceId -KeyName 'DEVPKEY_Device_Service').Data
    $row = $null
    try {
        $row = Claim-Rule '05AC *'
        Write-State @($row)
        $now = [string](Get-PnpDeviceProperty -InstanceId $phone[0].InstanceId -KeyName 'DEVPKEY_Device_Service').Data
        if ($now -ne 'WINUSB') { throw "phone service is $now" }
        Log "selftest phone $before -> $now"
    } finally {
        if ($row) { Restore-Row $row }
        if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force }
    }
    $after = [string](Get-PnpDeviceProperty -InstanceId $phone[0].InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
    Log "selftest restored $after"
    if ($after -eq 'WINUSB') { throw 'phone stayed on WinUSB' }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'administrator required'
}
Add-Type -TypeDefinition $source -Language CSharp
if ($SelfTest) {
    try { Invoke-SelfTest; Set-Content -LiteralPath (Join-Path $Directory 'usb-selftest.txt') -Value 'ok' -Encoding ascii }
    catch { Log $_.Exception.Message; Set-Content -LiteralPath $errorPath -Value $_.Exception.Message -Encoding utf8; throw }
    return
}

$script:seenGen = ''
try {
    $script:signingCert = Ensure-Certificate
    Set-Content -LiteralPath $holderPath -Value $PID -Encoding ascii
    Sync-Rules
    Set-Content -LiteralPath $readyPath -Value 'ok' -Encoding ascii
    $deadline = (Get-Date).AddSeconds(90)
    $qemuId = ''
    while ($true) {
        try { Sync-Rules } catch { Log $_.Exception.Message; Set-Content -LiteralPath $errorPath -Value $_.Exception.Message -Encoding utf8 }
        if ($qemuId.Length -eq 0 -and (Test-Path -LiteralPath $qemuFile)) {
            $qemuId = (Get-Content -LiteralPath $qemuFile -Raw).Trim()
            Log "pid $qemuId"
            if ($qemuId -eq '0' -or $qemuId -notmatch '^[1-9][0-9]*$') { break }
        }
        if ($qemuId -match '^[1-9][0-9]*$') {
            $running = Get-Process -Id ([int]$qemuId) -ErrorAction SilentlyContinue
            if (-not $running) { Log 'qemu exited'; break }
        } elseif ($qemuId.Length -eq 0 -and (Get-Date) -gt $deadline) { Log 'no qemu pid'; break }
        Start-Sleep -Milliseconds 200
    }
} catch {
    Log $_.Exception.Message
    Set-Content -LiteralPath $errorPath -Value $_.Exception.Message -Encoding utf8
} finally { Restore-All }
