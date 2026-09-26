using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using System.Runtime.InteropServices;
using DragonNestResearchViewer.Core;
using DragonNestResearchViewer.Rendering;
using Pfim;

namespace DragonNestResearchViewer;

public sealed class ViewerForm : Form
{
    readonly TreeView _resources = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly ListBox _animations = new() { Dock = DockStyle.Fill };
    readonly TextBox _inspector = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 9)
    };
    readonly PictureBox _ddsPreview = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(25, 28, 34)
    };
    readonly TabControl _rightTabs = new() { Dock = DockStyle.Fill };
    readonly GlViewport _viewport = new() { Dock = DockStyle.Fill };
    readonly TrackBar _timeline = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, TickStyle = TickStyle.None };
    readonly Label _frameLabel = new() { AutoSize = true, Text = "Frame: 0 / 0", Padding = new Padding(8, 8, 8, 0) };
    readonly Button _play = new() { Text = "▶ Play", AutoSize = true };
    readonly Button _pause = new() { Text = "Ⅱ Pause", AutoSize = true };
    readonly Button _stop = new() { Text = "■ Stop", AutoSize = true };
    readonly Button _fit = new() { Text = "Modeli Ortala", AutoSize = true };
    readonly CheckBox _loop = new() { Text = "Loop", Checked = true, AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    readonly CheckBox _skeleton = new() { Text = "Skeleton", Checked = false, AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    readonly CheckBox _grid = new() { Text = "Grid", Checked = true, AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    readonly CheckBox _wire = new() { Text = "Wireframe", AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    readonly ToolStripStatusLabel _status = new("Hazır");

    string? _root;
    DnMesh? _mesh;
    DnSkin? _skin;
    DnAnimationSet? _ani;
    string? _loadedModelPath;
    bool _playing;
    readonly System.Diagnostics.Stopwatch _clock = new();
    double _lastSeconds;
    float _currentFrame;
    const float Fps = 60f;

    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".skn", ".msh", ".dds", ".ani", ".anim", ".act", ".eff", ".ptc"
    };

    public ViewerForm()
    {
        Text = "Dragon Nest Research Viewer V1.2";
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1050, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(32, 35, 42);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("Dosya");
        file.DropDownItems.Add("Resource Klasörü Seç...", null, (_, _) => ChooseRoot());
        file.DropDownItems.Add("Çıkış", null, (_, _) => Close());
        menu.Items.Add(file);

        var infoTab = new TabPage("Bilgi");
        infoTab.Controls.Add(_inspector);
        var ddsTab = new TabPage("DDS Önizleme");
        ddsTab.Controls.Add(_ddsPreview);
        _rightTabs.TabPages.Add(infoTab);
        _rightTabs.TabPages.Add(ddsTab);

        var main = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 320
        };

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 900
        };
        right.Panel1.Controls.Add(_viewport);
        right.Panel2.Controls.Add(_rightTabs);
        main.Panel2.Controls.Add(right);

        var left = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 580
        };
        left.Panel1.Controls.Add(_resources);
        left.Panel2.Controls.Add(_animations);
        main.Panel1.Controls.Add(left);

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4)
        };
        controls.Controls.AddRange([_play, _pause, _stop, _fit, _loop, _skeleton, _grid, _wire, _frameLabel]);

        var timelinePanel = new Panel { Dock = DockStyle.Bottom, Height = 82 };
        timelinePanel.Controls.Add(_timeline);
        timelinePanel.Controls.Add(controls);
        _timeline.Top = 42;
        _timeline.Height = 36;

        var status = new StatusStrip();
        status.Items.Add(_status);

        Controls.Add(main);
        Controls.Add(timelinePanel);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _resources.BeforeExpand += ResourcesBeforeExpand;
        _resources.AfterSelect += ResourcesAfterSelect;
        _animations.SelectedIndexChanged += AnimationsSelectedIndexChanged;
        _timeline.Scroll += (_, _) =>
        {
            _viewport.UseAnimation = true;
            SetFrame(_timeline.Value);
        };

        _play.Click += (_, _) => StartPlayback();
        _pause.Click += (_, _) => _playing = false;
        _stop.Click += (_, _) =>
        {
            _playing = false;
            _viewport.UseAnimation = false;
            ResetFrameUi();
        };
        _fit.Click += (_, _) => _viewport.FitToModel();

        _skeleton.CheckedChanged += (_, _) =>
        {
            _viewport.ShowSkeleton = _skeleton.Checked;
            _viewport.Invalidate();
        };
        _grid.CheckedChanged += (_, _) =>
        {
            _viewport.ShowGrid = _grid.Checked;
            _viewport.Invalidate();
        };
        _wire.CheckedChanged += (_, _) =>
        {
            _viewport.Wireframe = _wire.Checked;
            _viewport.Invalidate();
        };

        _timer.Tick += TimerTick;
        _timer.Start();

        Shown += (_, _) => _status.Text = "Dosya > Resource Klasörü Seç ile başlayın.";
    }

    void ChooseRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "PROJECT-DUCK-UNPACKED\\extracted\\resource klasörünü seçin",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadRoot(dialog.SelectedPath);
    }

    void LoadRoot(string path)
    {
        _root = path;
        _resources.Nodes.Clear();

        var root = new TreeNode(new DirectoryInfo(path).Name) { Tag = path };
        root.Nodes.Add(new TreeNode("yükleniyor..."));
        _resources.Nodes.Add(root);
        root.Expand();

        _status.Text = $"Resource: {path}";
    }

    void ResourcesBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is not string path || !Directory.Exists(path))
            return;

        if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag is not null || e.Node.Nodes[0].Text != "yükleniyor...")
            return;

        e.Node.Nodes.Clear();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName))
            {
                var node = new TreeNode(Path.GetFileName(dir)) { Tag = dir };
                node.Nodes.Add(new TreeNode("yükleniyor..."));
                e.Node.Nodes.Add(node);
            }

            foreach (var file in Directory.EnumerateFiles(path)
                                          .Where(f => Extensions.Contains(Path.GetExtension(f)))
                                          .OrderBy(Path.GetFileName))
            {
                e.Node.Nodes.Add(new TreeNode(Path.GetFileName(file)) { Tag = file });
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Klasör okunamadı: " + ex.Message;
        }
    }

    void ResourcesAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is not string path || Directory.Exists(path))
            return;

        try
        {
            _playing = false;

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".skn":
                    LoadSkn(path);
                    break;
                case ".msh":
                    LoadMshSmart(path);
                    break;
                case ".ani":
                case ".anim":
                    LoadAniSmart(path);
                    break;
                case ".dds":
                    InspectDds(path);
                    break;
                default:
                    _playing = false;
                    _viewport.ClearScene();
                    _animations.Items.Clear();
                    ResetFrameUi();
                    ShowInfo($"{Path.GetFileName(path)}\r\n\r\nBu format henüz 3D görüntüleme kapsamında değil.\r\nViewport temizlendi; önceki model ekranda bırakılmaz.");
                    _status.Text = Path.GetFileName(path);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowInfo(ex.ToString());
            _status.Text = "Hata: " + ex.Message;
        }
    }

    void LoadSkn(string path, bool autoAnimation = true)
    {
        _status.Text = "SKN okunuyor...";

        var skin = DnParsers.LoadSkn(path);
        string dir = Path.GetDirectoryName(path)!;

        string msh = Path.Combine(dir, skin.MeshFile);
        if (!File.Exists(msh))
            msh = Path.Combine(dir, Path.GetFileName(skin.MeshFile));

        if (!File.Exists(msh))
            msh = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".msh");

        if (!File.Exists(msh))
            throw new FileNotFoundException("SKN'nin bağlı MSH dosyası bulunamadı.", msh);

        _skin = skin;
        LoadMshInternal(msh, skin);
        _loadedModelPath = path;

        ShowInfo(
            $"SKN: {Path.GetFileName(path)}\r\n" +
            $"Version: {skin.Version}\r\n" +
            $"MSH: {Path.GetFileName(msh)}\r\n" +
            $"Materials: {skin.Materials.Count}\r\n" +
            $"{_viewport.TextureSummary()}\r\n\r\n" +
            string.Join("\r\n", skin.Materials.Select((m, i) =>
                $"[{i}] {m.Name} | {m.Effect} | DDS={m.DiffuseTexture ?? "(yok)"}")));

        if (!autoAnimation)
            return;

        string ani = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".ani");
        if (File.Exists(ani))
            LoadAniInternal(ani, updateInspector: false);
    }

    void LoadMshSmart(string path)
    {
        DnSkin? skin = TryFindSkinForMesh(path);
        _skin = skin;
        LoadMshInternal(path, skin);

        var m = _mesh!;
        ShowInfo(
            $"MSH: {Path.GetFileName(path)}\r\n" +
            $"Version: {m.Version}\r\n" +
            $"Meshes: {m.Parts.Count}\r\n" +
            $"Bones: {m.Bones.Count}\r\n" +
            $"Bounds: {m.BoundsMin} -> {m.BoundsMax}\r\n" +
            $"{_viewport.TextureSummary()}\r\n\r\n" +
            string.Join("\r\n", m.Parts.Select((p, i) =>
                $"[{i}] {p.Name} | Vert={p.Vertices.Length} | Tri={p.Indices.Length / 3} | Rig={p.RigNames.Length}")));
    }

    DnSkin? TryFindSkinForMesh(string mshPath)
    {
        string dir = Path.GetDirectoryName(mshPath)!;
        string sameName = Path.ChangeExtension(mshPath, ".skn");

        if (File.Exists(sameName))
        {
            try { return DnParsers.LoadSkn(sameName); }
            catch { }
        }

        foreach (var skn in Directory.EnumerateFiles(dir, "*.skn"))
        {
            try
            {
                var parsed = DnParsers.LoadSkn(skn);
                if (string.Equals(
                        Path.GetFileName(parsed.MeshFile),
                        Path.GetFileName(mshPath),
                        StringComparison.OrdinalIgnoreCase))
                    return parsed;
            }
            catch
            {
                // Keep searching. A broken/unsupported sibling SKN should not block MSH viewing.
            }
        }

        return null;
    }

    void LoadMshInternal(string path, DnSkin? skin)
    {
        _status.Text = "MSH okunuyor...";
        _mesh = DnParsers.LoadMsh(path);
        _ani = null;
        _animations.Items.Clear();
        _viewport.SetModel(_mesh, skin, Path.GetDirectoryName(path)!);
        _loadedModelPath = path;
        ResetFrameUi();

        _status.Text =
            $"Model yüklendi: {_mesh.Parts.Count} mesh, {_mesh.Bones.Count} bone | {_viewport.TextureSummary()}";
    }

    void LoadAniSmart(string path)
    {
        EnsureCompanionModel(path);
        LoadAniInternal(path, updateInspector: true);
    }

    void EnsureCompanionModel(string animationPath)
    {
        string dir = Path.GetDirectoryName(animationPath)!;
        string baseName = Path.GetFileNameWithoutExtension(animationPath);

        if (_mesh != null &&
            _loadedModelPath != null &&
            string.Equals(Path.GetDirectoryName(_loadedModelPath), dir, StringComparison.OrdinalIgnoreCase))
            return;

        string skn = Path.Combine(dir, baseName + ".skn");
        if (File.Exists(skn))
        {
            LoadSkn(skn, autoAnimation: false);
            return;
        }

        string msh = Path.Combine(dir, baseName + ".msh");
        if (File.Exists(msh))
        {
            LoadMshSmart(msh);
            return;
        }

        // Fallback: use the only renderable model in the directory if it is unambiguous.
        var skins = Directory.EnumerateFiles(dir, "*.skn").Take(2).ToArray();
        if (skins.Length == 1)
        {
            LoadSkn(skins[0], autoAnimation: false);
            return;
        }

        var meshes = Directory.EnumerateFiles(dir, "*.msh").Take(2).ToArray();
        if (meshes.Length == 1)
        {
            LoadMshSmart(meshes[0]);
            return;
        }

        if (_mesh == null)
            throw new InvalidOperationException("Animasyon için eşleşen SKN/MSH bulunamadı. Aynı klasörde model dosyasını seçin.");
    }

    void LoadAniInternal(string path, bool updateInspector)
    {
        if (_mesh == null)
            throw new InvalidOperationException("Önce bir SKN/MSH model yükleyin.");

        _status.Text = "ANI okunuyor...";
        _ani = DnParsers.LoadAni(path);
        _viewport.SetAnimation(_ani);
        _viewport.UseAnimation = false;

        _animations.Items.Clear();
        foreach (var name in _ani.Names)
            _animations.Items.Add(name);

        if (_animations.Items.Count > 0)
            _animations.SelectedIndex = 0;
        else
            ResetFrameUi();

        if (updateInspector)
        {
            ShowInfo(
                $"Animation: {Path.GetFileName(path)}\r\n" +
                $"Version: {_ani.Version}\r\n" +
                $"Bones: {_ani.Bones.Count}\r\n" +
                $"Animations: {_ani.Names.Count}\r\n\r\n" +
                string.Join("\r\n", _ani.Names.Select((n, i) =>
                    $"[{i}] {n} | Frames={_ani.FrameCounts.ElementAtOrDefault(i)}")));
        }

        _status.Text = $"Animasyon yüklendi: {_ani.Names.Count} clip";
    }

    void InspectDds(string path)
    {
        _playing = false;
        _status.Text = "DDS okunuyor...";

        using var image = Pfimage.FromFile(path);
        var bitmap = PfimToBitmap(image);

        var old = _ddsPreview.Image;
        _ddsPreview.Image = bitmap;
        old?.Dispose();

        _rightTabs.SelectedIndex = 1;

        _inspector.Text =
            $"DDS: {Path.GetFileName(path)}\r\n" +
            $"{image.Width} x {image.Height}\r\n" +
            $"Format: {image.Format}\r\n" +
            $"BitsPerPixel: {image.BitsPerPixel}\r\n" +
            $"MipMaps: {image.MipMaps.Length}";

        _status.Text = $"DDS: {image.Width}x{image.Height} {image.Format}";
    }

    static Bitmap PfimToBitmap(IImage image)
    {
        DrawingPixelFormat format;
        int bytesPerPixel;

        switch (image.Format)
        {
            case Pfim.ImageFormat.Rgba32:
                format = DrawingPixelFormat.Format32bppArgb;
                bytesPerPixel = 4;
                break;
            case Pfim.ImageFormat.Rgb24:
                format = DrawingPixelFormat.Format24bppRgb;
                bytesPerPixel = 3;
                break;
            case Pfim.ImageFormat.Rgb8:
                var gray = new Bitmap(image.Width, image.Height, DrawingPixelFormat.Format24bppRgb);
                for (int y = 0; y < image.Height; y++)
                {
                    int srcRow = y * image.Stride;
                    for (int x = 0; x < image.Width; x++)
                    {
                        byte v = image.Data[srcRow + x];
                        gray.SetPixel(x, y, Color.FromArgb(v, v, v));
                    }
                }
                return gray;
            default:
                throw new NotSupportedException("DDS pixel formatı önizleme için desteklenmiyor: " + image.Format);
        }

        var bmp = new Bitmap(image.Width, image.Height, format);
        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, format);

        try
        {
            int rowBytes = image.Width * bytesPerPixel;
            for (int y = 0; y < image.Height; y++)
            {
                IntPtr dst = data.Scan0 + y * data.Stride;
                Marshal.Copy(image.Data, y * image.Stride, dst, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    void ShowInfo(string text)
    {
        _inspector.Text = text;
        _rightTabs.SelectedIndex = 0;
    }

    void AnimationsSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_ani == null || _animations.SelectedIndex < 0)
            return;

        int index = _animations.SelectedIndex;
        _viewport.AnimationIndex = index;
        _viewport.UseAnimation = false;

        int frames = Math.Max(1, _ani.FrameCounts.ElementAtOrDefault(index));
        _timeline.Maximum = Math.Max(0, frames - 1);
        ResetFrameUi();
    }

    void StartPlayback()
    {
        if (_ani == null || _animations.SelectedIndex < 0)
            return;

        _viewport.UseAnimation = true;
        _playing = true;
        _clock.Restart();
        _lastSeconds = 0;
    }

    void TimerTick(object? sender, EventArgs e)
    {
        if (!_playing || _ani == null || _animations.SelectedIndex < 0)
            return;

        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastSeconds;
        _lastSeconds = now;

        _currentFrame += (float)(dt * Fps);
        int max = _timeline.Maximum;

        if (_currentFrame > max)
        {
            if (_loop.Checked)
                _currentFrame = max > 0 ? _currentFrame % (max + 1) : 0;
            else
            {
                _currentFrame = max;
                _playing = false;
            }
        }

        int frame = Math.Clamp((int)_currentFrame, 0, max);
        if (_timeline.Value != frame)
            _timeline.Value = frame;

        _viewport.UseAnimation = true;
        _viewport.Frame = _currentFrame;
        _frameLabel.Text = $"Frame: {frame} / {max}";
    }

    void SetFrame(float frame)
    {
        _currentFrame = frame;
        _viewport.Frame = frame;

        int value = Math.Clamp((int)frame, _timeline.Minimum, _timeline.Maximum);
        if (_timeline.Value != value)
            _timeline.Value = value;

        _frameLabel.Text = $"Frame: {value} / {_timeline.Maximum}";
    }

    void ResetFrameUi()
    {
        _currentFrame = 0;
        _timeline.Value = _timeline.Minimum;
        _viewport.Frame = 0;
        _frameLabel.Text = $"Frame: 0 / {_timeline.Maximum}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _ddsPreview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
