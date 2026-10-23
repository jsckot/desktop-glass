using DesktopGlass.Core;

var failed = 0;
var passed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
}
void True(bool value) { if (!value) throw new Exception("assertion failed"); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"expected {typeof(T).Name}");
}
var screenA = new DisplayArea("A", "内屏", 0, 0, 1536, 816, 1.25, true);
var screenB = new DisplayArea("B", "外屏", -2560, 0, 2560, 1400, 1, false);
Test("move has unique ownership and preserves source path", () => {
    var w = new Workspace(); var a = w.AddGroup("开发工具", "A"); var b = w.AddGroup("文件", "B");
    var path = "C:/用户/桌面/IDE.lnk"; w.MoveEntry(path, a.Id); w.MoveEntry(path, b.Id);
    Equal(0, a.Entries.Count); Equal(path, b.Entries.Single());
});
Test("reorder within group", () => {
    var w = new Workspace(); var g = w.AddGroup("工具", "A");
    w.MoveEntry("one", g.Id); w.MoveEntry("two", g.Id); w.MoveEntry("two", g.Id, 0);
    Equal("two,one", string.Join(',', g.Entries));
});
Test("remove entry leaves real file", () => {
    var root = Path.Combine(AppContext.BaseDirectory, "test-data"); Directory.CreateDirectory(root);
    var file = Path.Combine(root, Guid.NewGuid()+".txt"); File.WriteAllText(file, "keep");
    try { var w = new Workspace(); var g = w.AddGroup("文件", "A"); w.MoveEntry(file, g.Id); w.MoveEntry(file, null); True(File.Exists(file)); Equal(0, g.Entries.Count); }
    finally { File.Delete(file); }
});
Test("unknown group does not lose existing membership", () => {
    var w = new Workspace(); var g = w.AddGroup("工具", "A"); w.MoveEntry("one", g.Id);
    Throws<ArgumentException>(() => w.MoveEntry("one", "missing")); Equal("one", g.Entries.Single());
});
Test("disconnect fallback does not mutate preference and reconnect restores", () => {
    var g = new Group { DisplayId="B", X=800, Y=180, Width=400, Height=300 };
    var fallback = LayoutResolver.Resolve(g, [screenA]); True(fallback.Temporary); Equal(800d,g.X); Equal("B",g.DisplayId);
    var back = LayoutResolver.Resolve(g, [screenA,screenB]); True(!back.Temporary); Equal(-1760d, back.Left); Equal(180d,back.Top);
});
Test("work area shrinking clamps presentation only", () => {
    var g = new Group { DisplayId="A", X=1400, Y=700, Width=500, Height=400 };
    var p = LayoutResolver.Resolve(g,[screenA]); True(p.Left+p.Width<=1536); True(p.Top+p.Height<=816); Equal(1400d,g.X);
});
Test("mixed DPI converts origin and bounds consistently", () => {
    var d = new DisplayArea("B","外屏",1920,-200,2560,1400,1.5,false);
    var p = LayoutResolver.Resolve(new Group{DisplayId="B",X=40,Y=30,Width=400,Height=300},[d]);
    Equal(1980d,p.Left); Equal(-155d,p.Top); Equal(600d,p.Width);
});
Test("nonfinite geometry and no monitors rejected", () => {
    Throws<ArgumentException>(() => LayoutResolver.Resolve(new Group{X=double.NaN},[screenA]));
    Throws<ArgumentException>(() => LayoutResolver.Resolve(new Group(),[]));
});
Test("delete group makes references unclassified", () => {
    var w = new Workspace(); var g = w.AddGroup("文件","A"); w.MoveEntry("keep.txt",g.Id); w.RemoveGroup(g.Id); Equal(0,w.Groups.Count);
});
Test("store roundtrip and corrupt primary restores previous valid backup", () => {
    var dir = Path.Combine(AppContext.BaseDirectory,"test-data",Guid.NewGuid().ToString()); Directory.CreateDirectory(dir);
    try { var store = new WorkspaceStore(dir); var w = new Workspace(); w.AddGroup("第一份","A"); store.Save(w); w.AddGroup("第二份","B"); store.Save(w);
      Equal(2,store.Load().Groups.Count); File.WriteAllText(Path.Combine(dir,"workspace.json"),"{invalid");
      var recovered=store.Load(); Equal(1,recovered.Groups.Count); True(store.RecoveredFromBackup); True(File.ReadAllText(Path.Combine(dir,"workspace.json")).Contains("invalid"));
    } finally { Directory.Delete(dir,true); }
});
Test("both corrupt copies fail without overwrite", () => {
    var dir=Path.Combine(AppContext.BaseDirectory,"test-data",Guid.NewGuid().ToString()); Directory.CreateDirectory(dir);
    try {File.WriteAllText(Path.Combine(dir,"workspace.json"),"bad"); var store=new WorkspaceStore(dir); Throws<InvalidDataException>(()=>store.Load()); Equal("bad",File.ReadAllText(Path.Combine(dir,"workspace.json")));}
    finally {Directory.Delete(dir,true);}
});
Test("invalid persisted data rejected", () => {
    var w=new Workspace(); var g=w.AddGroup("工具","A"); g.Width=-20; Throws<InvalidDataException>(()=>w.Validate());
});
Test("canceled or system interrupted layout edits never commit",()=>{
    True(!LayoutEditPolicy.CanCommit(true,false,false,false,false));
    True(!LayoutEditPolicy.CanCommit(false,true,false,false,false));
    True(!LayoutEditPolicy.CanCommit(false,false,true,false,false));
    True(!LayoutEditPolicy.CanCommit(false,false,false,true,false));
    True(!LayoutEditPolicy.CanCommit(false,false,false,false,true));
    True(LayoutEditPolicy.CanCommit(false,false,false,false,false));
});

Test("v2 starts empty and only explicit add enables a group", () => {
    var w = new Workspace(); Equal(2, w.Version); Equal(0, w.Groups.Count);
    var g = w.AddGroup("工具", "A"); True(g.DesktopVisible); Equal(320d, g.Width); Equal(240d, g.Height);
});
Test("v1 upgrade pauses all groups while preserving content and layout", () => {
    var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try {
        var json = """{"Version":1,"Locked":true,"Groups":[{"Id":"old","Name":"旧分组","DisplayId":"B","X":123,"Y":456,"Width":350,"Height":300,"Tint":0.32,"Radius":24,"Entries":["C:/desktop/test.lnk"]}]}""";
        File.WriteAllText(Path.Combine(root,"workspace.json"), json);
        var store = new WorkspaceStore(root); var w = store.Load();
        Equal(2,w.Version); True(!w.Groups[0].DesktopVisible); Equal("B",w.Groups[0].DisplayId);
        Equal(123d,w.Groups[0].X); Equal("C:/desktop/test.lnk",w.Groups[0].Entries.Single()); True(w.Locked);
        store.Save(w); True(File.Exists(Path.Combine(root,"workspace.v1.backup.json")));
        Equal(json,File.ReadAllText(Path.Combine(root,"workspace.v1.backup.json")));
        w.Groups[0].DesktopVisible=true; store.Save(w);
        True(new WorkspaceStore(root).Load().Groups[0].DesktopVisible);
        Equal(json,File.ReadAllText(Path.Combine(root,"workspace.v1.backup.json")));
    } finally { Directory.Delete(root,true); }
});
Test("only enabled groups on connected displays can collect", () => {
    var w = new Workspace(); var a=w.AddGroup("当前","A"); a.Entries.Add("a");
    var b=w.AddGroup("缺屏","B"); b.Entries.Add("b");
    var c=w.AddGroup("隐藏","A"); c.DesktopVisible=false; c.Entries.Add("c");
    Equal("a",string.Join(",",WorkspaceVisibility.VisibleEntries(w,[screenA])));
    Equal("a,b",string.Join(",",WorkspaceVisibility.VisibleEntries(w,[screenA,screenB])));
    Equal("B",b.DisplayId);
});

Test("unknown primary version never falls back or overwrites older backup", () => {
    var root=Path.Combine(AppContext.BaseDirectory,"test-data",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try {
        var newer="""{"Version":3,"Groups":[]}""";var older="""{"Version":2,"Groups":[]}""";
        File.WriteAllText(Path.Combine(root,"workspace.json"),newer);File.WriteAllText(Path.Combine(root,"workspace.json.bak"),older);
        Throws<NotSupportedException>(()=>new WorkspaceStore(root).Load());
        Equal(newer,File.ReadAllText(Path.Combine(root,"workspace.json")));Equal(older,File.ReadAllText(Path.Combine(root,"workspace.json.bak")));
    } finally {Directory.Delete(root,true);}
});

Test("malformed schema types still recover valid backup", () => {
    foreach(var bad in new[]{"[]","null","42","{}","{\"Version\":\"bad\",\"Groups\":[]}"}) {
        var root=Path.Combine(AppContext.BaseDirectory,"test-data",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root,"workspace.json"),bad);
            var good=new Workspace();good.AddGroup("备份分组","A");
            File.WriteAllText(Path.Combine(root,"workspace.json.bak"),System.Text.Json.JsonSerializer.Serialize(good));
            var store=new WorkspaceStore(root);Equal("备份分组",store.Load().Groups.Single().Name);True(store.RecoveredFromBackup);
            Equal(bad,File.ReadAllText(Path.Combine(root,"workspace.json")));
        } finally {Directory.Delete(root,true);}
    }
});
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
