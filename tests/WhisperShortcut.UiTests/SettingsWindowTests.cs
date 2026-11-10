using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace WhisperShortcut.UiTests;

/// <summary>
/// Opens the real settings window, the way the tray does, against a throwaway settings.json in the temp folder.
/// Catches what the Core tests cannot: a window that is slow to open or freezes under frequent updates.
/// </summary>
public sealed class SettingsWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-uitests-" + Guid.NewGuid().ToString("N"));

    public SettingsWindowTests()
    {
        Directory.CreateDirectory(_dir);
        Core.Log.Init(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// 0.6.0 froze while an offline model downloaded: every progress tick rebuilt all model tiles and relaid out the
    /// page. Ticks must leave the tiles alone and stay cheap.
    /// </summary>
    [Fact]
    public void DownloadProgressTicks_DoNotRebuildTiles()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings();
            var tiles = form.PickerTilesForTests;
            Assert.NotEmpty(tiles);

            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                form.DownloadTickForTests();
                Application.DoEvents();
            }
            watch.Stop();

            Assert.Equal(tiles, form.PickerTilesForTests); // same instances: nothing rebuilt
            Assert.True(watch.ElapsedMilliseconds < 2000, $"200 progress ticks took {watch.ElapsedMilliseconds} ms");
        });
    }

    /// <summary>Building and showing a new window (the app normally shows one built in advance); a regression shows up here.</summary>
    [Fact]
    public void SettingsWindow_OpensQuickly()
    {
        Sta.Run(() =>
        {
            // A first window takes the one-off JIT and font cost, which the user pays once per app start.
            using (OpenSettings()) { }
            var watch = Stopwatch.StartNew();
            using var form = OpenSettings();
            watch.Stop();
            // Measured 04.11.2026 on Magnus's PC: 9.1 s before the lazy page layout, 5.4 s after, about 2 s once Show
            // stopped laying out per container (SettingsForm.SetVisibleCore). Without the app's prebuilt window this
            // is what the first Alt+0 costs.
            const int budget = 4000;
            Assert.True(watch.ElapsedMilliseconds < budget, $"settings window took {watch.ElapsedMilliseconds} ms to open (budget {budget} ms)");
        });
    }

    /// <summary>
    /// The app builds the window in advance (TrayApp.ScheduleSpareSettings), so Alt+0 only has to make it visible —
    /// instant like the Mac. Building it must neither show it nor take the focus.
    /// </summary>
    [Fact]
    public void PrewarmedWindow_ShowsInstantly()
    {
        Sta.Run(() =>
        {
            var settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
            using var form = new SettingsForm(settings, new OfflineEngine(settings), new Core.GeminiClient(settings),
                new Core.OpenAiClient(settings), new UpdateService(settings, () => true, () => { })) { ShowInTaskbar = false };
            form.Prewarm();
            Assert.False(form.Visible);
            Assert.Equal(1.0, form.Opacity);

            form.Load += (_, _) => form.Location = new System.Drawing.Point(-3000, -3000); // keep the test off screen
            form.Location = new System.Drawing.Point(-3000, -3000);
            var watch = Stopwatch.StartNew();
            form.Show();
            Application.DoEvents();
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 600, $"prewarmed settings window took {watch.ElapsedMilliseconds} ms to show");
        });
    }

    /// <summary>The spare window waits hidden for hours; its 150 ms edit check and the AI-app clock must only run while it is visible.</summary>
    [Fact]
    public void PrewarmedWindow_DoesNoBackgroundWork()
    {
        Sta.Run(() =>
        {
            var settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
            using var form = new SettingsForm(settings, new OfflineEngine(settings), new Core.GeminiClient(settings),
                new Core.OpenAiClient(settings), new UpdateService(settings, () => true, () => { })) { ShowInTaskbar = false };
            form.Prewarm();
            Assert.False(form.TimersRunningForTests);

            form.StartPosition = FormStartPosition.Manual;
            form.Load += (_, _) => form.Location = new System.Drawing.Point(-3000, -3000);
            form.Location = new System.Drawing.Point(-3000, -3000);
            form.Show();
            Application.DoEvents();
            Assert.True(form.TimersRunningForTests);
        });
    }

    /// <summary>Clicking through every page must not throw (all pages are built up front, shown on demand).</summary>
    [Fact]
    public void EveryPage_Opens()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings();
            foreach (var page in form.PageNamesForTests)
            {
                form.ShowPage(page);
                Application.DoEvents();
            }
        });
    }

    /// <summary>
    /// Diktieren → Modus (Mac Dictate mode): Live is always selectable; it shows either the Live caption or the orange
    /// note saying why it cannot start, never both. Back on Diktieren the note goes away.
    /// </summary>
    [Fact]
    public void DictateMode_LiveShowsCaptionOrWarning()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings();
            form.ShowPage("Diktieren");
            Application.DoEvents();
            var live = form.SetDictateModeForTests(Core.DictateMode.Live);
            Assert.True(live.WarningVisible ^ live.Caption.Length > 0, $"warning {live.WarningVisible}, caption '{live.Caption}'");
            var dictate = form.SetDictateModeForTests(Core.DictateMode.Dictate);
            Assert.False(dictate.WarningVisible);
            Assert.Equal("Der Text wird eingefügt, wenn du stoppst.", dictate.Caption);
        });
    }

    /// <summary>R8: the engine, punctuation, dictate-mode and after-paste combos are filled from Core.Choices and must show what is stored.</summary>
    [Fact]
    public void ChoiceCombos_ShowStoredValues()
    {
        Sta.Run(() =>
        {
            var path = Path.Combine(_dir, "settings.json");
            var stored = Core.Settings.Load(path, new Core.PlainSecretProtector());
            stored.OpenAiKey = "sk-test";
            stored.SpeechEngine = Core.Engine.OpenAI;
            stored.TextEngine = Core.Engine.OpenAI;
            stored.PunctuationMode = Core.PunctuationMode.SpokenOnly;
            stored.DictateMode = Core.DictateMode.Live;
            stored.ClipboardAfterPaste = Core.ClipboardAfterPaste.Clear;
            stored.Save();

            using var form = OpenSettings();
            Assert.Equal(Core.Engine.OpenAI, ComboValue(form, "_speechEngine"));
            Assert.Equal(Core.Engine.OpenAI, ComboValue(form, "_textEngine"));
            Assert.Equal(Core.PunctuationMode.SpokenOnly, ComboValue(form, "_punctuation"));
            Assert.Equal(Core.DictateMode.Live, ComboValue(form, "_dictateMode"));
            Assert.Equal(Core.ClipboardAfterPaste.Clear, ComboValue(form, "_afterPaste"));
        });
    }

    /// <summary>R8: picking a non-default entry in each of those combos and saving writes the matching token to settings.json.</summary>
    [Fact]
    public void ChoiceCombos_SaveToFile()
    {
        Sta.Run(() =>
        {
            var path = Path.Combine(_dir, "settings.json");
            var seed = Core.Settings.Load(path, new Core.PlainSecretProtector());
            seed.OpenAiKey = "sk-test";
            seed.Save();

            using var form = OpenSettings();
            SetCombo(form, "_speechEngine", Core.Engine.OpenAI);
            SetCombo(form, "_punctuation", Core.PunctuationMode.SpokenOnly);
            SetCombo(form, "_afterPaste", Core.ClipboardAfterPaste.Clear);
            SetCombo(form, "_dictateMode", Core.DictateMode.Live);
            Assert.True((bool)typeof(SettingsForm).GetMethod("TrySave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!);

            var fresh = Core.Settings.Load(path, new Core.PlainSecretProtector());
            Assert.Equal(Core.Engine.OpenAI, fresh.SpeechEngine);
            Assert.Equal(Core.PunctuationMode.SpokenOnly, fresh.PunctuationMode);
            Assert.Equal(Core.ClipboardAfterPaste.Clear, fresh.ClipboardAfterPaste);
            Assert.Equal(Core.DictateMode.Live, fresh.DictateMode);

            var raw = File.ReadAllText(path);
            foreach (var token in new[] { "\"openai\"", "\"spokenOnly\"", "\"clear\"", "\"live\"" })
                Assert.Contains(token, raw);
        });
    }

    /// <summary>
    /// A shortcut switched off in settings.json (dictateHotkeyEnabled / promptHotkeyEnabled false) must show as empty
    /// ("Kein Kürzel"), like the Mac's disabled shortcuts, and saving the window must keep it off and keep the key.
    /// </summary>
    [Fact]
    public void DisabledDictateAndPromptKeys_ShowEmptyAndStayOff()
    {
        Sta.Run(() =>
        {
            var path = Path.Combine(_dir, "settings.json");
            var seed = Core.Settings.Load(path, new Core.PlainSecretProtector());
            seed.DictateHotkeyEnabled = false;
            seed.PromptHotkeyEnabled = false;
            seed.Save();

            using var form = OpenSettings();
            Assert.Equal("", KeyBoxValue(form, "_dictateKey"));
            Assert.Equal("", KeyBoxValue(form, "_promptKey"));
            Assert.True((bool)typeof(SettingsForm).GetMethod("TrySave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!);

            var fresh = Core.Settings.Load(path, new Core.PlainSecretProtector());
            Assert.False(fresh.DictateHotkeyEnabled);
            Assert.False(fresh.PromptHotkeyEnabled);
            Assert.Equal(seed.DictateHotkey, fresh.DictateHotkey);
            Assert.Equal("", fresh.ActiveDictateHotkey);
            Assert.Equal("", fresh.ActivePromptHotkey);
        });
    }

    private static string KeyBoxValue(SettingsForm form, string field)
    {
        var box = typeof(SettingsForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        return (string)box.GetType().GetProperty("Value")!.GetValue(box)!;
    }

    private static ComboBox Combo(SettingsForm form, string field) =>
        (ComboBox)typeof(SettingsForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    /// <summary>The Value of the combo's selected ChoiceItem (a private record, hence reflection).</summary>
    private static object? ComboValue(SettingsForm form, string field) =>
        Combo(form, field).SelectedItem!.GetType().GetProperty("Value")!.GetValue(Combo(form, field).SelectedItem);

    private static void SetCombo(SettingsForm form, string field, object value)
    {
        var combo = Combo(form, field);
        combo.SelectedItem = combo.Items.Cast<object>().First(i => Equals(i.GetType().GetProperty("Value")!.GetValue(i), value));
    }

    private SettingsForm OpenSettings()
    {
        var settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
        var form = new SettingsForm(settings, new OfflineEngine(settings), new Core.GeminiClient(settings),
            new Core.OpenAiClient(settings), new UpdateService(settings, () => true, () => { }))
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-3000, -3000), // off screen: the test must not steal the user's focus
        };
        form.Show();
        Application.DoEvents();
        return form;
    }
}
