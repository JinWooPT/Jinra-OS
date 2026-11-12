using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using static Jinra.Os.Setup.SetupLanguage;

namespace Jinra.Os.Setup;

internal sealed record Partition(string Letter, string Guid, string FileSystem, string[] Access,
    bool Boot, bool System, bool PageFile, bool Encrypted, bool WslData);
internal sealed record Disk(int Number, string Name, string Identity, string Path, string Bus,
    long Size, int Sector, bool Offline, bool ReadOnly, bool Boot, bool System, Partition[] Parts)
{
    public bool CanWrite => Size is >= 26_000_000_000 and <= 16_000_000_000_000 && Sector == 512 &&
        !Offline && !ReadOnly && !Boot && !System && Identity.Length > 0 && Path.Length > 0 &&
        Bus is "USB" or "NVMe" or "SATA" or "SAS" or "ATA" or "SCSI" &&
        Parts.All(p => !p.Boot && !p.System && !p.PageFile && !p.Encrypted && !p.WslData);

    public override string ToString()
    {
        string letters = string.Join(", ", Parts.Where(p => p.Letter.Length == 1).Select(p => p.Letter + ":"));
        return $"{LocalText("Disk", "Disco")} {Number}  ·  {Name}  ·  {Size / 1e9:0.#} GB  ·  {Bus}" +
            (letters.Length == 0 ? "" : $"  ·  {letters}");
    }
}

internal sealed record Library(Disk Disk, Partition Part, string Relative)
{
    public string FileSystem => Part.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
        ? "ntfs3" : "exfat";
}

internal static class WindowsStorage
{
    private const string Inventory = """
        $ErrorActionPreference='Stop'
        $pages=@(Get-CimInstance Win32_PageFileUsage | ForEach-Object { [string]$_.Name })
        $wslPaths=@(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss\*' -ErrorAction SilentlyContinue |
            ForEach-Object { [string]$_.BasePath -replace '^\\\\\?\\','' })
        $bitlocker=@{}
        if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) {
            foreach($v in @(Get-BitLockerVolume -ErrorAction SilentlyContinue)) {
                if($v.MountPoint) { $bitlocker[$v.MountPoint] = $v.VolumeStatus -ne 'FullyDecrypted' }
            }
        }
        $disks=@(Get-Disk | ForEach-Object {
            $d=$_
            $parts=@(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | ForEach-Object {
                $p=$_
                $vol=Get-Volume -Partition $p -ErrorAction SilentlyContinue
                $letter=[string]$p.DriveLetter
                $name=if($letter -match '^[A-Z]$') {$letter} else {''}
                $pageFile= @($pages | Where-Object { $name -and $_.StartsWith($name+':\', 'OrdinalIgnoreCase') }).Count -gt 0
                $wslData= @($wslPaths | Where-Object { $name -and $_.StartsWith($name+':\', 'OrdinalIgnoreCase') }).Count -gt 0
                @{
                    Letter=$name; Guid=[string]$p.Guid; FileSystem=[string]$vol.FileSystem
                    Access=@($p.AccessPaths | Where-Object { $_ } | ForEach-Object { [string]$_ })
                    Boot=[bool]$p.IsBoot; System=[bool]$p.IsSystem; PageFile=$pageFile
                    Encrypted=[bool]$bitlocker[$name+':']; WslData=$wslData
                }
            })
            @{
                Number=$d.Number; Name=[string]$d.FriendlyName
                Identity=([string]$d.UniqueId+'|'+[string]$d.SerialNumber).Trim('|')
                Path=[string]$d.Path; Bus=[string]$d.BusType; Size=$d.Size; Sector=$d.LogicalSectorSize
                Offline=[bool]$d.IsOffline; ReadOnly=[bool]$d.IsReadOnly
                Boot=[bool]$d.IsBoot; System=[bool]$d.IsSystem; Parts=$parts
            }
        })
        ConvertTo-Json -InputObject $disks -Depth 7 -Compress
        """;

    public static async Task<Disk[]> Disks() =>
        JsonSerializer.Deserialize<Disk[]>(await PowerShell(Inventory)) ?? [];

    public static async Task<Disk> Revalidate(Disk selected, string? library, string payload, string? mods)
    {
        var disks = await Disks();
        var disk = disks.SingleOrDefault(d => d.Number == selected.Number);
        if (disk is null || !disk.CanWrite || disk.Identity != selected.Identity ||
            disk.Path != selected.Path || disk.Size != selected.Size || disk.Bus != selected.Bus)
            throw new IOException(LocalText("The selected disk changed or is no longer available.",
                "O disco selecionado mudou ou já não pode ser utilizado."));
        foreach (var path in new[] { library, payload, mods, Environment.ProcessPath, Path.GetTempPath() })
        {
            if (string.IsNullOrEmpty(path)) continue;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (disk.Parts.Any(p => p.Letter.Length == 1 && (p.Letter + @":\").Equals(root, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(LocalText("The target disk contains a file or folder used by the installer.",
                    "O disco de destino contém um dos ficheiros ou pastas usados pela instalação."));
        }
        return disk;
    }

    public static Library FindLibrary(string folder, Disk[] disks)
    {
        string full = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(full)!;
        if (root.Length != 3 || root[1] != ':' || !Directory.Exists(full))
            throw new IOException(LocalText("Select an existing folder on a local drive with a drive letter.",
                "Seleciona uma pasta existente num disco local com letra de unidade."));
        CheckFolder(full);
        var (disk, part) = disks.SelectMany(d => d.Parts.Select(p => (d, p)))
            .FirstOrDefault(x => x.p.Letter.Equals(root[..1], StringComparison.OrdinalIgnoreCase));
        if (part is null || !Guid.TryParse(part.Guid, out var guid) || part.Encrypted ||
            part.FileSystem.ToUpperInvariant() is not ("NTFS" or "EXFAT"))
            throw new IOException(LocalText("The games folder needs an NTFS or exFAT GPT partition without BitLocker.",
                "A pasta de jogos precisa de estar numa partição GPT em NTFS ou exFAT, sem BitLocker."));
        return new Library(disk!, part with { Guid = guid.ToString("D") },
            Path.GetRelativePath(root, full) is "." ? "" : Path.GetRelativePath(root, full).Replace('\\', '/'));
    }

    public static void CheckFolder(string path)
    {
        for (var directory = new DirectoryInfo(path); directory.Parent != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(LocalText("Select the original folder. Shortcuts and junctions are unsupported.",
                    "Escolhe a pasta real em vez de um atalho ou junção."));
    }

    public static Task<string> Refresh() => PowerShell("Update-HostStorageCache -ErrorAction Stop");

    private static Task<string> PowerShell(string script)
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var code = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script;
        return ProcessRunner.Run(exe, ["-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(code))]);
    }
}

internal static class ProcessRunner
{
    public static async Task<string> Run(string exe, IEnumerable<string> args, Action<string>? log = null)
    {
        var start = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["WSL_UTF8"] = "1";
        using var process = Process.Start(start) ?? throw new IOException(LocalText("Could not start ", "Não foi possível iniciar ") + exe);
        var output = new StringBuilder();
        var errors = new StringBuilder();
        async Task Read(StreamReader reader, StringBuilder result)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                result.AppendLine(line);
                log?.Invoke(line);
            }
        }
        await Task.WhenAll(Read(process.StandardOutput, output), Read(process.StandardError, errors),
            process.WaitForExitAsync()).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException(LocalText($"{Path.GetFileName(exe)} failed ({process.ExitCode}). ",
                $"Falhou {Path.GetFileName(exe)} ({process.ExitCode}). ") +
                string.Join(" ", (errors.Length > 0 ? errors : output).ToString().Trim().Split('\n').TakeLast(4)));
        return output.ToString().Trim();
    }
}

internal sealed record DiskImage(long Capacity, long FrontBytes, long BackupOffset)
{
    public const int BackupSize = 33 * 512;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static DiskImage ReadPlan(string folder, long capacity)
    {
        var image = JsonSerializer.Deserialize<DiskImage>(File.ReadAllText(Path.Combine(folder, "image.json")), Json)
            ?? throw new InvalidDataException(LocalText("The image has no disk write plan.", "A imagem não tem um plano de gravação."));
        if (image.Capacity != capacity || image.FrontBytes <= 0 || image.FrontBytes % 512 != 0 ||
            image.FrontBytes > image.BackupOffset || image.BackupOffset != capacity - BackupSize)
            throw new InvalidDataException(LocalText("The image layout does not match the disk.", "O plano da imagem não corresponde ao disco."));
        CheckHash(Path.Combine(folder, "front.img.gz"), Path.Combine(folder, "image.sha256"));
        CheckHash(Path.Combine(folder, "backup.gpt"), Path.Combine(folder, "image.sha256"));
        if (new FileInfo(Path.Combine(folder, "backup.gpt")).Length != BackupSize)
            throw new InvalidDataException(LocalText("The backup GPT is incomplete.", "A cópia de segurança da tabela GPT está incompleta."));
        return image;
    }

    public static void CheckHash(string path, string manifest)
    {
        var expected = File.ReadLines(manifest).Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .SingleOrDefault(l => l.Length == 2 && l[1].TrimStart('*') == Path.GetFileName(path));
        using var file = File.OpenRead(path);
        if (expected == null || !Convert.ToHexString(SHA256.HashData(file)).Equals(expected[0], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(LocalText($"{Path.GetFileName(path)} failed SHA-256 verification.",
                $"O ficheiro {Path.GetFileName(path)} não passou a verificação SHA-256."));
    }

    public void Apply(Stream destination, string folder, bool verify, Action<int>? report)
    {
        using var source = new GZipStream(File.OpenRead(Path.Combine(folder, "front.img.gz")), CompressionMode.Decompress);
        var buffer = new byte[4 * 1024 * 1024];
        var readback = verify ? new byte[buffer.Length] : null;
        destination.Seek(0, SeekOrigin.Begin);
        for (long done = 0; done < FrontBytes;)
        {
            int count = (int)Math.Min(buffer.Length, FrontBytes - done);
            source.ReadExactly(buffer.AsSpan(0, count));
            if (readback is null)
                destination.Write(buffer, 0, count);
            else
            {
                destination.ReadExactly(readback.AsSpan(0, count));
                if (!buffer.AsSpan(0, count).SequenceEqual(readback.AsSpan(0, count)))
                    throw new IOException(LocalText($"Disk verification differed at byte {done}.",
                        $"A leitura do disco divergiu a partir do byte {done}."));
            }
            done += count;
            report?.Invoke((int)(done * 100 / FrontBytes));
        }
        if (source.ReadByte() != -1)
            throw new InvalidDataException(LocalText("The image exceeds the expected size.", "A imagem excede o tamanho esperado."));
        byte[] backup = File.ReadAllBytes(Path.Combine(folder, "backup.gpt"));
        destination.Seek(BackupOffset, SeekOrigin.Begin);
        if (verify)
        {
            destination.ReadExactly(buffer.AsSpan(0, backup.Length));
            if (!backup.AsSpan().SequenceEqual(buffer.AsSpan(0, backup.Length)))
                throw new IOException(LocalText("The GPT on disk does not match the image.", "A cópia da tabela GPT no disco não corresponde à imagem."));
        }
        else
            destination.Write(backup);
    }
}

internal static partial class PhysicalWriter
{
    private const uint ReadWrite = 0xC0000000;
    private const uint LockVolume = 0x00090018, DismountVolume = 0x00090020;
    private const uint GetLength = 0x0007405C, GetDeviceNumber = 0x002D1080, RefreshLayout = 0x00070140;

    public static void Write(Disk disk, DiskImage image, string folder, Action<int, bool> progress)
    {
        var locks = new List<SafeFileHandle>();
        string device = disk.Path;
        try
        {
            foreach (var part in disk.Parts)
            {
                string? name = part.Access.FirstOrDefault(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
                if (name != null) name = @"\\.\" + name[4..].TrimEnd('\\');
                else if (part.Letter.Length == 1) name = @"\\.\" + part.Letter + ":";
                if (name == null)
                {
                    if (part.FileSystem.Length != 0 || part.Access.Length != 0)
                        throw new IOException(LocalText("Windows did not grant direct access to one of the partitions.",
                            "O Windows não forneceu um acesso direto a uma das partições."));
                    continue;
                }
                var handle = Open(name);
                locks.Add(handle);
                Control(handle, LockVolume, "Bloquear " + name);
                Control(handle, DismountVolume, "Desmontar " + name);
            }
            using var raw = Open(device, writeThrough: true);
            byte[] number = new byte[12];
            if (!DeviceIoControl(raw, GetDeviceNumber, IntPtr.Zero, 0, number, 12, out uint numberBytes, IntPtr.Zero) ||
                numberBytes != 12 || BitConverter.ToUInt32(number, 4) != disk.Number)
                throw new IOException(LocalText("Windows returned a different number for the selected disk.",
                    "O Windows devolveu um número diferente para o disco selecionado."));
            byte[] length = new byte[8];
            if (!DeviceIoControl(raw, GetLength, IntPtr.Zero, 0, length, 8, out uint received, IntPtr.Zero) ||
                received != 8 || BitConverter.ToInt64(length) != disk.Size)
                throw new IOException(LocalText("The opened device capacity does not match the selected disk.",
                    "A capacidade do dispositivo aberto não corresponde ao disco selecionado."));
            using var stream = new FileStream(raw, FileAccess.ReadWrite, 1, false);
            image.Apply(stream, folder, false, p => progress(p, false));
            stream.Flush(flushToDisk: true);
            image.Apply(stream, folder, true, p => progress(p, true));
        }
        finally
        {
            foreach (var handle in locks) handle.Dispose();
        }
        using var diskHandle = Open(device);
        _ = DeviceIoControl(diskHandle, RefreshLayout, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero);
    }

    private static SafeFileHandle Open(string path, bool writeThrough = false)
    {
        var handle = CreateFile(path, ReadWrite, 3, IntPtr.Zero, 3, writeThrough ? 0x80000000u : 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, LocalText("Could not open ", "Não foi possível abrir ") + path);
    }

    private static void Control(SafeFileHandle handle, uint code, string operation)
    {
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), operation + LocalText(
                " failed. Close applications that are using this disk.", " falhou. Fecha aplicações que estejam a usar este disco."));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input,
        uint inSize, [Out] byte[]? output, uint outSize, out uint bytes, IntPtr overlapped);
}
