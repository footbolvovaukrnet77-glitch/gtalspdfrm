using System;
using System.Threading;
using System.Windows.Forms;
using Gtamp.Client.Ui;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Clipboard access for the "copy error" and "create bug report" actions.
    /// <para>
    /// The Windows clipboard requires an STA thread, and the ScriptHookVDotNet
    /// script thread is not one, so each copy runs on a short-lived STA thread. It
    /// is slow, but it happens only when a human presses a key.
    /// </para>
    /// </summary>
    public sealed class WindowsClipboard : IClipboard
    {
        private readonly LogBus _log;

        public WindowsClipboard(LogBus log)
        {
            _log = log;
        }

        public void SetText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch (Exception exception)
                    {
                        _log.Warning(LogCategory.Console, "Clipboard write failed: " + exception.Message);
                    }
                });

                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join(2000);
            }
            catch (Exception exception)
            {
                _log.Warning(LogCategory.Console, "Could not start the clipboard thread: " + exception.Message);
            }
        }
    }
}
