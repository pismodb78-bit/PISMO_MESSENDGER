using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Небольшое окно превью перед включением камеры или демонстрации экрана.
    /// Показывает локальный поток ("что увидит собеседник") и требует
    /// явного подтверждения через кнопку "Включить" — отмена останавливает
    /// захват без добавления трека в звонок.
    ///
    /// Для камеры дополнительно показывает выпадающий список устройств,
    /// позволяя сменить камеру/микрофон без выхода из превью.
    /// </summary>
    public class MediaPreviewForm : Form
    {
        private readonly PictureBox _pbPreview;
        private readonly Button _btnConfirm;
        private readonly Button _btnCancel;
        private readonly ComboBox _cmbDevice;
        private readonly Label _lblTitle;
        private readonly Label _lblHint;

        /// <summary>Срабатывает при нажатии "Включить".</summary>
        public event Action Confirmed;

        /// <summary>Срабатывает при нажатии "Отмена" или закрытии окна без подтверждения.</summary>
        public event Action Cancelled;

        /// <summary>Срабатывает при выборе другого устройства в списке (для камеры/микрофона).</summary>
        public event Action<string> DeviceChanged;

        private bool _confirmed = false;

        public MediaPreviewForm(string title, bool showDevicePicker)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, showDevicePicker ? 360 : 320);
            BackColor = Color.FromArgb(30, 31, 34);

            _lblTitle = new Label
            {
                Text = title,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 14)
            };

            _pbPreview = new PictureBox
            {
                BackColor = Color.FromArgb(20, 21, 23),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                Size = new Size(388, 218),
                Location = new Point(16, 46)
            };

            _lblHint = new Label
            {
                Text = "Это увидит собеседник после подтверждения",
                ForeColor = Color.FromArgb(150, 152, 157),
                AutoSize = true,
                Location = new Point(16, 270)
            };

            int buttonsTop = 296;

            if (showDevicePicker)
            {
                _cmbDevice = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(16, 270),
                    Size = new Size(388, 26)
                };
                _lblHint.Location = new Point(16, 300);
                buttonsTop = 326;
                _cmbDevice.SelectedIndexChanged += (s, e) =>
                {
                    if (_cmbDevice.SelectedItem != null)
                        DeviceChanged?.Invoke(_cmbDevice.SelectedItem.ToString());
                };
            }

            _btnCancel = new Button
            {
                Text = "Отмена",
                Size = new Size(110, 32),
                Location = new Point(220, buttonsTop),
                BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Click += (s, e) => { _confirmed = false; Close(); };

            _btnConfirm = new Button
            {
                Text = "Включить",
                Size = new Size(110, 32),
                Location = new Point(336, buttonsTop),
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnConfirm.FlatAppearance.BorderSize = 0;
            _btnConfirm.Click += (s, e) => { _confirmed = true; Confirmed?.Invoke(); Close(); };

            var controls = new System.Collections.Generic.List<Control>
                { _lblTitle, _pbPreview, _lblHint, _btnCancel, _btnConfirm };
            if (showDevicePicker) controls.Add(_cmbDevice);
            Controls.AddRange(controls.ToArray());

            FormClosing += (s, e) =>
            {
                if (!_confirmed) Cancelled?.Invoke();
            };
        }

        /// <summary>Обновляет картинку превью новым JPEG-кадром. Безопасно вызывать
        /// из фонового потока — переключается на UI-поток самостоятельно.</summary>
        public void UpdateFrame(byte[] jpegBytes)
        {
            if (IsDisposed || !IsHandleCreated) return;
            Bitmap img;
            try
            {
                using var ms = new MemoryStream(jpegBytes);
                img = new Bitmap(ms);
            }
            catch { return; }

            try
            {
                BeginInvoke(() =>
                {
                    if (_pbPreview.IsDisposed) { img.Dispose(); return; }
                    var old = _pbPreview.Image;
                    _pbPreview.Image = img;
                    old?.Dispose();
                });
            }
            catch (ObjectDisposedException) { img.Dispose(); }
        }

        /// <summary>Заполняет список устройств (для камеры/микрофона) и
        /// выбирает текущее по имени, если оно есть в списке.</summary>
        /// <summary>Включает/выключает кнопку "Включить" — используется, чтобы
        /// не дать подтвердить превью раньше, чем поток реально захвачен
        /// (например, пока пользователь ещё не выбрал экран/окно в системном
        /// диалоге, или пока getUserMedia ещё не отработал).</summary>
        public void SetConfirmEnabled(bool enabled)
        {
            if (IsDisposed || !IsHandleCreated) { _btnConfirm.Enabled = enabled; return; }
            try { BeginInvoke(() => { if (!_btnConfirm.IsDisposed) _btnConfirm.Enabled = enabled; }); }
            catch (ObjectDisposedException) { }
        }

        public void SetDeviceList(string[] deviceNames, string currentDeviceName)
        {
            if (_cmbDevice == null) return;
            _cmbDevice.Items.Clear();
            _cmbDevice.Items.AddRange(deviceNames);
            if (!string.IsNullOrEmpty(currentDeviceName))
            {
                int idx = _cmbDevice.Items.IndexOf(currentDeviceName);
                if (idx >= 0) _cmbDevice.SelectedIndex = idx;
            }
            else if (_cmbDevice.Items.Count > 0)
            {
                _cmbDevice.SelectedIndex = 0;
            }
        }
    }
}
