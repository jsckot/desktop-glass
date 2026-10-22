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
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
