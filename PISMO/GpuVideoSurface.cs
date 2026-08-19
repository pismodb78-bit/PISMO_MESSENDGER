using System;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PISMO
{
    /// <summary>
    /// GPU-рендер входящего видео (демок): WPF Image внутри ElementHost.
    /// WPF рисует через DirectX (Milcore/DWM) — масштабирование и композиция
    /// кадра идут на видеокарте, в отличие от PictureBox (GDI+, CPU-скейл на
    /// каждый Paint). Кадры принимаются сырым BGRA без промежуточного BMP:
    /// WritePixels копирует буфер прямо в видеопамять WPF-поверхности.
    ///
    /// Вызывать PushFrame строго из UI-потока (диспетчер WPF = тот же STA-поток
    /// WinForms).
    /// </summary>
    internal sealed class GpuVideoSurface : ElementHost
    {
        private readonly System.Windows.Controls.Image _img;
        private WriteableBitmap _wb;

        /// <summary>Одиночный клик по видео.</summary>
        public event Action Clicked;
        /// <summary>Двойной клик по видео (развернуть/свернуть).</summary>
        public event Action DoubleClicked;
        /// <summary>Правый клик по видео (контекстное меню).</summary>
        public event Action RightClicked;

        public GpuVideoSurface()
        {
            BackColor = System.Drawing.Color.FromArgb(24, 25, 28);
            _img = new System.Windows.Controls.Image
            {
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            // Linear — билинейная фильтрация на GPU (быстро и гладко).
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.Linear);

            // Фон WPF-части — тот же тёмный, чтобы поля letterbox не белели.
            var host = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 25, 28)),
                Child = _img
            };
            Child = host;

            host.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) DoubleClicked?.Invoke();
                else Clicked?.Invoke();
            };
            host.MouseRightButtonUp += (s, e) => RightClicked?.Invoke();
        }

        /// <summary>Показ BGRA-кадра (packed, stride = w*4). UI-поток!</summary>
        public void PushFrame(byte[] bgra, int w, int h)
        {
            if (bgra == null || w <= 0 || h <= 0 || IsDisposed) return;
            try
            {
                if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
                {
                    _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    _img.Source = _wb;
                }
                _wb.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
            }
            catch
            {
                // Битмап в плохом состоянии — пересоздаём на следующем кадре,
                // иначе картинка «зависнет» навсегда на последнем удачном кадре.
                _wb = null;
            }
        }
    }
}
