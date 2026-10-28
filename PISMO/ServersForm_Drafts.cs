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
        /// <summary>
        /// Подпись канала в списке: «Черновик» — только у ЗАКРЫТОГО канала, где
        /// что-то осталось недописанным; в открытом — обычное последнее
        /// сообщение (черновик и так в поле ввода). Раньше метка ставилась на
        /// каждое нажатие клавиши и сразу при входе в канал.
        /// </summary>
        private void UpdateChannelDraftPreview(int channel)
        {
            if (channel <= 0 || !_channelPreviewCtl.TryGetValue(channel, out var ctl) || ctl.IsDisposed) return;
            bool draft = channel != _channelId && HasChannelDraft(channel);
            string text = draft ? "Черновик" : (_channelPreviews.TryGetValue(channel, out var t) ? t : "");
            // Подпись бывает картинкой (эмодзи рисуются ею) — меняем контрол
            // целиком, а не его текст.
            var host = ctl.Parent;
            if (host == null) return;
            var loc = ctl.Location; var size = ctl.Size;
            var click = host.AccessibleName ?? "";
            host.Controls.Remove(ctl);
            try { ctl.Dispose(); } catch { }
            var pc = MainForm.MakePreviewControl(text, loc, size);
            pc.ForeColor = draft ? System.Drawing.Color.FromArgb(235, 75, 85) : System.Drawing.Color.FromArgb(185, 190, 198);
            int cid = channel;
            pc.Click += (s, e) => SelectChannel(cid, "text", click);
            host.Controls.Add(pc);
            _channelPreviewCtl[channel] = pc;
        }

        /// <summary>Сохранение по ходу набора — не на каждую букву, а когда набор утих.</summary>
        private System.Windows.Forms.Timer _chDraftTimer;
        private void ScheduleChannelDraftSave()
        {
            if (_chDraftTimer == null)
            {
                _chDraftTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _chDraftTimer.Tick += (s, e) => { _chDraftTimer.Stop(); SaveChannelDraft(); };
            }
            _chDraftTimer.Stop();
            _chDraftTimer.Start();
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
