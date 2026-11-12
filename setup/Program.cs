using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using static Jinra.Os.Setup.SetupLanguage;

namespace Jinra.Os.Setup;

internal static class SetupLanguage
{
    internal static bool Portuguese { get; set; }
    internal static string LocalText(string english, string portuguese) => Portuguese ? portuguese : english;
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupWindow());
    }
}

internal sealed record DisplayMode(uint Width, uint Height, uint Refresh, string Monitor)
{
    public override string ToString() => $"{Width} × {Height} · {Refresh} Hz";

    public static DisplayMode? Current()
    {
        var mode = new DeviceMode { Size = (ushort)Marshal.SizeOf<DeviceMode>() };
        var device = Screen.PrimaryScreen?.DeviceName;
        return EnumDisplaySettings(device, -1, ref mode) &&
            mode.Width is >= 320 and <= 16384 && mode.Height is >= 240 and <= 16384 &&
            mode.Frequency is >= 24 and <= 1000
            ? new DisplayMode(mode.Width, mode.Height, mode.Frequency, MonitorCode(device)) : null;
    }

    private static string MonitorCode(string? adapter)
    {
        if (adapter == null) return "";
        var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        if (!EnumDisplayDevices(adapter, 0, ref display, 1)) return "";
        var fields = (display.Id ?? "").Split('#');
        if (fields.Length < 4 || !fields[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return "";
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{fields[1]}\{fields[2]}\Device Parameters");
        return key?.GetValue("EDID") is byte[] edid && edid.Length >= 12
            ? Convert.ToHexStringLower(edid.AsSpan(8, 4)) : "";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, Flags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DeviceMode output);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int State;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice output, uint flags);
}

internal sealed class SetupWindow : Form
{
    private static readonly Size InitialClientSize = new(1120, 575);
    private static readonly Size MinimumWindowSize = new(820, 560);
    private static readonly Color Background = Color.FromArgb(13, 18, 33);
    private static readonly Color Surface = Color.FromArgb(20, 29, 46);
    private static readonly Color FieldBackground = Color.FromArgb(27, 40, 61);
    private static readonly Color Border = Color.FromArgb(42, 62, 88);
    private static readonly Color Ink = Color.FromArgb(234, 242, 252);
    private static readonly Color Muted = Color.FromArgb(167, 187, 210);
    private static readonly Color Blue = Color.FromArgb(11, 112, 195);
    private readonly ComboBox target = Selector(), gpu = Selector(), upscaler = Selector(),
        distro = Selector(), display = Selector(), language = Selector();
    private readonly CheckBox useExternal = new() { AutoSize = true };
    private readonly CheckBox importMods = new() { AutoSize = true };
    private readonly TextBox games = Field(), mods = Field(), payload = Field();
    private readonly NumericUpDown shared = new() { Minimum = 0, Maximum = 128, Value = 40, Width = 82,
        BackColor = FieldBackground, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button gamesBrowse = ActionButton(), modsBrowse = ActionButton();
    private readonly Button start = ActionButton(primary: true), refresh = ActionButton();
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 7 };
    private readonly TextBox log = Field();
    private readonly Label summary = Caption("");
    private readonly DisplayMode? windowsDisplay = DisplayMode.Current();
    private readonly List<(Control Control, string English, string Portuguese)> translations = [];
    private bool running;

    private sealed record UpscalerOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public SetupWindow()
    {
        Text = "Jinra OS Setup";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = MinimumWindowSize;
        ClientSize = InitialClientSize;
        StartPosition = FormStartPosition.CenterScreen;
        log.Multiline = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.Height = 132;
        language.Items.AddRange(["English", "Português"]);
        language.SelectedIndex = 0;
        language.AccessibleName = "Language / Idioma";
        Bind(useExternal, "Use a games folder on another disk", "Usar uma pasta de jogos noutro disco");
        Bind(importMods, "Copy mods from Jinra to Jinra OS", "Copiar os mods do Jinra para o Jinra OS");
        Bind(gamesBrowse, "Browse…", "Procurar…");
        Bind(modsBrowse, "Browse…", "Procurar…");
        Bind(start, "Prepare disk", "Preparar disco");
        Bind(refresh, "Refresh list", "Atualizar lista");
        Controls.Add(CreateLayout());

        gpu.Items.AddRange(["NVIDIA · Driver 595 Open", "AMD · Mesa RADV"]);
        gpu.SelectedIndex = 0;
        payload.Text = Path.Combine(AppContext.BaseDirectory, "jinra-os-payload.tar.zst");
        mods.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Jinra", "user", "load");
        importMods.Checked = Directory.Exists(mods.Text);

        gpu.SelectedIndexChanged += (_, _) => PopulateUpscalers();
        language.SelectedIndexChanged += (_, _) => ApplyLanguage();
        useExternal.CheckedChanged += (_, _) => {
            shared.Value = useExternal.Checked ? 2 : 40;
            UpdateSummary();
        };
        importMods.CheckedChanged += (_, _) => UpdateSummary();
        target.SelectedIndexChanged += (_, _) => UpdateSummary();
        distro.SelectedIndexChanged += (_, _) => UpdateSummary();
        shared.ValueChanged += (_, _) => UpdateSummary();
        gamesBrowse.Click += (_, _) => ChooseFolder(games);
        modsBrowse.Click += (_, _) => ChooseFolder(mods);
        refresh.Click += async (_, _) => await LoadEnvironment();
        start.Click += async (_, _) => await Prepare();
        Shown += async (_, _) => await LoadEnvironment();
        FormClosing += (_, e) => { if (running) e.Cancel = true; };
        ApplyLanguage();
    }

    protected override void OnLoad(EventArgs e)
    {
        var area = Screen.FromControl(this).WorkingArea;
        var frame = Size - ClientSize;
        MinimumSize = new Size(Math.Min(LogicalToDeviceUnits(MinimumWindowSize.Width), area.Width),
            Math.Min(LogicalToDeviceUnits(MinimumWindowSize.Height), area.Height));
        Size = new Size(Math.Min(LogicalToDeviceUnits(InitialClientSize.Width) + frame.Width, area.Width),
            Math.Min(LogicalToDeviceUnits(InitialClientSize.Height) + frame.Height, area.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        base.OnLoad(e);
    }

    private TableLayoutPanel CreateLayout()
    {
        var window = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(24, 18, 24, 18) };
        window.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        window.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        window.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        window.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3,
            Margin = new Padding(0, 0, 0, 16) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        using (var stream = typeof(SetupWindow).Assembly.GetManifestResourceStream("JinraLogo.png")!)
        using (var source = Image.FromStream(stream))
        {
            var logo = new PictureBox { Image = new Bitmap(source), SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(52, 52), Margin = new Padding(0), Anchor = AnchorStyles.Left };
            logo.Disposed += (_, _) => logo.Image?.Dispose();
            header.Controls.Add(logo, 0, 0);
        }
        var title = Stack();
        AddRow(title, new Label { Text = "Jinra OS Setup", AutoSize = true,
            Font = new Font("Segoe UI", 23, FontStyle.Bold), Margin = Padding.Empty });
        AddRow(title, Bind(Caption(""), "Prepare a disk that boots straight into Jinra Home.",
            "Prepara um disco para arrancar diretamente no Jinra Home."));
        header.Controls.Add(title, 1, 0);
        var languageBox = Stack();
        languageBox.Width = 145;
        languageBox.MinimumSize = new Size(145, 0);
        AddRow(languageBox, Bind(Caption(""), "Language", "Idioma"));
        AddRow(languageBox, language);
        header.Controls.Add(languageBox, 2, 0);
        window.Controls.Add(header, 0, 0);

        var viewport = new Panel { AutoScroll = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var left = Stack();
        left.Margin = new Padding(0, 0, 7, 0);
        AddRow(left, Card("Installation", "Instalação", Row("Target disk", "Disco de destino", target, refresh),
            Row("WSL 2 distribution", "Distribuição WSL 2", distro),
            Row("Jinra OS package", "Pacote Jinra OS", payload, ChoosePayload())));
        AddRow(left, Card("Library", "Biblioteca", useExternal, Row("Games folder", "Pasta de jogos", games, gamesBrowse),
            Row("Windows partition (GiB)", "Partição Windows (GiB)", shared), importMods,
            Row("Mods folder", "Pasta dos mods", mods, modsBrowse)));
        var right = Stack();
        right.Margin = new Padding(7, 0, 0, 0);
        AddRow(right, Card("Graphics", "Gráficos", Row("Graphics profile", "Perfil da gráfica", gpu),
            Row("Zelda upscaling", "Upscaling do Zelda", upscaler), Row("Display mode", "Modo do monitor", display)));
        AddRow(right, Card("Activity", "Atividade", log));
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);
        viewport.Controls.Add(columns);
        window.Controls.Add(viewport, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0, 12, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summary.Anchor = AnchorStyles.Left;
        start.Padding = new Padding(14, 3, 14, 3);
        footer.Controls.Add(summary, 0, 0);
        footer.Controls.Add(start, 1, 0);
        footer.Controls.Add(progress, 0, 1);
        footer.SetColumnSpan(progress, 2);
        window.Controls.Add(footer, 0, 2);
        return window;
    }

    private async Task LoadEnvironment()
    {
        refresh.Enabled = false;
        try
        {
            var disksTask = WindowsStorage.Disks();
            var wslTask = Wsl(["--list", "--quiet"]);
            await Task.WhenAll(disksTask, wslTask);
            var old = target.SelectedItem as Disk;
            target.Items.Clear();
            target.Items.AddRange(disksTask.Result.Where(d => d.CanWrite).Cast<object>().ToArray());
            if (old != null) target.SelectedItem = target.Items.Cast<Disk>()
                .FirstOrDefault(d => d.Identity == old.Identity && d.Path == old.Path);
            var previousDistro = distro.SelectedItem as string;
            distro.Items.Clear();
            distro.Items.AddRange(wslTask.Result.Replace("\0", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(n => !n.StartsWith("docker-", StringComparison.OrdinalIgnoreCase)).Cast<object>().ToArray());
            if (previousDistro != null && distro.Items.Contains(previousDistro)) distro.SelectedItem = previousDistro;
            else if (distro.Items.Count > 0) distro.SelectedIndex = 0;
        }
        catch (Exception e) { WriteLog(e.Message); }
        finally { refresh.Enabled = true; UpdateSummary(); }
    }

    private void ApplyLanguage()
    {
        Portuguese = language.SelectedIndex == 1;
        foreach (var (control, english, portuguese) in translations) control.Text = LocalText(english, portuguese);
        int displayIndex = display.SelectedIndex < 0 ? (windowsDisplay == null ? 0 : 1) : display.SelectedIndex;
        display.Items.Clear();
        display.Items.Add(LocalText("Automatic · Monitor's preferred mode", "Automático · Modo preferido do monitor"));
        if (windowsDisplay != null) display.Items.Add(LocalText("Same as Windows · ", "Igual ao Windows · ") + windowsDisplay);
        display.SelectedIndex = displayIndex;
        PopulateUpscalers((upscaler.SelectedItem as UpscalerOption)?.Id);
        target.Refresh();
        UpdateSummary();
    }

    private void PopulateUpscalers(string? selectedId = null)
    {
        var options = new List<UpscalerOption> { new("game", LocalText("Game default", "Do jogo")) };
        if (gpu.SelectedIndex == 0) {
            options.Add(new("dlss", "DLSS Quality (experimental)"));
            options.Add(new("dlaa", "DLAA (experimental)"));
        }
        options.Add(new("fsr31", "FSR 3.1"));
        int selected = options.FindIndex(option => option.Id == selectedId);
        upscaler.Items.Clear();
        upscaler.Items.AddRange(options.ToArray());
        upscaler.SelectedIndex = selected >= 0 ? selected : gpu.SelectedIndex == 0 ? 2 : 1;
    }

    private void UpdateSummary()
    {
        games.Enabled = gamesBrowse.Enabled = useExternal.Checked && !running;
        mods.Enabled = modsBrowse.Enabled = importMods.Checked && !running;
        shared.Minimum = useExternal.Checked ? 0 : 1;
        if (shared.Value is > 0 and < 1) shared.Value = 1;
        long required = (24834 + (long)shared.Value * 1024) * 1048576;
        summary.Text = LocalText($"Required space  {required / 1e9:0.#} GB  ·  The target disk will be erased.",
            $"Espaço necessário  {required / 1e9:0.#} GB  ·  O disco de destino será apagado.");
        start.Enabled = !running && target.SelectedItem is Disk disk && disk.Size >= required && distro.SelectedItem != null;
    }

    private async Task Prepare()
    {
        if (target.SelectedItem is not Disk chosen || distro.SelectedItem is not string distribution) return;
        var files = Path.GetFullPath(payload.Text.Trim());
        var libraryFolder = useExternal.Checked ? games.Text.Trim() : null;
        var modFolder = importMods.Checked ? mods.Text.Trim() : null;
        var settings = new {
            Profile = gpu.SelectedIndex == 0 ? "nvidia" : "amd",
            Upscaler = (upscaler.SelectedItem as UpscalerOption)?.Id ?? "game",
            Shared = (int)shared.Value * 1024,
            Display = display.SelectedIndex == 1 ? windowsDisplay : null
        };
        running = true;
        SetBusy(true);
        string? scratch = null;
        try
        {
            if (libraryFolder == "" || modFolder == "")
                throw new IOException(LocalText("Select a folder for each option you enabled.", "Seleciona as pastas que ativaste."));
            if (modFolder != null) {
                WindowsStorage.CheckFolder(modFolder);
                if (!Directory.Exists(modFolder))
                    throw new IOException(LocalText("The mods folder does not exist.", "A pasta de mods não existe."));
            }
            if (!File.Exists(files) || !File.Exists(files + ".sha256"))
                throw new IOException(LocalText("Select the Jinra OS package and keep its .sha256 file beside it.",
                    "Seleciona o pacote Jinra OS e mantém o ficheiro .sha256 ao lado."));
            string builder = Path.Combine(AppContext.BaseDirectory, "build-install-image.sh");
            if (!File.Exists(builder))
                throw new IOException(LocalText("Keep build-install-image.sh in the application folder.",
                    "O ficheiro build-install-image.sh deve estar na pasta da aplicação."));
            Disk disk = await WindowsStorage.Revalidate(chosen, libraryFolder, files, modFolder);
            Library? library = libraryFolder is null ? null : WindowsStorage.FindLibrary(libraryFolder, await WindowsStorage.Disks());
            if (library?.Disk.Number == disk.Number)
                throw new IOException(LocalText("The games folder must be on a different disk from the target.",
                    "A pasta dos jogos tem de estar num disco diferente do destino."));
            var devices = string.Join("\n", disk.Parts.Select(p => p.Letter.Length == 1 ? p.Letter + ":" : p.FileSystem).Where(s => s.Length > 0));
            if (MessageBox.Show(this, LocalText($"Erase all data on this disk?\n\n{disk}\n{devices}\n\nJinra OS will replace its partitions.",
                    $"Apagar todo o conteúdo deste disco?\n\n{disk}\n{devices}\n\nO Jinra OS substituirá as partições neste disco."),
                    LocalText("Confirm target disk", "Confirmar disco"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            progress.Style = ProgressBarStyle.Marquee;
            WriteLog(LocalText("Verifying the package and preparing the image.", "A verificar o pacote e a preparar a imagem."));
            await Task.Run(() => DiskImage.CheckHash(files, files + ".sha256"));
            scratch = Directory.CreateTempSubdirectory("jinra-os-").FullName;
            string request = Path.Combine(scratch, "request.json");
            string modsLinux = modFolder == null ? "" : await LinuxPath(distribution, Path.GetFullPath(modFolder));
            File.WriteAllText(request, JsonSerializer.Serialize(new {
                profile = settings.Profile, upscaler = settings.Upscaler, capacity = disk.Size,
                sharedMiB = settings.Shared, libraryPartition = library?.Part.Guid ?? "",
                libraryFilesystem = library?.FileSystem ?? "", libraryFolder = library?.Relative ?? "",
                width = settings.Display?.Width ?? 0, height = settings.Display?.Height ?? 0,
                refresh = settings.Display?.Refresh ?? 0, monitor = settings.Display?.Monitor ?? "",
                modsPath = modsLinux
            }, DiskImage.Json));
            await Wsl(["-d", distribution, "-u", "root", "--exec", "unshare", "--mount",
                "--propagation", "private", "bash", await LinuxPath(distribution, builder),
                await LinuxPath(distribution, files), await LinuxPath(distribution, request),
                await LinuxPath(distribution, scratch)], WriteLog);
            var image = await Task.Run(() => DiskImage.ReadPlan(scratch, disk.Size));
            disk = await WindowsStorage.Revalidate(disk, libraryFolder, files, modFolder);
            if (libraryFolder != null)
            {
                var current = WindowsStorage.FindLibrary(libraryFolder, await WindowsStorage.Disks());
                if (current.Part.Guid != library!.Part.Guid || current.Relative != library.Relative || current.FileSystem != library.FileSystem)
                    throw new IOException(LocalText("The games folder partition changed during preparation.",
                        "A partição da pasta de jogos mudou durante a preparação."));
            }
            progress.Style = ProgressBarStyle.Blocks;
            WriteLog(LocalText("Writing the disk and verifying the data.", "A gravar o disco e a verificar a leitura."));
            await Task.Run(() => PhysicalWriter.Write(disk, image, scratch, (p, verify) =>
                BeginInvoke(() => { progress.Value = (verify ? 50 : 0) + p / 2; })));
            try { await WindowsStorage.Refresh(); }
            catch (IOException error) { WriteLog(LocalText("Disk write verified. ", "A gravação foi verificada. ") + error.Message); }
            string success = LocalText("The disk is ready. Select it in the UEFI boot menu to open Jinra Home.",
                "O disco ficou preparado. Seleciona-o no menu de arranque UEFI para abrir o Jinra Home.");
            WriteLog(success);
            MessageBox.Show(this, success, LocalText("Jinra OS installed", "Jinra OS instalado"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            WriteLog(e.Message);
            MessageBox.Show(this, e.Message, LocalText("Installation interrupted", "Instalação interrompida"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (scratch != null)
            {
                try { Directory.Delete(scratch, recursive: true); }
                catch (IOException) { WriteLog(LocalText($"Temporary files at {scratch}", $"Ficheiros temporários em {scratch}")); }
            }
            progress.Style = ProgressBarStyle.Blocks;
            running = false;
            SetBusy(false);
            await LoadEnvironment();
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (Control control in new Control[] { target, gpu, upscaler, distro, display, language, useExternal,
            importMods, shared, refresh }) control.Enabled = !busy;
        UpdateSummary();
    }

    private void WriteLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => WriteLog(line)); return; }
        log.AppendText(line + Environment.NewLine);
    }

    private static Task<string> Wsl(string[] arguments, Action<string>? log = null) =>
        ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "wsl.exe"), arguments, log);

    private static Task<string> LinuxPath(string distroName, string file) =>
        Wsl(["-d", distroName, "--exec", "wslpath", "-a", "-u", file]);

    private static ComboBox Selector()
    {
        var selector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed, BackColor = FieldBackground, ForeColor = Ink };
        selector.FontChanged += (_, _) => selector.ItemHeight = selector.Font.Height + 7;
        selector.DrawItem += (_, e) => {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool editing = (e.State & DrawItemState.ComboBoxEdit) != 0;
            using var fill = new SolidBrush(selected && !editing ? Blue : FieldBackground);
            e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index < 0) return;
            var textBounds = Rectangle.Inflate(e.Bounds, -5, 0);
            TextRenderer.DrawText(e.Graphics, selector.GetItemText(selector.Items[e.Index]), e.Font!, textBounds,
                selector.Enabled ? Ink : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        };
        return selector;
    }
    private static TextBox Field() => new() { BackColor = FieldBackground, ForeColor = Ink,
        BorderStyle = BorderStyle.FixedSingle, ReadOnly = true };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true,
        ForeColor = Muted, Margin = new Padding(0, 3, 0, 3) };

    private T Bind<T>(T control, string english, string portuguese) where T : Control
    {
        translations.Add((control, english, portuguese));
        control.Text = LocalText(english, portuguese);
        return control;
    }

    private static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top,
            Margin = Padding.Empty };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    private static void AddRow(TableLayoutPanel stack, Control control)
    {
        int row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, row);
        control.Dock = DockStyle.Top;
    }

    private TableLayoutPanel Card(string english, string portuguese, params Control[] contents)
    {
        var card = Stack();
        card.BackColor = Surface;
        card.Padding = new Padding(16, 12, 16, 14);
        card.Margin = new Padding(0, 0, 0, 12);
        AddRow(card, Bind(new Label { AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 9) }, english, portuguese));
        foreach (var control in contents) AddRow(card, control);
        return card;
    }

    private static Button ActionButton(bool primary = false)
    {
        var button = new SetupButton { AutoSize = true, MinimumSize = new Size(0, primary ? 38 : 29),
            FlatStyle = FlatStyle.Flat, Margin = Padding.Empty, Anchor = AnchorStyles.Right,
            BackColor = primary ? Blue : FieldBackground, ForeColor = Ink };
        button.FlatAppearance.BorderColor = primary ? Blue : Border;
        return button;
    }

    private sealed class SetupButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) {
                base.OnPaint(e);
                return;
            }
            e.Graphics.Clear(FieldBackground);
            using var outline = new Pen(Border);
            e.Graphics.DrawRectangle(outline, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    private TableLayoutPanel Row(string english, string portuguese, Control value, Control? action = null)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 3, 0, 3) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var caption = Bind(Caption(""), english, portuguese);
        caption.Anchor = AnchorStyles.Left;
        row.Controls.Add(caption, 0, 0);
        value.Margin = new Padding(0, 0, action == null ? 0 : 6, 0);
        value.Anchor = value is NumericUpDown ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
        row.Controls.Add(value, 1, 0);
        if (action != null) row.Controls.Add(action, 2, 0);
        return row;
    }
    private Button ChoosePayload()
    {
        var button = Bind(ActionButton(), "Browse…", "Procurar…");
        button.Click += (_, _) => {
            if (running) return;
            using var dialog = new OpenFileDialog { Filter = LocalText("Jinra OS package|*.tar.zst", "Pacote do Jinra OS|*.tar.zst"),
                CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) payload.Text = dialog.FileName;
        };
        return button;
    }
    private void ChooseFolder(TextBox field)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = field.Text, ShowNewFolderButton = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
    }
}
