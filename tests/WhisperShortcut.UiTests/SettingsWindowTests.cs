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
            stored.Dictate = stored.Dictate with { Engine = Core.Engine.OpenAI };
            stored.Prompt = stored.Prompt with { Engine = Core.Engine.OpenAI };
            stored.PunctuationMode = Core.PunctuationMode.SpokenOnly;
            stored.DictateMode = Core.DictateMode.Live;
            stored.ClipboardAfterPaste = Core.ClipboardAfterPaste.Clear;
            stored.Save();

            using var form = OpenSettings();
            Assert.Equal(Core.Engine.OpenAI, ComboValue(form, "_dictateStep.Engine"));
            Assert.Equal(Core.Engine.OpenAI, ComboValue(form, "_promptStep.Engine"));
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
            SetCombo(form, "_dictateStep.Engine", Core.Engine.OpenAI);
            SetCombo(form, "_punctuation", Core.PunctuationMode.SpokenOnly);
            SetCombo(form, "_afterPaste", Core.ClipboardAfterPaste.Clear);
            SetCombo(form, "_dictateMode", Core.DictateMode.Live);
            Assert.True((bool)typeof(SettingsForm).GetMethod("TrySave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!);

            var fresh = Core.Settings.Load(path, new Core.PlainSecretProtector());
            Assert.Equal(Core.Engine.OpenAI, fresh.Dictate.Engine);
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

    /// <summary>
    /// R7: a setting changed from outside (tray, playback bar, AI app) while the window is open and untouched there is shown after
    /// Pull, and a later save of another field neither reverts it in the file nor in the window.
    /// </summary>
    [Fact]
    public void OutsideChange_IsShownAfterPull_AndNotOverwrittenByLaterSave()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings(out var settings);
            Assert.False(form.HasUnsavedChanges);
            double speed = Core.ReadAloud.Speeds.First(s => Math.Abs(s - settings.ReadAloudSpeed) > 0.001);

            settings.Sounds = !settings.Sounds;
            settings.ReadAloudSpeed = speed;
            settings.OfflineMode = !settings.OfflineMode;
            var sounds = settings.Sounds;
            var offline = settings.OfflineMode;
            form.Pull();

            Assert.Equal(sounds, Toggle(form, "_sounds"));
            Assert.Equal(speed, ComboProperty(form, "_readAloudSpeed", "Speed"));
            Assert.Equal(offline, Toggle(form, "_offlineMode"));
            Assert.False(form.HasUnsavedChanges); // taking a value over is no edit

            // Edit something else and save; the pulled values must stay.
            SetCombo(form, "_afterPaste", Core.ClipboardAfterPaste.Clear);
            Assert.True(form.HasUnsavedChanges);
            Assert.True(Save(form));

            var fresh = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
            Assert.Equal(Core.ClipboardAfterPaste.Clear, fresh.ClipboardAfterPaste);
            Assert.Equal(sounds, fresh.Sounds);
            Assert.Equal(speed, fresh.ReadAloudSpeed);
            Assert.Equal(offline, fresh.OfflineMode);
            Assert.False(form.HasUnsavedChanges);
        });
    }

    /// <summary>R7: even without a Pull, a save writes only what was edited: a stale control never reverts an outside change.</summary>
    [Fact]
    public void Save_WritesOnlyEditedFields()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings(out var settings);
            settings.Sounds = !settings.Sounds;
            settings.ChatWebSearch = !settings.ChatWebSearch;
            var (sounds, search) = (settings.Sounds, settings.ChatWebSearch);

            SetCombo(form, "_punctuation", Core.PunctuationMode.SpokenOnly);
            Assert.True(Save(form));

            Assert.Equal(sounds, settings.Sounds);
            Assert.Equal(search, settings.ChatWebSearch);
            Assert.Equal(Core.PunctuationMode.SpokenOnly, settings.PunctuationMode);
        });
    }

    /// <summary>R7: a field the user is editing keeps the edit through a Pull; the other fields still take the outside change.</summary>
    [Fact]
    public void EditedField_IsNotOverwrittenByPull()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings(out var settings);
            var prompt = PromptBox(form, "_chatPrompt");
            prompt.Text = "Mein eigener Chat-Prompt";
            Assert.True(form.HasUnsavedChanges);

            settings.ChatSystemPrompt = "Von außen gesetzt";
            settings.Sounds = !settings.Sounds;
            form.Pull();

            Assert.Equal("Mein eigener Chat-Prompt", prompt.Text);
            Assert.Equal(settings.Sounds, Toggle(form, "_sounds"));
            Assert.True(form.HasUnsavedChanges);

            Assert.True(Save(form));
            Assert.Equal("Mein eigener Chat-Prompt", settings.ChatSystemPrompt); // the edit wins on its own field
            Assert.False(form.HasUnsavedChanges);
        });
    }

    /// <summary>R7: a glossary term added from outside (the glossary key) appears in the open window, also while the glossary box is being edited.</summary>
    [Fact]
    public void GlossaryTermFromOutside_IsShown_AndKeptNextToAnEdit()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings(out var settings);
            var glossary = (TextBox)Field(form, "_glossary");

            settings.Glossary = new List<string> { "Müller" };
            form.Pull();
            Assert.Equal("Müller", glossary.Text);
            Assert.False(form.HasUnsavedChanges);

            glossary.Text = "Müller\r\nSchmidt"; // typed, not yet saved
            settings.Glossary = new List<string> { "Müller", "Meier" }; // the glossary key, meanwhile
            form.Pull();
            Assert.Equal(new[] { "Müller", "Schmidt", "Meier" }, glossary.Lines);

            Assert.True(Save(form));
            Assert.Equal(new[] { "Müller", "Schmidt", "Meier" }, settings.Glossary);
        });
    }

    /// <summary>R7: shortcuts are taken over only when all are valid; the other edits are saved anyway and the refused ones stay in the window.</summary>
    [Fact]
    public void TakenShortcut_IsHeldBack_OtherEditsAreSaved()
    {
        Sta.Run(() =>
        {
            using var form = OpenSettings(out var settings);
            var (dictate, prompt) = (settings.DictateHotkey, settings.PromptHotkey);
            var box = Field(form, "_dictateKey");
            box.GetType().GetMethod("SetValue")!.Invoke(box, new object[] { settings.PromptHotkey, false }); // taken twice
            SetCombo(form, "_afterPaste", Core.ClipboardAfterPaste.Clear);

            Assert.True(Save(form));

            Assert.Equal(Core.ClipboardAfterPaste.Clear, settings.ClipboardAfterPaste);
            Assert.Equal(dictate, settings.DictateHotkey);
            Assert.Equal(prompt, settings.PromptHotkey);
            Assert.True(form.HasUnsavedChanges); // the refused key is still shown as edited

            // Fixing the clash lets the next save take the key.
            box.GetType().GetMethod("SetValue")!.Invoke(box, new object[] { "Alt+8", false });
            Assert.True(Save(form));
            Assert.Equal("Alt+8", settings.DictateHotkey);
            Assert.False(form.HasUnsavedChanges);
        });
    }

    private static object Field(SettingsForm form, string field) =>
        typeof(SettingsForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static bool Toggle(SettingsForm form, string field)
    {
        var control = Field(form, field);
        return (bool)control.GetType().GetProperty("Checked")!.GetValue(control)!;
    }

    private static TextBox PromptBox(SettingsForm form, string field)
    {
        var editor = Field(form, field);
        return (TextBox)editor.GetType().GetProperty("Box")!.GetValue(editor)!;
    }

    private static object? ComboProperty(SettingsForm form, string field, string property)
    {
        var item = Combo(form, field).SelectedItem!;
        return item.GetType().GetProperty(property)!.GetValue(item);
    }

    private static bool Save(SettingsForm form) =>
        (bool)typeof(SettingsForm).GetMethod("TrySave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;

    private static string KeyBoxValue(SettingsForm form, string field)
    {
        var box = typeof(SettingsForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        return (string)box.GetType().GetProperty("Value")!.GetValue(box)!;
    }

    /// <summary>A combo field of the form, or "_step.Property" for a combo of a model step's controls.</summary>
    private static ComboBox Combo(SettingsForm form, string field)
    {
        var parts = field.Split('.');
        var value = typeof(SettingsForm).GetField(parts[0], BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        if (parts.Length > 1) value = value.GetType().GetProperty(parts[1])!.GetValue(value)!;
        return (ComboBox)value;
    }

    /// <summary>The Value of the combo's selected ChoiceItem (a private record, hence reflection).</summary>
    private static object? ComboValue(SettingsForm form, string field) =>
        Combo(form, field).SelectedItem!.GetType().GetProperty("Value")!.GetValue(Combo(form, field).SelectedItem);

    private static void SetCombo(SettingsForm form, string field, object value)
    {
        var combo = Combo(form, field);
        combo.SelectedItem = combo.Items.Cast<object>().First(i => Equals(i.GetType().GetProperty("Value")!.GetValue(i), value));
    }

    private SettingsForm OpenSettings() => OpenSettings(out _);

    private SettingsForm OpenSettings(out Core.Settings settings)
    {
        settings = Core.Settings.Load(Path.Combine(_dir, "settings.json"), new Core.PlainSecretProtector());
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
