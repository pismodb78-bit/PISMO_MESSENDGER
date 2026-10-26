using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace PISMO
{
    public sealed partial class ServersForm
    {
        private sealed class ChannelDraftData
        {
            public string Text { get; set; } = "";
            public List<ChannelDraftFile> Files { get; set; } = new();
        }
        private sealed class ChannelDraftFile
        {
            public string Name { get; set; } = "";
            public bool IsImg { get; set; }
            public string Data { get; set; } = "";
        }
        private static string ChannelDraftDirectory(int channel) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PISMO", "Drafts", $"server-{channel}");
        private bool HasChannelDraft(int channel) => File.Exists(Path.Combine(ChannelDraftDirectory(channel), "draft.json"));
        private void UpdateChannelDraftPreview()
        {
            if (_channelId <= 0 || !_channelPreviewCtl.TryGetValue(_channelId, out var ctl) || ctl.IsDisposed) return;
            if (HasChannelDraft(_channelId)) { ctl.Text = "Черновик"; ctl.ForeColor = System.Drawing.Color.FromArgb(235, 75, 85); }
            else { ctl.Text = _channelPreviews.TryGetValue(_channelId, out var text) ? text : ""; ctl.ForeColor = System.Drawing.Color.FromArgb(185, 190, 198); }
        }
        private bool _channelDraftFilesDirty = true;
        private bool _switchingChannelDraft;
        private void SaveChannelDraft()
        {
            if (_switchingChannelDraft || _channelId <= 0 || _txtInput == null) return;
            string dir = ChannelDraftDirectory(_channelId);
            try
            {
                if (string.IsNullOrWhiteSpace(_txtInput.Text) && _chPending.Count == 0)
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    _channelDraftFilesDirty = false;
                    UpdateChannelDraftPreview();
                    return;
                }
                Directory.CreateDirectory(dir);
                var draft = new ChannelDraftData { Text = _txtInput.Text };
                for (int i = 0; i < _chPending.Count; i++)
                {
                    var a = _chPending[i]; string file = $"file-{i}.bin";
                    string dataPath = Path.Combine(dir, file);
                    if (_channelDraftFilesDirty || !File.Exists(dataPath) || new FileInfo(dataPath).Length != a.Data.LongLength)
                        File.WriteAllBytes(dataPath, a.Data);
                    draft.Files.Add(new ChannelDraftFile { Name = a.Name, IsImg = a.IsImg, Data = file });
                }
                File.WriteAllText(Path.Combine(dir, "draft.json"), JsonSerializer.Serialize(draft));
                _channelDraftFilesDirty = false;
                UpdateChannelDraftPreview();
            }
            catch { }
        }
        private void RestoreChannelDraft()
        {
            if (_switchingChannelDraft || _channelId <= 0 || _txtInput == null) return;
            try
            {
                string dir = ChannelDraftDirectory(_channelId), path = Path.Combine(ChannelDraftDirectory(_channelId), "draft.json");
                if (!File.Exists(path)) return;
                var draft = JsonSerializer.Deserialize<ChannelDraftData>(File.ReadAllText(path));
                if (draft == null) return;
                _txtInput.Text = draft.Text ?? "";
                foreach (var a in draft.Files)
                {
                    string file = Path.Combine(dir, a.Data ?? "");
                    if (File.Exists(file)) StageChannelAttachment(File.ReadAllBytes(file), a.Name, a.IsImg);
                }
            }
            catch { }
        }
    }
}
