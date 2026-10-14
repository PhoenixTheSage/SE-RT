using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using ClientPlugin;
using ClientPlugin.RichHud;
using ClientPlugin.Settings;
using SERT.Integration;
using SERT.Rendering;
using VRage.FileSystem;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void RetryNow(AnomalyTerminalHook hook) => typeof(AnomalyTerminalHook).GetField("nextAttempt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hook, 0L);
    static void Main()
    {
        try
        {
            var data = new RendererDiagnostics();
            int shows = 0, resets = 0, logs = 0, changes = 0;
            Config.Current.PropertyChanged += (_, __) => changes++;
            using (var hook = new AnomalyTerminalHook(() => data, () => shows++, () => resets++, () => logs++))
            {
                TerminalConfigRegistry.Available = false;
                hook.Update(true, true);
                Check(!hook.Installed && TerminalConfigRegistry.Pages.Count == 0, "missing host must be tolerated");
                TerminalConfigRegistry.Available = true; TerminalConfigRegistry.FailNextPopulate = true; RetryNow(hook);
                hook.Update(true, true);
                Check(!hook.Installed && TerminalConfigRegistry.Pages.Count == 0, "partial population must roll back");
                RetryNow(hook); hook.Update(true, true);
                Check(hook.Installed && TerminalConfigRegistry.Pages.Count == 2, "two folder pages must install");
                RichHudSupport.Available = false;
                hook.Update(false, true);
                Check(hook.Status.Contains("Master"), "registered pages must report a missing Master handshake");
                RichHudSupport.Available = true; hook.Update(false, true);
                var settings = TerminalConfigRegistry.Pages["SE-RT/Settings"];
                var status = TerminalConfigRegistry.Pages["SE-RT/Status"];
                settings.Setters["Enabled"](true);
                settings.Setters["Sampling scale"](.25f);
                settings.Setters["GPU target (ms)"](7f);
                settings.Setters["Storage exposure"](2f);
                Check(Config.Current.Enabled && Config.Current.SamplingScale == .25f && Config.Current.TargetMilliseconds == 7 && Config.Current.StorageExposure == 2, "HUD setters must update shared config");
                Config.Current.TargetMilliseconds = 9; // Pulsar/programmatic edit.
                hook.Update(true, false);
                Check((float)settings.Values["GPU target (ms)"] == 9, "push refresh must mirror external config changes");
                int afterChange = changes;
                hook.Update(true, false);
                Check(changes == afterChange, "refresh must not invoke settings setters");
                Config.Current.TargetMilliseconds = 10;
                TerminalConfigRegistry.FailNextRefresh = true;
                hook.Update(true, false); hook.Update(false, false);
                Check((float)settings.Values["GPU target (ms)"] == 10, "failed refresh must retain the pending settings synchronization");
                Config.Current.TargetMilliseconds = 9; hook.Update(true, false);
                int refreshes = status.Refreshes;
                hook.Update(false, true);
                Check(status.Refreshes == refreshes, "unchanged diagnostics must not rebuild labels");
                data.LastError = "test failure"; data.Fallbacks = 12;
                hook.Update(false, true);
                Check(status.Refreshes == refreshes + 1 && ((string)status.Values["Lifecycle and failures"]).Contains("test failure"), "new failures must reach status page");
                Check(settings.Buttons.Count == 0, "diagnostic buttons must live only on the Status page");
                status.Buttons["Show Status"](); status.Buttons["Reset history"](); status.Buttons["Write report to log"]();
                Check(shows == 1 && resets == 1 && logs == 1, "diagnostic buttons must call their owners");
                hook.Update(true, true);
                Check(TerminalConfigRegistry.Pages.Count == 2 && settings.Setters.Count == 15, "repeated updates must not duplicate controls");
                Check(Config.Current.RayTracedShadows && !Config.Current.AllowLightBlockOffsets, "physical-position shadow defaults");
                settings.Setters["Allow light block offsets"](true);
                settings.Setters["Shadow surface bias (m)"](.004f);
                settings.Setters["Shadow debug view"](SERT.Core.ShadowDebugView.SunVisibility);
                var shadows = Config.Current.Snapshot().Shadows;
                Check(shadows.Enabled && shadows.AllowLightBlockOffsets && shadows.Bias == .004f && shadows.DebugView == SERT.Core.ShadowDebugView.SunVisibility, "HUD shadow settings reach coherent render snapshot");
                settings.Setters["Lamp emitter radius (m)"](.08f);settings.Setters["Sun angular radius (degrees)"](.4f);
                settings.Setters["Refinement allowance"](.075f);settings.Setters["Thin glass transmission"](.8f);
                settings.Setters["Shadow bias mode"](SERT.Core.ShadowBiasMode.Manual);settings.Setters["Shadow sampling mode"](SERT.Core.ShadowSamplingMode.Hashed);settings.Setters["Sampling seed"](117f);
                shadows=Config.Current.Snapshot().Shadows;
                Check(shadows.LampRadius==.08f&&shadows.SolarDegrees==.4f&&shadows.RefinementFraction==.075f&&shadows.GlassTransmission==.8f&&shadows.SamplingSeed==117&&shadows.BiasMode==SERT.Core.ShadowBiasMode.Manual&&shadows.Sampling==SERT.Core.ShadowSamplingMode.Hashed,"all quality controls reach shared settings snapshot");
            }
            Check(TerminalConfigRegistry.Pages.Count == 0, "dispose must unregister exact folder/page keys");
            string path = Path.Combine(MyFileSystem.UserDataPath, "Storage", "SERT.cfg");
            ConfigStorage.FlushPending();
            Check(!File.Exists(path), "slider edits must not write before quiet period");
            ConfigStorage.FlushPending(true);
            Check(File.Exists(path) && ConfigStorage.Status.StartsWith("saved"), "forced shutdown flush must persist");
            int before = changes;
            Config.Current.TargetMilliseconds = 9;
            Check(changes == before && ConfigStorage.Status.StartsWith("saved"), "no-op assignment must not dirty config");
            Config.Current.StorageExposure = 3;
            ConfigStorage.FlushPending();
            Check(ConfigStorage.Status.StartsWith("dirty"), "new edit must defer persistence");
            Thread.Sleep(450);
            ConfigStorage.FlushPending();
            Config.Current.StorageExposure = 4; // Edit while an earlier snapshot may be saving.
            ConfigStorage.FlushPending(true);
            using (var reader = File.OpenRead(path))
            {
                var roundtrip = (Config)new XmlSerializer(typeof(Config)).Deserialize(reader);
                Check(roundtrip.Enabled && roundtrip.SamplingScale == .25f && roundtrip.TargetMilliseconds == 9 && roundtrip.StorageExposure == 4, "shutdown must preserve edits made during an earlier save");
                Check(roundtrip.AllowLightBlockOffsets && roundtrip.ShadowBias == .004f && roundtrip.ShadowDebugView == SERT.Core.ShadowDebugView.SunVisibility, "shadow settings persist with general settings");
                Check(roundtrip.LampRadius==.08f&&roundtrip.SolarAngularRadius==.4f&&roundtrip.RefinementAllowance==.075f&&roundtrip.GlassTransmission==.8f&&roundtrip.ShadowBiasMode==SERT.Core.ShadowBiasMode.Manual&&roundtrip.ShadowSamplingMode==SERT.Core.ShadowSamplingMode.Hashed&&roundtrip.SamplingSeed==117,"quality settings roundtrip persistence");
            }
            using(var legacy=new StringReader("<Config><ShadowBias>0.006</ShadowBias></Config>")){var old=(Config)new XmlSerializer(typeof(Config)).Deserialize(legacy);Check(old.ShadowBias==.006f&&old.ShadowBiasMode==SERT.Core.ShadowBiasMode.Automatic&&old.LampRadius==.025f&&old.RefinementAllowance==.05f,"old config retains manual value while receiving automatic defaults");}
            Config.Current.SamplingScale = float.NaN; Config.Current.TargetMilliseconds = float.PositiveInfinity; Config.Current.StorageExposure = float.NaN;
            var snapshot = Config.Current.Snapshot();
            Check(snapshot.SamplingScale == .5f && snapshot.TargetMilliseconds == 4 && snapshot.StorageExposure == 1, "invalid input must normalize before rendering/persistence");
            data.SceneBytes = 32L * 1048576; data.ReconstructionBytes = 181L * 1048576;
            var report = data.ToReport();
            Check(report.Contains("GPU: pending") && report.Contains("32.0 / 512.0 MiB") && report.Contains("candidate counts are not traced-ray"), "report must distinguish unavailable timing, budgets, and candidates");
            Console.WriteLine("SE-RT integration: " + checks + " checks passed.");
        }
        finally
        {
            ConfigStorage.FlushPending(true);
            // Test-owned unique temporary directory; never a workspace or user profile.
            string testDirectory = Path.GetFullPath(MyFileSystem.UserDataPath);
            string tempDirectory = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(testDirectory) == tempDirectory && Path.GetFileName(testDirectory).StartsWith("sert-integration-", StringComparison.Ordinal) && Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, true);
        }
    }
}
