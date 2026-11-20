using System.ComponentModel;
using System.Runtime.InteropServices;
using Pos.Desktop.Services;

namespace Pos.Desktop.Infrastructure;

/// <summary>
/// Sends bytes unchanged (datatype RAW) through the Windows print spooler, so the EPSON driver passes ESC/POS
/// commands (cut, drawer pulse) to the printer as they are.
/// </summary>
public sealed class WindowsRawPrinter : IReceiptPrinter
{
    public void PrintRaw(string printerName, byte[] data, string documentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(data);

        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new PrinterException($"printer tidak ditemukan atau tidak dapat dibuka ({LastError()}).");
        try
        {
            var info = new DocInfo { DocName = documentName, OutputFile = null, DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref info) == 0)
                throw new PrinterException($"spooler menolak pekerjaan cetak ({LastError()}).");
            try
            {
                if (!StartPagePrinter(handle))
                    throw new PrinterException($"halaman tidak dapat dimulai ({LastError()}).");

                var buffer = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, buffer, data.Length);
                    if (!WritePrinter(handle, buffer, data.Length, out var written) || written != data.Length)
                        throw new PrinterException($"data tidak terkirim lengkap ({LastError()}).");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int count, out int written);
}
