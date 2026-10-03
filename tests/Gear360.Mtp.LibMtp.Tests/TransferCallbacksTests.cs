using System.Runtime.InteropServices;

using Gear360.Mtp.LibMtp.Native;

namespace Gear360.Mtp.LibMtp.Tests;

/// <summary>
/// Calls the libmtp callbacks through the same unmanaged function pointers libmtp receives, with
/// native buffers, to check what they write and what they return.
/// </summary>
public sealed unsafe class TransferCallbacksTests
{
    [Fact]
    public void Put_writes_each_chunk_and_reports_running_total()
    {
        using var destination = new MemoryStream();
        var reported = new List<long>();
        var state = new TransferState(destination, new SyncProgress(reported.Add), CancellationToken.None);

        var first = Put(state, [1, 2, 3], out var put1);
        var second = Put(state, [4, 5], out var put2);

        Assert.Equal(LibMtpNative.HandlerReturnOk, first);
        Assert.Equal(LibMtpNative.HandlerReturnOk, second);
        Assert.Equal(3u, put1);
        Assert.Equal(2u, put2);
        Assert.Equal([1, 2, 3, 4, 5], destination.ToArray());
        Assert.Equal([3L, 5L], reported);
        Assert.Equal(5, state.BytesWritten);
    }

    [Fact]
    public void Put_returns_cancel_when_the_token_is_cancelled()
    {
        using var destination = new MemoryStream();
        var state = new TransferState(destination, null, new CancellationToken(canceled: true));

        var result = Put(state, [1], out var putLength);

        Assert.Equal(LibMtpNative.HandlerReturnCancel, result);
        Assert.Equal(0u, putLength);
        Assert.True(state.Cancelled);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void Put_captures_stream_errors_instead_of_throwing_into_native_code()
    {
        using var destination = new MemoryStream([], writable: false);
        var state = new TransferState(destination, null, CancellationToken.None);

        var result = Put(state, [1, 2], out var putLength);

        Assert.Equal(LibMtpNative.HandlerReturnError, result);
        Assert.Equal(0u, putLength);
        Assert.IsType<NotSupportedException>(state.Error);
    }

    [Fact]
    public void Put_without_state_returns_error()
    {
        uint putLength = 99;
        byte value = 1;
        var result = TransferCallbacks.PutFunction(IntPtr.Zero, IntPtr.Zero, 1, &value, &putLength);

        Assert.Equal(LibMtpNative.HandlerReturnError, result);
        Assert.Equal(0u, putLength);
    }

    [Fact]
    public void Progress_continues_until_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var state = new TransferState(Stream.Null, null, cancellation.Token);
        var handle = GCHandle.Alloc(state);
        try
        {
            var data = GCHandle.ToIntPtr(handle);
            Assert.Equal(0, TransferCallbacks.ProgressFunction(10, 100, data));

            cancellation.Cancel();
            Assert.NotEqual(0, TransferCallbacks.ProgressFunction(20, 100, data));
            Assert.True(state.Cancelled);
        }
        finally
        {
            handle.Free();
        }
    }

    private static ushort Put(TransferState state, byte[] chunk, out uint putLength)
    {
        var handle = GCHandle.Alloc(state);
        var buffer = (byte*)NativeMemory.Alloc((nuint)Math.Max(chunk.Length, 1));
        try
        {
            chunk.CopyTo(new Span<byte>(buffer, chunk.Length));
            uint put = 12345;
            var result = TransferCallbacks.PutFunction(IntPtr.Zero, GCHandle.ToIntPtr(handle), (uint)chunk.Length, buffer, &put);
            putLength = put;
            return result;
        }
        finally
        {
            NativeMemory.Free(buffer);
            handle.Free();
        }
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
