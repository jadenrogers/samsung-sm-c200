using System.Runtime.InteropServices;

namespace Gear360.Mtp.LibMtp.Native;

/// <summary>The managed side of one file download, handed to libmtp's callbacks through a <see cref="GCHandle"/>.</summary>
internal sealed class TransferState(Stream destination, IProgress<long>? bytesProgress, CancellationToken cancellationToken)
{
    public Stream Destination { get; } = destination;

    public IProgress<long>? BytesProgress { get; } = bytesProgress;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Bytes written to <see cref="Destination"/> so far.</summary>
    public long BytesWritten { get; set; }

    /// <summary>True when a callback asked libmtp to stop because the token was cancelled.</summary>
    public bool Cancelled { get; set; }

    /// <summary>The first exception thrown while writing, rethrown once libmtp returns.</summary>
    public Exception? Error { get; set; }
}

/// <summary>
/// Callbacks passed to <c>LIBMTP_Get_File_To_Handler</c>. They are static
/// <see cref="UnmanagedCallersOnly"/> functions, so there is no delegate for the GC to collect while
/// libmtp holds the pointer; per-transfer state travels in the <c>priv</c>/<c>data</c> pointer as a
/// <see cref="GCHandle"/> to a <see cref="TransferState"/>.
/// </summary>
/// <remarks>Exceptions must never cross back into native code, so each callback catches everything.</remarks>
internal static unsafe class TransferCallbacks
{
    /// <summary>Pointer to <see cref="Put"/>, an <c>MTPDataPutFunc</c>.</summary>
    public static delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint*, ushort> PutFunction => &Put;

    /// <summary>Pointer to <see cref="Progress"/>, a <c>LIBMTP_progressfunc_t</c>.</summary>
    public static delegate* unmanaged<ulong, ulong, IntPtr, int> ProgressFunction => &Progress;

    /// <summary>
    /// <c>uint16_t put_func(void *params, void *priv, uint32_t sendlen, unsigned char *data, uint32_t *putlen)</c>:
    /// writes one chunk of the file to the destination stream.
    /// </summary>
    [UnmanagedCallersOnly]
    private static ushort Put(IntPtr parameters, IntPtr priv, uint sendLength, byte* data, uint* putLength)
    {
        if (putLength != null)
        {
            *putLength = 0;
        }

        if (GetState(priv) is not { } state)
        {
            return LibMtpNative.HandlerReturnError;
        }

        try
        {
            if (state.CancellationToken.IsCancellationRequested)
            {
                state.Cancelled = true;
                return LibMtpNative.HandlerReturnCancel;
            }

            if (sendLength > 0)
            {
                if (data == null)
                {
                    throw new LibMtpException("libmtp passed no data for a non-empty chunk.");
                }

                // ReadOnlySpan<byte> is limited to int.MaxValue bytes; libmtp chunks are far smaller.
                state.Destination.Write(new ReadOnlySpan<byte>(data, checked((int)sendLength)));
                state.BytesWritten += sendLength;
                state.BytesProgress?.Report(state.BytesWritten);
            }

            if (putLength != null)
            {
                *putLength = sendLength;
            }

            return LibMtpNative.HandlerReturnOk;
        }
        catch (Exception ex)
        {
            state.Error ??= ex;
            return LibMtpNative.HandlerReturnError;
        }
    }

    /// <summary>
    /// <c>int progress(uint64_t const sent, uint64_t const total, void const * const data)</c>:
    /// returns non-zero to cancel the transfer.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int Progress(ulong sent, ulong total, IntPtr data)
    {
        if (GetState(data) is not { } state)
        {
            return 1;
        }

        if (state.CancellationToken.IsCancellationRequested)
        {
            state.Cancelled = true;
            return 1;
        }

        return 0;
    }

    private static TransferState? GetState(IntPtr handle)
    {
        try
        {
            return handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target as TransferState;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
