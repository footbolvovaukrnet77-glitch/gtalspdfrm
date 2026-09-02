using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using GTA.Native;

namespace Gtamp.Client.Shv.Interop
{
    /// <summary>
    /// Strings across the native boundary, without ScriptHookVDotNet's marshalling.
    /// <para>
    /// <b>Why this exists.</b> Handing a <c>string</c> to <c>Function.Call</c> looks free
    /// — an implicit conversion to <c>InputArgument</c> — but that conversion pins the
    /// string through <c>SHVDN.ScriptDomain.PinString</c>, which calls
    /// <c>NativeMemory.StringToCoTaskMemUTF8</c>. Reading a string back with
    /// <c>Function.Call&lt;string&gt;</c> goes through <c>NativeMemory.PtrToStringUTF8</c>
    /// for the same reason. <c>NativeMemory</c> builds its address map by scanning the
    /// game for byte patterns and fails outright on a game build the installed
    /// ScriptHookVDotNet does not know — so on such a build a native call carrying a
    /// string throws where the identical call carrying numbers succeeds.
    /// </para>
    /// <para>
    /// That is not a hypothetical. The client's own load notification is a string
    /// argument, it threw out of the script constructor, and the client stopped starting
    /// at all on a build where it had previously run.
    /// </para>
    /// <para>
    /// So the bytes are pinned and read here instead, with <see cref="Marshal"/>, and the
    /// pointer is passed through the one <see cref="InputArgument"/> constructor that
    /// takes an <see cref="IntPtr"/> and does nothing else.
    /// </para>
    /// </summary>
    public static class NativeString
    {
        /// <summary>
        /// What one <c>ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME</c> accepts, in bytes.
        /// The game truncates past it rather than refusing, so the caller has to count.
        /// </summary>
        public const int ComponentByteLimit = 99;

        /// <summary>
        /// Buffers pinned for the native call in progress. All native work happens on the
        /// one script thread, so a single list serves and a per-call allocation does not.
        /// </summary>
        private static readonly List<IntPtr> Pinned = new List<IntPtr>();

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// The string as a native argument. The caller <b>must</b> call
        /// <see cref="Release"/> in a <c>finally</c> once the native command has ended —
        /// the game copies the text while the command runs, so the buffer has to outlive
        /// the call and nothing else.
        /// </summary>
        public static InputArgument Arg(string? text)
        {
            byte[] bytes = Utf8.GetBytes(text ?? string.Empty);
            IntPtr buffer = Marshal.AllocCoTaskMem(bytes.Length + 1);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteByte(buffer, bytes.Length, 0);
            Pinned.Add(buffer);
            return new InputArgument(buffer);
        }

        /// <summary>
        /// The string as the components a text command accepts, split on bytes because
        /// that is what the game counts. Same contract as <see cref="Arg"/>.
        /// </summary>
        public static IEnumerable<InputArgument> Components(string? text)
        {
            foreach (string chunk in Gtamp.Client.Ui.TextChunker.SplitUtf8(text, ComponentByteLimit))
            {
                yield return Arg(chunk);
            }
        }

        /// <summary>Frees everything pinned since the last release.</summary>
        public static void Release()
        {
            for (int i = 0; i < Pinned.Count; i++)
            {
                Marshal.FreeCoTaskMem(Pinned[i]);
            }

            Pinned.Clear();
        }

        /// <summary>
        /// A string returned by a native, read as the caller rather than through
        /// <c>Function.Call&lt;string&gt;</c>. The game owns the memory; nothing is freed
        /// here. Returns an empty string for a null pointer, which is what a native means
        /// by "no such thing".
        /// </summary>
        public static string Read(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Utf8.GetString(bytes);
        }
    }
}
