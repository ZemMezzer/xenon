param([Parameter(Mandatory = $true)][string] $PublishDirectory)

$ErrorActionPreference = 'Stop'
$sourceIcon = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../../icon.png'))
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class XenonIconVerification
{
    public static bool ContainsPng(string path, byte[] png)
    {
        return File.ReadAllBytes(path).AsSpan().IndexOf(png.AsSpan()) >= 0;
    }

    private delegate bool ResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, ResourceNameCallback callback, IntPtr parameter);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32")]
    private static extern bool FreeLibrary(IntPtr module);

    public static void VerifyWindowsIcon(string path)
    {
        IntPtr module = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new InvalidOperationException("Cannot read executable resources.");
        try
        {
            int frames = 0;
            ResourceNameCallback callback = (handle, type, name, parameter) =>
            {
                IntPtr group = FindResource(handle, name, type);
                uint size = SizeofResource(handle, group);
                if (size < 6) return true;
                byte[] data = new byte[size];
                Marshal.Copy(LockResource(LoadResource(handle, group)), data, 0, data.Length);
                int count = BitConverter.ToUInt16(data, 4);
                if (size < 6 + 14 * count) return true;
                for (int i = 0; i < count; i++)
                {
                    int id = BitConverter.ToUInt16(data, 6 + 14 * i + 12);
                    IntPtr image = FindResource(handle, (IntPtr)id, (IntPtr)3);
                    if (image != IntPtr.Zero && SizeofResource(handle, image) > 0) frames++;
                }
                return true;
            };
            EnumResourceNames(module, (IntPtr)14, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (frames < 7) throw new InvalidOperationException("Expected seven embedded application icon sizes.");
        }
        finally { FreeLibrary(module); }
    }
}
'@

$windowsBinary = Join-Path $PublishDirectory 'xenon.exe'
if (Test-Path -LiteralPath $windowsBinary) {
    $binary = $windowsBinary
    [XenonIconVerification]::VerifyWindowsIcon($binary)
} else {
    $bundle = Join-Path $PublishDirectory 'Xenon.app'
    $binary = Join-Path $bundle 'Contents/MacOS/xenon'
    if (Test-Path -LiteralPath (Join-Path $PublishDirectory 'xenon')) {
        throw 'The macOS distribution must contain only the bundled executable.'
    }
    $plist = Join-Path $bundle 'Contents/Info.plist'
    $iconName = & /usr/bin/plutil -extract CFBundleIconFile raw -o - $plist
    if ($LASTEXITCODE -ne 0 -or $iconName -ne 'Xenon.icns') { throw 'Invalid bundle icon metadata.' }
    $iconBytes = [IO.File]::ReadAllBytes((Join-Path $bundle "Contents/Resources/$iconName"))
    if ([Text.Encoding]::ASCII.GetString($iconBytes, 0, 4) -ne 'icns') { throw 'Invalid ICNS resource.' }
    & /usr/bin/codesign --verify --strict $bundle
    if ($LASTEXITCODE -ne 0) { throw 'Invalid application bundle signature.' }
    & $binary --version
    if ($LASTEXITCODE -ne 0) { throw 'The bundled CLI failed to start.' }
}
if (-not [XenonIconVerification]::ContainsPng($binary, $sourceIcon)) {
    throw 'The original icon.png is missing from the NativeAOT executable.'
}
Write-Output 'Application icon and embedded source PNG verified.'
