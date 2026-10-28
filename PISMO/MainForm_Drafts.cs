using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace PISMO
{
    public partial class MainForm
    {
        private sealed class DraftManifest
        {
            public string Text { get; set; } = "";
            public List<DraftFile> Files { get; set; } = new();
        }
        private sealed class DraftFile
        {
            public string Name { get; set; } = "";
            public int Kind { get; set; }
            public string Data { get; set; } = "";
            public string Cover { get; set; } = "";
        }
        private bool _draftFilesDirty = true;
        private readonly System.Windows.Forms.Timer _draftSaveTimer = new() { Interval = 400 };
        private string DraftKey => _currentGroupId > 0 ? $"group-{_currentGroupId}" :
            _currentChatPartnerId > 0 ? $"dm-{_currentChatPartnerId}" : null;
        private string DraftFolder(string key) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PISMO", "Drafts", key);

        private void InitializeDraftSaving()
        {
            _draftSaveTimer.Tick += (s, e) => { _draftSaveTimer.Stop(); SaveCurrentDraft(); };
            txtMessage.TextChanged += (s, e) => { _draftSaveTimer.Stop(); _draftSaveTimer.Start(); };
            FormClosing += (s, e) => { _draftSaveTimer.Stop(); SaveCurrentDraft(); };
        }

        private void SaveCurrentDraft()
        {
            string key = DraftKey;
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                var dir = DraftFolder(key);
                if (string.IsNullOrWhiteSpace(txtMessage.Text) && _pendingAttach.Count == 0)
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    _draftFilesDirty = false;
                    return;
                }
                Directory.CreateDirectory(dir);
                var manifest = new DraftManifest { Text = txtMessage.Text };
                for (int i = 0; i < _pendingAttach.Count; i++)
                {
                    var a = _pendingAttach[i];
                    string dataName = $"file-{i}.bin";
                    string dataPath = Path.Combine(dir, dataName);
                    if (_draftFilesDirty || !File.Exists(dataPath) || new FileInfo(dataPath).Length != a.Data.LongLength)
                        File.WriteAllBytes(dataPath, a.Data);
                    string coverName = "";
                    if (a.Cover?.Length > 0)
                    {
                        coverName = $"cover-{i}.bin";
                        string coverPath = Path.Combine(dir, coverName);
                        if (_draftFilesDirty || !File.Exists(coverPath) || new FileInfo(coverPath).Length != a.Cover.LongLength)
                            File.WriteAllBytes(coverPath, a.Cover);
                    }
                    manifest.Files.Add(new DraftFile { Name = a.FileName, Kind = (int)a.Kind, Data = dataName, Cover = coverName });
                }
                File.WriteAllText(Path.Combine(dir, "draft.json"), JsonSerializer.Serialize(manifest));
                _draftFilesDirty = false;
            }
            catch { }
        }

        // ── Метка «Черновик» в списке ───────────────────────────────────
        //
        // Метка — только у ЗАКРЫТОЙ переписки, где что-то осталось недописанным,
        // и появляется в момент выхода из неё. Раньше при сохранении (а оно идёт
        // по ходу набора и сразу при входе в чат) в карточку писалось само слово
        // «Черновик» — даже когда поле было пустым и черновик удалялся.

        /// <summary>Последний НАСТОЯЩИЙ текст под именем — чтобы вернуть его вместо метки.</summary>
        private readonly Dictionary<string, string> _realPreview = new();

        private static string DraftKeyOf(bool group, int id) => group ? $"group-{id}" : $"dm-{id}";

        private bool HasDraft(bool group, int id) => id > 0 && File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", "Drafts", DraftKeyOf(group, id), "draft.json"));

        private bool IsOpenChat(bool group, int id) =>
            group ? _currentGroupId == id : (_currentGroupId <= 0 && _currentChatPartnerId == id);

        /// <summary>Перерисовать подпись карточки: метка — если есть черновик и чат закрыт.</summary>
        private void RefreshDraftCard(bool group, int id)
        {
            if (id <= 0) return;
            if (!_realPreview.TryGetValue(DraftKeyOf(group, id), out var real)) real = null;
            if (real == null && !HasDraft(group, id)) return;
            UpdateCardPreview(group ? _groupPanels : _userPanels, id, real ?? "");
        }
        private void RestoreCurrentDraft()
        {
            string key = DraftKey;
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                string path = Path.Combine(DraftFolder(key), "draft.json");
                if (!File.Exists(path)) return;
                var manifest = JsonSerializer.Deserialize<DraftManifest>(File.ReadAllText(path));
                if (manifest == null) return;
                txtMessage.Text = manifest.Text ?? "";
                _pendingAttach.Clear();
                foreach (var f in manifest.Files)
                {
                    string dir = DraftFolder(key);
                    string dataPath = Path.Combine(dir, f.Data ?? "");
                    if (!File.Exists(dataPath)) continue;
                    var a = new PendingAttachment(File.ReadAllBytes(dataPath), f.Name, (AttachKind)f.Kind);
                    string coverPath = Path.Combine(dir, f.Cover ?? "");
                    if (!string.IsNullOrEmpty(f.Cover) && File.Exists(coverPath)) a.Cover = File.ReadAllBytes(coverPath);
                    _pendingAttach.Add(a);
                }
                ShowPreview();
            }
            catch { }
        }
    }
}
