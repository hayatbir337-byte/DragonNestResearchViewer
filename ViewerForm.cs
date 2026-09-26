using DragonNestResearchViewer.Core;
using DragonNestResearchViewer.Rendering;

namespace DragonNestResearchViewer;

public sealed class ViewerForm : Form
{
    readonly TreeView _resources = new() { Dock=DockStyle.Fill, HideSelection=false };
    readonly ListBox _animations = new() { Dock=DockStyle.Fill };
    readonly TextBox _inspector = new() { Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, Font=new Font("Consolas",9) };
    readonly GlViewport _viewport = new() { Dock=DockStyle.Fill };
    readonly TrackBar _timeline = new() { Dock=DockStyle.Fill, Minimum=0, Maximum=1, TickStyle=TickStyle.None };
    readonly Label _frameLabel = new() { AutoSize=true, Text="Frame: 0 / 0", Padding=new Padding(8,8,8,0) };
    readonly Button _play = new() { Text="▶ Play", AutoSize=true };
    readonly Button _pause = new() { Text="Ⅱ Pause", AutoSize=true };
    readonly Button _stop = new() { Text="■ Stop", AutoSize=true };
    readonly CheckBox _loop = new() { Text="Loop", Checked=true, AutoSize=true, Padding=new Padding(8,7,8,0) };
    readonly CheckBox _skeleton = new() { Text="Skeleton", Checked=false, AutoSize=true, Padding=new Padding(8,7,8,0) };\n    readonly Button _fit = new() { Text="Modeli Ortala", AutoSize=true };
    readonly CheckBox _grid = new() { Text="Grid", Checked=true, AutoSize=true, Padding=new Padding(8,7,8,0) };
    readonly CheckBox _wire = new() { Text="Wireframe", AutoSize=true, Padding=new Padding(8,7,8,0) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval=16 };
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
    const float Fps=60f;

    public ViewerForm()
    {
        Text="Dragon Nest Research Viewer V1";
        Width=1500; Height=900; MinimumSize=new Size(1000,650);
        BackColor=Color.FromArgb(32,35,42);

        var menu=new MenuStrip();
        var file=new ToolStripMenuItem("Dosya");
        file.DropDownItems.Add("Resource Klasörü Seç...",null,(_,_)=>ChooseRoot());
        file.DropDownItems.Add("Çıkış",null,(_,_)=>Close());
        menu.Items.Add(file);

        var main=new SplitContainer{Dock=DockStyle.Fill,SplitterDistance=300};
        var right=new SplitContainer{Dock=DockStyle.Fill,SplitterDistance=900};
        right.Panel1.Controls.Add(_viewport);
        right.Panel2.Controls.Add(_inspector);
        main.Panel2.Controls.Add(right);

        var left=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=580};
        left.Panel1.Controls.Add(_resources);
        left.Panel2.Controls.Add(_animations);
        main.Panel1.Controls.Add(left);

        var controls=new FlowLayoutPanel{Dock=DockStyle.Top,Height=40,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Padding=new Padding(4)};
        controls.Controls.AddRange([_play,_pause,_stop,_fit,_loop,_skeleton,_grid,_wire,_frameLabel]);

        var timelinePanel=new Panel{Dock=DockStyle.Bottom,Height=82};
        timelinePanel.Controls.Add(_timeline);
        timelinePanel.Controls.Add(controls);
        _timeline.Top=42; _timeline.Height=36;

        var status=new StatusStrip();status.Items.Add(_status);

        Controls.Add(main);Controls.Add(timelinePanel);Controls.Add(status);Controls.Add(menu);
        MainMenuStrip=menu;

        _resources.BeforeExpand+=ResourcesBeforeExpand;
        _resources.AfterSelect+=ResourcesAfterSelect;
        _animations.SelectedIndexChanged+=AnimationsSelectedIndexChanged;
        _timeline.Scroll+=(_,_)=>SetFrame(_timeline.Value);
        _play.Click+=(_,_)=>StartPlayback();\n        _fit.Click+=(_,_)=>_viewport.FitToModel();
        _pause.Click+=(_,_)=>_playing=false;
        _stop.Click+=(_,_)=>{_playing=false;_viewport.UseAnimation=false;SetFrame(0);};
        _skeleton.CheckedChanged+=(_,_)=>{_viewport.ShowSkeleton=_skeleton.Checked;_viewport.Invalidate();};
        _grid.CheckedChanged+=(_,_)=>_viewport.ShowGrid=_grid.Checked;
        _wire.CheckedChanged+=(_,_)=>_viewport.Wireframe=_wire.Checked;
        _timer.Tick+=TimerTick;
        _timer.Start();
    }

    void ChooseRoot()
    {
        using var d=new FolderBrowserDialog{Description="PROJECT-DUCK-UNPACKED\\extracted\\resource klasörünü seçin",UseDescriptionForTitle=true,ShowNewFolderButton=false};
        if(d.ShowDialog(this)!=DialogResult.OK)return;
        LoadRoot(d.SelectedPath);
    }

    void LoadRoot(string path)
    {
        _root=path;_resources.Nodes.Clear();
        var root=new TreeNode(new DirectoryInfo(path).Name){Tag=path};
        root.Nodes.Add(new TreeNode("yükleniyor..."));
        _resources.Nodes.Add(root);root.Expand();
        _status.Text=path;
    }

    static readonly HashSet<string> Extensions=new(StringComparer.OrdinalIgnoreCase)
    { ".skn",".msh",".dds",".ani",".anim",".act",".eff",".ptc" };

    void ResourcesBeforeExpand(object? s,TreeViewCancelEventArgs e)
    {
        if(e.Node?.Tag is not string path || !Directory.Exists(path))return;
        if(e.Node.Nodes.Count==1 && e.Node.Nodes[0].Tag is null && e.Node.Nodes[0].Text=="yükleniyor...")
        {
            e.Node.Nodes.Clear();
            try
            {
                foreach(var dir in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName))
                {
                    var n=new TreeNode(Path.GetFileName(dir)){Tag=dir,ImageKey="folder"};
                    n.Nodes.Add(new TreeNode("yükleniyor..."));e.Node.Nodes.Add(n);
                }
                foreach(var f in Directory.EnumerateFiles(path).Where(f=>Extensions.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName))
                    e.Node.Nodes.Add(new TreeNode(Path.GetFileName(f)){Tag=f});
            }
            catch(Exception ex){_status.Text=ex.Message;}
        }
    }

    void ResourcesAfterSelect(object? s,TreeViewEventArgs e)
    {
        if(e.Node?.Tag is not string path || Directory.Exists(path))return;
        try
        {
            switch(Path.GetExtension(path).ToLowerInvariant())
            {
                case ".skn": LoadSkn(path); break;
                case ".msh": LoadMsh(path,null); break;
                case ".ani":
                case ".anim": LoadAni(path); break;
                case ".dds": InspectDds(path); break;
                default: _inspector.Text=$"{Path.GetFileName(path)}\r\n\r\nV1: dosya listeleniyor; parser V2 kapsamına bırakıldı."; break;
            }
        }
        catch(Exception ex){_status.Text="Hata: "+ex.Message;_inspector.Text=ex.ToString();}
    }

    void LoadSkn(string path)
    {
        _status.Text="SKN okunuyor...";
        var skin=DnParsers.LoadSkn(path);
        string dir=Path.GetDirectoryName(path)!;
        string msh=Path.Combine(dir,skin.MeshFile);
        if(!File.Exists(msh)) msh=Path.Combine(dir,Path.GetFileNameWithoutExtension(path)+".msh");
        if(!File.Exists(msh)) throw new FileNotFoundException("SKN'nin bağlı MSH dosyası bulunamadı.",msh);
        _skin=skin;
        LoadMsh(msh,skin);
        _loadedModelPath=path;

        var autoAni=Path.Combine(dir,Path.GetFileNameWithoutExtension(path)+".ani");
        if(File.Exists(autoAni)) LoadAni(autoAni,false);

        _inspector.Text=$"SKN: {Path.GetFileName(path)}\r\nVersion: {skin.Version}\r\nMSH: {Path.GetFileName(msh)}\r\nMaterials: {skin.Materials.Count}\r\n\r\n"+
            string.Join("\r\n",skin.Materials.Select((m,i)=>$"[{i}] {m.Name} | {m.Effect} | DDS={m.DiffuseTexture ?? "(yok)"}"));
    }

    void LoadMsh(string path,DnSkin? skin)
    {
        _status.Text="MSH okunuyor...";
        _mesh=DnParsers.LoadMsh(path);
        if(skin==null)_skin=null;
        _viewport.SetModel(_mesh,skin,Path.GetDirectoryName(path)!);
        _loadedModelPath=path;
        _status.Text=$"Model yüklendi: {_mesh.Parts.Count} mesh, {_mesh.Bones.Count} bone | {_viewport.TextureSummary()}";
        _inspector.Text=$"MSH: {Path.GetFileName(path)}\r\nVersion: {_mesh.Version}\r\nMeshes: {_mesh.Parts.Count}\r\nBones: {_mesh.Bones.Count}\r\nBounds: {_mesh.BoundsMin} -> {_mesh.BoundsMax}\r\n\r\n"+
            string.Join("\r\n",_mesh.Parts.Select((p,i)=>$"[{i}] {p.Name} | Vert={p.Vertices.Length} | Tri={p.Indices.Length/3} | Rig={p.RigNames.Length}"));
    }

    void LoadAni(string path,bool updateInspector=true)
    {
        if(_mesh==null)throw new InvalidOperationException("Önce bir SKN/MSH model yükleyin.");
        _status.Text="ANI okunuyor...";
        _ani=DnParsers.LoadAni(path);
        _viewport.SetAnimation(_ani);\n        _viewport.UseAnimation=false;
        _animations.Items.Clear();
        foreach(var n in _ani.Names)_animations.Items.Add(n);
        if(_animations.Items.Count>0)_animations.SelectedIndex=0;
        if(updateInspector)_inspector.Text=$"Animation: {Path.GetFileName(path)}\r\nVersion: {_ani.Version}\r\nBones: {_ani.Bones.Count}\r\nAnimations: {_ani.Names.Count}\r\n\r\n"+
            string.Join("\r\n",_ani.Names.Select((n,i)=>$"[{i}] {n} | Frames={_ani.FrameCounts.ElementAtOrDefault(i)}"));
        _status.Text=$"Animasyon yüklendi: {_ani.Names.Count} clip";
    }

    void InspectDds(string path)
    {
        using var img=Pfim.Pfimage.FromFile(path);
        _inspector.Text=$"DDS: {Path.GetFileName(path)}\r\n{img.Width} x {img.Height}\r\nFormat: {img.Format}\r\nBitsPerPixel: {img.BitsPerPixel}\r\nMipMaps: {img.MipMaps.Length}";
    }

    void AnimationsSelectedIndexChanged(object? s,EventArgs e)
    {
        if(_ani==null||_animations.SelectedIndex<0)return;
        int i=_animations.SelectedIndex;
        _viewport.AnimationIndex=i;
        int frames=Math.Max(1,_ani.FrameCounts.ElementAtOrDefault(i));
        _timeline.Maximum=Math.Max(0,frames-1);
        SetFrame(0);
    }

    void StartPlayback()
    {
        if(_ani==null||_animations.SelectedIndex<0)return;
        _playing=true;_clock.Restart();_lastSeconds=0;
    }

    void TimerTick(object? s,EventArgs e)
    {
        if(!_playing||_ani==null||_animations.SelectedIndex<0)return;
        double now=_clock.Elapsed.TotalSeconds;double dt=now-_lastSeconds;_lastSeconds=now;
        _currentFrame+=(float)(dt*Fps);
        int max=_timeline.Maximum;
        if(_currentFrame>max)
        {
            if(_loop.Checked)_currentFrame=max>0?_currentFrame%(max+1):0;
            else{_currentFrame=max;_playing=false;}
        }
        int f=Math.Clamp((int)_currentFrame,0,max);
        if(_timeline.Value!=f)_timeline.Value=f;
        _viewport.Frame=_currentFrame;
        _frameLabel.Text=$"Frame: {f} / {max}";
    }

    void SetFrame(float frame)
    {
        _currentFrame=frame;_viewport.Frame=frame;
        int f=Math.Clamp((int)frame,_timeline.Minimum,_timeline.Maximum);
        if(_timeline.Value!=f)_timeline.Value=f;
        _frameLabel.Text=$"Frame: {f} / {_timeline.Maximum}";
    }
}