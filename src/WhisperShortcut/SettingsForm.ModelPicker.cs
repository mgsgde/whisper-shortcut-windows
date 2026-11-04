namespace WhisperShortcut;

/// <summary>
/// The model pickers on Diktieren and Dictate Prompt, built like the Mac's (ModelPickerCuration): at most three tiles
/// (Empfohlen · Am schnellsten · Offline), the rest behind „Weitere Modelle“, one line about the chosen model below.
/// A tile sets the step's provider and model; the provider and model combos underneath stay the stored state, so
/// Snapshot and TrySave are unchanged.
/// </summary>
internal sealed partial class SettingsForm
{
    private sealed class Picker
    {
        public required FlowLayoutPanel Tiles { get; init; }
        public required FlowLayoutPanel More { get; init; }
        public required LinkLabel MoreToggle { get; init; }
        public required ThemedLabel Description { get; init; }
        public required Func<IReadOnlyList<Core.PickerModel>> Models { get; init; }
        public required Func<IReadOnlyList<Core.PickerModel>, IReadOnlyList<Core.PickerModel>> Recommended { get; init; }
        public required FluentCombo Engine { get; init; }
        public required FluentCombo Gemini { get; init; }
        public required FluentCombo OpenAi { get; init; }
        public required Func<bool> LocalReady { get; init; }
        public bool MoreOpen { get; set; }
    }

    private readonly List<Picker> _pickers = new();
    /// <summary>What the tiles last showed about the models on this PC (installed speech, installed LLM, which LLM).</summary>
    private (bool, bool, string)? _pickerModelState;
    private const int TileWidth = 214, TileHeight = 52;

    /// <summary>A model picker card for one step; <paramref name="engine"/> and the model combos are its stored state.</summary>
    private Card ModelPicker(string name, Func<IReadOnlyList<Core.PickerModel>> models,
        Func<IReadOnlyList<Core.PickerModel>, IReadOnlyList<Core.PickerModel>> recommended,
        FluentCombo engine, FluentCombo gemini, FluentCombo openAi, Func<bool> localReady)
    {
        // WHY no wrapping: a table cell measures a wrapping flow panel as if every tile broke onto its own row, and keeps
        // that height. The top row has at most four tiles (narrower then), the rest is a list like the Mac's.
        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), AccessibleName = name, AccessibleRole = AccessibleRole.Grouping };
        var more = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 4, 0, 0), Visible = false };
        var toggle = new LinkLabel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        var description = new ThemedLabel { AutoSize = true, Tone = Tone.Secondary, Font = Theme.Small, Margin = new Padding(0, 8, 0, 0) };
        Fit(description, 2 * RowPadding);
        var block = Block();
        block.Controls.Add(tiles);
        block.Controls.Add(toggle);
        block.Controls.Add(more);
        block.Controls.Add(description);
        var card = new Card();
        card.AddRow(block);
        var picker = new Picker
        {
            Tiles = tiles, More = more, MoreToggle = toggle, Description = description, Models = models, Recommended = recommended,
            Engine = engine, Gemini = gemini, OpenAi = openAi, LocalReady = localReady,
        };
        toggle.LinkClicked += (_, _) => { picker.MoreOpen = !picker.MoreOpen; RefreshPicker(picker); };
        _pickers.Add(picker);
        RefreshPicker(picker);
        return card;
    }

    /// <summary>For the UI tests: the tiles currently shown, all pickers (top rows and „Weitere Modelle“).</summary>
    internal IReadOnlyList<Control> PickerTilesForTests =>
        _pickers.SelectMany(p => p.Tiles.Controls.Cast<Control>().Concat(p.More.Controls.Cast<Control>())).ToList();

    /// <summary>For the UI tests: the names of all settings pages.</summary>
    internal IReadOnlyList<string> PageNamesForTests => _pages.Keys.ToList();

    /// <summary>For the UI tests: what a download progress tick does (OfflineEngine.DownloadChanged).</summary>
    internal void DownloadTickForTests() => UpdateModelRows();

    private void RefreshPickers()
    {
        if (IsDisposed) return;
        foreach (var p in _pickers) RefreshPicker(p);
    }

    private bool Usable(Core.PickerModel m) => m.Engine switch
    {
        Core.Engine.Gemini => _geminiKey.Value.Trim().Length > 0,
        Core.Engine.OpenAI => _openAiKey.Value.Trim().Length > 0,
        _ => true,
    };

    private static Core.PickerModel? Current(Picker p, IReadOnlyList<Core.PickerModel> models)
    {
        var engine = SelectedEngine(p.Engine);
        string id = engine switch
        {
            Core.Engine.Gemini => SelectedModel(p.Gemini),
            Core.Engine.OpenAI => SelectedModel(p.OpenAi),
            _ => Core.ModelPickers.LocalId,
        };
        return Core.ModelPickers.Find(models, engine, id);
    }

    private void RefreshPicker(Picker p)
    {
        var models = p.Models();
        var selected = Current(p, models);
        var curation = Core.PickerCuration.Curate(models, p.Recommended(models), selected, Usable);
        // Built before the Datenschutz page: no offline switch yet means the stored mode.
        bool offline = _offlineMode?.Checked ?? _settings.OfflineMode;
        SuspendLayout();
        Fill(p.Tiles, curation.Featured.Select(f => (f.Model, f.Badges)), curation.Featured.Count > 3 ? 162 : TileWidth, TileHeight);
        // „Weitere Modelle“: one list in the picker's order (provider by provider), the provider under each name.
        Fill(p.More, curation.More.Select(m => (m, (IReadOnlyList<Core.PickerBadge>)Array.Empty<Core.PickerBadge>())), 3 * TileWidth + 16, TileHeight - 6);
        p.MoreToggle.Visible = curation.More.Count > 0;
        p.MoreToggle.Text = $"{(p.MoreOpen ? "▾" : "▸")} Weitere Modelle ({curation.More.Count})";
        p.More.Visible = p.MoreOpen && curation.More.Count > 0;
        var shown = offline ? models.FirstOrDefault(m => m.Engine == Core.Engine.Local) : selected;
        p.Description.Text = shown == null ? ""
            : offline ? $"Der Offline-Modus ist an (Datenschutz & Berechtigungen): Es läuft {shown.Name} auf diesem PC."
            : $"{shown.Name} · {shown.Description}" + (Usable(shown) ? "" : " · Dafür fehlt ein Schlüssel unter Konten.")
              + (shown.Engine == Core.Engine.Local && !p.LocalReady() ? " · Muss einmal geladen werden (unten)." : "");
        ResumeLayout(true);
        // WHY: an auto-size flow panel keeps the height it had when it was laid out narrower (more wrapped rows).
        p.Tiles.Parent?.PerformLayout();

        void Fill(FlowLayoutPanel panel, IEnumerable<(Core.PickerModel Model, IReadOnlyList<Core.PickerBadge> Badges)> entries, int width, int height)
        {
            var old = panel.Controls.Cast<Control>().ToList();
            panel.Controls.Clear();
            foreach (var c in old) c.Dispose();
            foreach (var (model, badges) in entries)
            {
                var notes = badges.Select(b => b switch
                {
                    Core.PickerBadge.Recommended => "★ Empfohlen",
                    Core.PickerBadge.Fastest => "⚡ Am schnellsten",
                    _ => "Offline",
                }).ToList();
                if (!Usable(model)) notes.Add("Schlüssel nötig");
                if (model.Engine == Core.Engine.Local && !p.LocalReady()) notes.Add("Download nötig");
                if (badges.Count == 0) notes.Insert(0, ProviderName(model.Engine));
                var tile = new ModelTile(model.Name, string.Join(" · ", notes))
                {
                    Size = new Size(width, height), Margin = new Padding(0, 0, 8, panel == p.More ? 4 : 8),
                    Selected = !offline && model == selected, Enabled = !offline,
                };
                tile.Click += (_, _) => Pick(p, model);
                panel.Controls.Add(tile);
            }
        }
    }

    private static string ProviderName(Core.Engine engine) => engine switch
    {
        Core.Engine.Gemini => "Google",
        Core.Engine.OpenAI => "OpenAI",
        _ => "Auf diesem PC",
    };

    /// <summary>A tile was chosen: set the step's provider and, online, its model. The automatic apply stores it.</summary>
    private void Pick(Picker p, Core.PickerModel model)
    {
        p.Engine.SelectedItem = p.Engine.Items.Cast<EngineItem>().First(i => i.Engine == model.Engine);
        var combo = model.Engine switch { Core.Engine.Gemini => p.Gemini, Core.Engine.OpenAI => p.OpenAi, _ => null };
        if (combo != null) combo.SelectedItem = combo.Items.Cast<ModelItem>().First(i => i.Model.Id == model.Id);
        RefreshPicker(p);
    }
}
