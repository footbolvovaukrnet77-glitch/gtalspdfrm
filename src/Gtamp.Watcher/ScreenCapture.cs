using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Grabs the screen when something goes wrong, because some defects have no
    /// text: a car under the map, a camera in the air, a character behind glass.
    /// <para>
    /// KNOWN LIMITATION, stated because it will bite: a copy of the desktop shows
    /// GTA V only when the game is <b>windowed or borderless</b>. In exclusive
    /// fullscreen the game owns the display and the copy comes back black or shows
    /// the desktop behind it. There is no way around that from outside the process
    /// short of hooking DirectX, which is a different kind of program and is not
    /// this one. Set the game to borderless windowed if you want the pictures.
    /// </para>
    /// <para>
    /// A capture is also never published unless asked for separately: a screenshot
    /// cannot be redacted. It may carry a player name, the overlay's server
    /// address, and whatever else is on the screen.
    /// </para>
    /// </summary>
    public static class ScreenCapture
    {
        public static bool Supported => OperatingSystem.IsWindows();

        /// <summary>
        /// Writes a PNG of the primary screen. Returns null when it could not, with
        /// the reason — never an exception, because a failed screenshot must not
        /// cost the text record that goes with it.
        /// </summary>
        public static string? TryCapture(string path, out string reason)
        {
            if (!OperatingSystem.IsWindows())
            {
                reason = "скриншоты только под Windows";
                return null;
            }

            try
            {
                return CaptureWindows(path, out reason);
            }
            catch (DllNotFoundException exception)
            {
                reason = "не нашлись системные библиотеки: " + exception.Message;
                return null;
            }
            catch (Exception exception)
            {
                reason = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string? CaptureWindows(string path, out string reason)
        {
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
            {
                reason = "система не сообщила размер экрана";
                return null;
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
            IntPtr previous = SelectObject(memoryDc, bitmap);

            try
            {
                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SRCCOPY | CAPTUREBLT))
                {
                    reason = "BitBlt отказал — обычно это полноэкранный эксклюзивный режим";
                    return null;
                }

                byte[] pixels = ReadPixels(memoryDc, bitmap, width, height);
                if (IsUniform(pixels))
                {
                    // A black rectangle is what exclusive fullscreen returns. Saying
                    // so beats shipping a file that looks like evidence and is not.
                    reason = "снимок вышел пустым — переведите игру в оконный без рамки";
                    return null;
                }

                File.WriteAllBytes(path, Png.Encode(pixels, width, height));
                reason = string.Empty;
                return path;
            }
            finally
            {
                SelectObject(memoryDc, previous);
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [SupportedOSPlatform("windows")]
        private static byte[] ReadPixels(IntPtr dc, IntPtr bitmap, int width, int height)
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,

                // Negative height asks GDI for a top-down image, which is the order
                // PNG wants; otherwise every row would have to be flipped by hand.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            byte[] buffer = new byte[width * height * 4];
            GetDIBits(dc, bitmap, 0, (uint)height, buffer, ref header, 0);
            return buffer;
        }

        private static bool IsUniform(byte[] pixels)
        {
            if (pixels.Length < 16)
            {
                return true;
            }

            byte b = pixels[0], g = pixels[1], r = pixels[2];
            for (int i = 4; i < pixels.Length; i += 4)
            {
                if (pixels[i] != b || pixels[i + 1] != g || pixels[i + 2] != r)
                {
                    return false;
                }
            }

            return true;
        }

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int operation);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr dc, IntPtr bitmap, uint start, uint lines,
            byte[] pixels, ref BITMAPINFOHEADER header, uint usage);
    }
}
