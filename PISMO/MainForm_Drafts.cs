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
                    RefreshDraftListPreview();
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
                RefreshDraftListPreview();
            }
            catch { }
        }

        private void RefreshDraftListPreview()
        {
            if (_currentGroupId > 0) UpdateCardPreview(_groupPanels, _currentGroupId, "Черновик");
            else if (_currentChatPartnerId > 0) UpdateCardPreview(_userPanels, _currentChatPartnerId, "Черновик");
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
