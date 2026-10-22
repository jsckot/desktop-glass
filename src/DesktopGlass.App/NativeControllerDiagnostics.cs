using System.Text.Json;

namespace DesktopGlass.App;

/// <summary>验证工作区收纳事务的故障边界；使用内存租约，不接触真实桌面、文件属性或用户配置。</summary>
internal static class NativeControllerDiagnostics
{
    /// <summary>执行排序及故障注入检查，将结果写入项目 .codex 下的指定 JSON。</summary>
    /// <param name="args">需要 --output JSON 路径；不创建应用窗口。</param>
    /// <returns>所有检查通过为 0，参数或检查失败为 1。</returns>
    internal static int Run(string[] args)
    {
        string? output = null;
        var passed = new List<string>();
        try
        {
            var index = Array.IndexOf(args, "--output");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("控制器检查需要 --output。");
            output = WidgetDiagnostics.CheckedOutput(args[index + 1]);
            Workspace Source() => new() { Groups = [new Group { Id = "A", Name = "第一组", DisplayId = "test", Entries = ["a", "b", "c", "d"] }, new Group { Id = "B", Name = "第二组", DisplayId = "test", Entries = ["e"] }] };
            void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"预期 {expected}，实际 {actual}"); }
            void Order(Workspace value, string group, string expected) => Equal(string.Join(",", value.Groups.Single(g => g.Id == group).Entries), expected);
            void Test(string name, Action action) { action(); passed.Add(name);  }
            void Throws(Action action, string? expectedType = null)
            {
                try { action(); } catch (Exception error) { if (expectedType is not null) Equal(error.GetType().Name, expectedType); return; }
                throw new Exception("预期异常，但事务成功返回。");
            }
            Workspace Collect(Workspace source, string[] paths, HashSet<string> hidden, Action<string> collect, Action<string> release,
                Action<Workspace> save, Func<bool>? alive = null) => NativeDesktopController.CollectTransaction(source, "A", paths, 4,
                    (Func<string, bool>)hidden.Contains, collect, release, save, alive ?? (() => true));
            Workspace Release(Workspace source, string[] paths, HashSet<string> hidden, Action<string> collect, Action<string> release,
                Action<Workspace> save, Action<Workspace>? mutate = null) => NativeDesktopController.ReleaseTransaction(source, paths,
                    mutate ?? (candidate => { foreach (var path in paths) candidate.MoveEntry(path, null); }), (Func<string, bool>)hidden.Contains, collect, release, save);

            Test("同组向后拖动修正目标索引且不修改源对象", () => {
                var source = Source(); var value = NativeDesktopController.PlanCollection(source, "A", new[] { "a" }, 3);
                Order(value, "A", "b,c,a,d"); Order(source, "A", "a,b,c,d");
            });
            Test("跨组多选按输入顺序插入并去重", () => {
                var value = NativeDesktopController.PlanCollection(Source(), "A", new[] { "e", "b", "E" }, 3);
                Order(value, "A", "a,c,e,b,d"); Order(value, "B", "");
            });
            Test("保存失败只恢复新收纳项并保留已有租约", () => {
                var source = Source(); var hidden = new HashSet<string> { "a" }; var released = new List<string>();
                Throws(() => Collect(source, ["x", "a"], hidden, path => hidden.Add(path), path => { released.Add(path); hidden.Remove(path); }, _ => throw new IOException("save failed")), "IOException");
                Equal(string.Join(",", hidden), "a"); Equal(string.Join(",", released), "x"); Order(source, "A", "a,b,c,d");
            });
            Test("隐藏失败后仍有恢复责任时必须回滚", () => {
                var hidden = new HashSet<string>(); var saves = 0;
                Throws(() => Collect(Source(), ["x"], hidden, path => { hidden.Add(path); throw new IOException("hide failed"); }, path => hidden.Remove(path), _ => saves++), "IOException");
                Equal(hidden.Count, 0); Equal(saves, 0);
            });
            Test("保护进程中途失效取消保存并恢复新隐藏项", () => {
                var hidden = new HashSet<string>(); var checks = 0; var saves = 0;
                Throws(() => Collect(Source(), ["x"], hidden, path => hidden.Add(path), path => hidden.Remove(path), _ => saves++, () => ++checks == 1), "InvalidOperationException");
                Equal(hidden.Count, 0); Equal(saves, 0);
            });
            Test("回滚失败明确升级且保留恢复责任", () => {
                var hidden = new HashSet<string>();
                Throws(() => Collect(Source(), ["x"], hidden, path => hidden.Add(path), _ => throw new IOException("restore failed"), _ => throw new IOException("save failed")), "MutationRecoveryException");
                Equal(hidden.Contains("x"), true);
            });
            Test("已有租约跨组移动不重复采集", () => {
                var hidden = new HashSet<string> { "e" }; var collects = 0;
                var value = Collect(Source(), ["e"], hidden, _ => collects++, _ => throw new Exception("unexpected release"), _ => { });
                Equal(collects, 0); Order(value, "A", "a,b,c,d,e"); Order(value, "B", "");
            });
            Test("移出后保存失败重建原租约和源工作区", () => {
                var source = Source(); var hidden = new HashSet<string> { "a" }; var collects = 0;
                Throws(() => Release(source, ["a"], hidden, path => { collects++; hidden.Add(path); }, path => hidden.Remove(path), _ => throw new IOException("save failed")), "IOException");
                Equal(hidden.Contains("a"), true); Equal(collects, 1); Order(source, "A", "a,b,c,d");
            });
            Test("移出回滚失败升级而不改源配置", () => {
                var source = Source(); var hidden = new HashSet<string> { "a" };
                Throws(() => Release(source, ["a"], hidden, _ => throw new IOException("guard failed"), path => hidden.Remove(path), _ => throw new IOException("save failed")), "MutationRecoveryException");
                Order(source, "A", "a,b,c,d");
            });
            Test("恢复本身失败不保存移出状态", () => {
                var hidden = new HashSet<string> { "a" }; var saves = 0;
                Throws(() => Release(Source(), ["a"], hidden, _ => { }, _ => throw new IOException("restore failed"), _ => saves++), "MutationRecoveryException");
                Equal(hidden.Contains("a"), true); Equal(saves, 0);
            });
            Test("删除组先恢复所有已租约项再保存", () => {
                var source = Source(); var hidden = new HashSet<string> { "a", "b" }; var trace = new List<string>();
                var value = Release(source, ["a", "b", "c", "d"], hidden, _ => throw new Exception("unexpected collect"), path => { trace.Add("release:" + path); hidden.Remove(path); },
                    _ => { Equal(hidden.Count, 0); trace.Add("save"); }, candidate => candidate.RemoveGroup("A"));
                Equal(string.Join(",", trace), "release:a,release:b,save"); Equal(value.Groups.Count, 1); Equal(source.Groups.Count, 2);
            });
            Test("空输入不隐藏也不保存", () => {
                var hidden = new HashSet<string>();
                Throws(() => Collect(Source(), [], hidden, _ => throw new Exception("unexpected collect"), _ => throw new Exception("unexpected release"), _ => throw new Exception("unexpected save")), "ArgumentException");
            });

            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = true, Count = passed.Count, Checks = passed,
                RealDesktopActions = false, RealFileAttributeActions = false }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = false, Count = passed.Count,
                Checks = passed, Error = error.ToString(), RealDesktopActions = false, RealFileAttributeActions = false },
                new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
