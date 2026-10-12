using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

namespace SpaceMaid.Core.Scanning;

// One authenticated connection, at most one acquisition and one buffered batch.
// The command reader stays active during acquisition so cancellation/disconnect
// also works while the native worker is waiting for the host to consume a batch.
internal static class ScanPipeSession
{
    internal static async Task RunAsync(Stream pipe, INtfsLayoutSource source)
    {
        object stateGate = new();
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? active = null;
        long activeRequest = 0, lastRequest = 0;
        Task worker = Task.CompletedTask;
        try
        {
            while (true)
            {
                var frame = ScanPipeProtocol.ReadHeader(pipe);
                if (frame.Kind == ScanFrameKind.Cancel && frame.Length == 8)
                {
                    byte[] cancel = new byte[8]; pipe.ReadExactly(cancel);
                    long request = BinaryPrimitives.ReadInt64LittleEndian(cancel);
                    lock (stateGate)
                        if (request == activeRequest) active?.Cancel();
                    continue;
                }
                if (frame.Kind != ScanFrameKind.Request || frame.Length != 11)
                    throw new InvalidDataException("Unexpected scan command.");
                byte[] payload = new byte[11]; pipe.ReadExactly(payload);
                long id = BinaryPrimitives.ReadInt64LittleEndian(payload);
                string volume = Encoding.ASCII.GetString(payload, 8, 3);
                lock (stateGate)
                {
                    if (active is not null || id <= lastRequest)
                        throw new InvalidDataException("Scan requests must be serial and uniquely identified.");
                    lastRequest = activeRequest = id;
                    active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var requestCancellation = active;
                    worker = Task.Run(() => Acquire(id, volume, requestCancellation));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        finally
        {
            // Closing the stream releases a worker blocked on pipe backpressure.
            // A lost host always ends the session, including while it is idle.
            lifetime.Cancel(); pipe.Dispose();
            await worker.ConfigureAwait(false);
        }

        void Acquire(long id, string volume, CancellationTokenSource cancellation)
        {
            Exception? failure = null;
            try
            {
                NativeNtfsLayoutSource.ValidateVolume(volume);
                source.Acquire(volume, block =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    ScanPipeProtocol.WriteBlock(pipe, id, block);
                }, cancellation.Token);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    lock (stateGate)
                    {
                        active = null;
                        if (!lifetime.IsCancellationRequested)
                        {
                            byte[] result = BitConverter.GetBytes(id);
                            if (cancellation.IsCancellationRequested)
                                ScanPipeProtocol.Write(pipe, ScanFrameKind.Cancelled, result);
                            else if (failure is not null)
                            {
                                byte[] message = Encoding.UTF8.GetBytes(failure.Message[..Math.Min(1024, failure.Message.Length)]);
                                byte[] error = new byte[12 + message.Length]; result.CopyTo(error, 0);
                                BinaryPrimitives.WriteInt32LittleEndian(error.AsSpan(8), (failure as Win32Exception)?.NativeErrorCode ?? 13);
                                message.CopyTo(error, 12); ScanPipeProtocol.Write(pipe, ScanFrameKind.Failure, error);
                            }
                            else ScanPipeProtocol.Write(pipe, ScanFrameKind.Complete, result);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                { lifetime.Cancel(); pipe.Dispose(); }
                finally { cancellation.Dispose(); }
            }
        }
    }
}
