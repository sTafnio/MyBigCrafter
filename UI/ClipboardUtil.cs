using System;
using System.Runtime.InteropServices;

namespace MyBigCrafter.UI;

/// <summary>
/// Direct Win32 clipboard access for the Share buttons. ImGui's own clipboard depends on how the host wired
/// its backend (cimgui builds can silently fall back to an in-process buffer, and native text can arrive with
/// stray terminators), which made share strings unreliable - talking to the OS clipboard directly removes
/// that whole failure class.
/// </summary>
internal static class ClipboardUtil
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Clipboard text, or null when it holds no text (or is briefly locked by another app).</summary>
    public static string GetText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Puts text on the OS clipboard. False when the clipboard couldn't be claimed (retry-able).</summary>
    public static bool SetText(string text)
    {
        if (text == null) return false;
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) { GlobalFree(handle); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);   // null terminator
            }
            finally { GlobalUnlock(handle); }

            // On success the system owns the handle; free it only when handing it over failed.
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero) { GlobalFree(handle); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
}
