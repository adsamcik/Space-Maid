using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace SpaceMaid.Core.Scanning;

internal readonly record struct NtfsLayoutQueryResult(int Bytes, int Error, double Milliseconds);

// One native iterator, with at most two owned buffers and one queued result.
// The consumer still borrows each buffer only until its callback returns.
internal static class NtfsLayoutAcquisition
{
    internal const int MaximumBufferBytes = 64 * 1024 * 1024;

    internal static void Acquire(Func<byte[], int, NtfsLayoutQueryResult> query, int initialBufferBytes,
        bool readAhead, Action<NtfsLayoutBlock> consume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!readAhead)
        {
            using var slot = new BufferSlot(initialBufferBytes);
            int size = initialBufferBytes;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                slot.EnsureSize(size);
                var result = query(slot.Buffer, size);
                consume(Block(slot, result, size));
                if (!Continue(result.Error, ref size)) return;
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var free = new BlockingCollection<BufferSlot>(2);
        using var pending = new BlockingCollection<(BufferSlot Slot, NtfsLayoutBlock Block)>(1);
        using var first = new BufferSlot(initialBufferBytes);
        using var second = new BufferSlot(initialBufferBytes);
        free.Add(first); free.Add(second);
        ExceptionDispatchInfo? failure = null;
        // A dedicated producer avoids thread-pool starvation while a native call
        // or pipe callback blocks. Only this thread touches the volume iterator.
        var producer = Task.Factory.StartNew(Produce, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            foreach (var item in pending.GetConsumingEnumerable(stop.Token))
            {
                try { consume(item.Block); }
                catch { stop.Cancel(); throw; }
                finally { free.Add(item.Slot); }
            }
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == stop.Token)
        { throw new OperationCanceledException(token); }
        finally
        {
            stop.Cancel();
            // Never dispose a handle, return a borrowed buffer, or start the next
            // request while the producer can still be inside DeviceIoControl.
            producer.GetAwaiter().GetResult();
        }
        token.ThrowIfCancellationRequested();
        failure?.Throw();

        void Produce()
        {
            try
            {
                int size = initialBufferBytes;
                while (true)
                {
                    var slot = free.Take(stop.Token);
                    slot.EnsureSize(size);
                    stop.Token.ThrowIfCancellationRequested();
                    var result = query(slot.Buffer, size);
                    pending.Add((slot, Block(slot, result, size)), stop.Token);
                    if (!Continue(result.Error, ref size)) return;
                }
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally { pending.CompleteAdding(); }
        }
    }

    private static NtfsLayoutBlock Block(BufferSlot slot, NtfsLayoutQueryResult result, int size)
    {
        if (result.Error != 0) return new(ReadOnlyMemory<byte>.Empty, result.Milliseconds);
        if (result.Bytes < 16 || result.Bytes > size)
            throw new InvalidDataException("Invalid NTFS metadata response length.");
        return new(slot.Buffer.AsMemory(0, result.Bytes), result.Milliseconds);
    }

    private static bool Continue(int error, ref int size)
    {
        if (error == 0) return true;
        if (error == NativeMethods.HandleEof) return false;
        if (error is 122 or 234 && size < MaximumBufferBytes)
        { size = Math.Min(size * 2, MaximumBufferBytes); return true; }
        throw new Win32Exception(error);
    }

    private sealed class BufferSlot(int size) : IDisposable
    {
        internal byte[] Buffer { get; private set; } = ArrayPool<byte>.Shared.Rent(size);

        internal void EnsureSize(int needed)
        {
            if (Buffer.Length >= needed) return;
            // This slot is free, so return its old storage before renting the
            // replacement. Keep disposal safe if a larger allocation fails.
            byte[] previous = Buffer;
            Buffer = [];
            ArrayPool<byte>.Shared.Return(previous);
            Buffer = ArrayPool<byte>.Shared.Rent(needed);
        }

        public void Dispose()
        {
            if (Buffer.Length != 0) ArrayPool<byte>.Shared.Return(Buffer);
        }
    }
}
