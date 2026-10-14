// Deliberately no game/Rich HUD binaries: exercise the real reflection hook and persistence code.
using System;
using System.Collections.Generic;
using System.IO;

namespace VRage.FileSystem
{
    public static class MyFileSystem
    {
        public static string UserDataPath = Path.Combine(Path.GetTempPath(), "sert-integration-" + Guid.NewGuid().ToString("N"));
    }
}
namespace VRage.Utils
{
    public sealed class MyLog
    {
        public static readonly MyLog Default = new MyLog();
        public void Warning(string text) => Console.WriteLine(text);
    }
}
namespace ClientPlugin
{
    public sealed class Plugin
    {
        public const string Name = "SERT";
        public static Plugin Instance;
        public void ShowStatus() { }
        public void RequestHistoryReset() { }
        public void LogStatus() { }
    }
}
namespace ClientPlugin.Settings.Elements
{
    public class CheckboxAttribute : Attribute { public CheckboxAttribute(string description = null) { } }
    public class SliderAttribute : Attribute
    {
        public enum SliderType { Float, Integer }
        public SliderAttribute(float min, float max, float step, SliderType type, string description = null) { }
    }
    public class ButtonAttribute : Attribute { public ButtonAttribute(string description = null) { } }
    public class DropdownAttribute : Attribute { public DropdownAttribute(string description = null) { } }
}
namespace ClientPlugin.RichHud
{
    public static class RichHudSupport { public static bool Available { get; set; } = true; }
    public static class TerminalConfigRegistry
    {
        public static bool Available = true, FailNextPopulate, FailNextRefresh;
        public static readonly Dictionary<string, Page> Pages = new Dictionary<string, Page>();
        public static Page RequestFolderPage(string folder, string page)
        {
            if (!Available) return null;
            string key = folder + "/" + page;
            if (!Pages.TryGetValue(key, out var result)) Pages[key] = result = new Page();
            return result;
        }
        public static bool UnregisterPage(string key) => Pages.Remove(key);
    }
    public sealed class Page
    {
        public int Refreshes;
        public readonly Dictionary<string, Func<object>> Getters = new Dictionary<string, Func<object>>();
        public readonly Dictionary<string, Action<object>> Setters = new Dictionary<string, Action<object>>();
        public readonly Dictionary<string, Action> Buttons = new Dictionary<string, Action>();
        public readonly Dictionary<string, object> Values = new Dictionary<string, object>();
        public Page Category(string name)
        {
            if (TerminalConfigRegistry.FailNextPopulate) { TerminalConfigRegistry.FailNextPopulate = false; throw new InvalidOperationException("injected population failure"); }
            return this;
        }
        public Page Columns(int count) => this;
        public Page Label(string text) => this;
        public Page Label(string text, Func<string> get) { Getters[text] = () => get(); return this; }
        public Page Checkbox(string text, Func<bool> get, Action<bool> set, string description)
        { Getters[text] = () => get(); Setters[text] = v => set((bool)v); return this; }
        public Page Slider(string text, float min, float max, Func<float> get, Action<float> set, string description, float step)
        { Getters[text] = () => get(); Setters[text] = v => set((float)v); return this; }
        public Page Dropdown(string text, Type type, Func<object> get, Action<object> set, string description)
        { Getters[text] = get; Setters[text] = set; return this; }
        public Page Button(string text, Action click, string description) { Buttons[text] = click; return this; }
        public Page Refresh()
        {
            if (TerminalConfigRegistry.FailNextRefresh) { TerminalConfigRegistry.FailNextRefresh = false; throw new InvalidOperationException("injected refresh failure"); }
            Refreshes++;
            foreach (var getter in Getters) Values[getter.Key] = getter.Value();
            return this;
        }
    }
}
