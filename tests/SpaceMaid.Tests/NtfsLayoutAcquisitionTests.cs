using System.ComponentModel;
using System.IO;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class NtfsLayoutAcquisitionTests
{
    private const int InitialBufferBytes = 64 * 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(64 * 1024 - 1)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public void NativeSourceRejectsUnsupportedBufferSizes(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeNtfsLayoutSource(size));
    }

    [Fact]
    public void NativeSourceRetainsExplicitBufferAndReadAheadConfiguration()
    {
        var serial = new NativeNtfsLayoutSource(InitialBufferBytes, readAhead: false);
        var buffered = new NativeNtfsLayoutSource(8 * 1024 * 1024, readAhead: true);
        Assert.Equal(InitialBufferBytes, serial.InitialBufferBytes); Assert.False(serial.ReadAhead);
        Assert.Equal(8 * 1024 * 1024, buffered.InitialBufferBytes); Assert.True(buffered.ReadAhead);
    }

    [Fact]
    public void ReadAheadOverlapsConsumptionWithOnlyTwoOutstandingQueriesAndStableBorrowedMemory()
    {
        using var secondQuery = new ManualResetEventSlim();
        int caller = Environment.CurrentManagedThreadId, producerThread = 0;
        int queries = 0, outstanding = 0, maximumOutstanding = 0, activeQueries = 0;
        var delivered = new List<byte>();

        NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref activeQueries));
            try
            {
                int currentThread = Environment.CurrentManagedThreadId;
                if (producerThread == 0) producerThread = currentThread;
                Assert.Equal(producerThread, currentThread); Assert.NotEqual(caller, currentThread);
                int pending = Interlocked.Increment(ref outstanding);
                Assert.InRange(pending, 1, 2);
                maximumOutstanding = Math.Max(maximumOutstanding, pending);
                int query = ++queries;
                if (query == 9) return new(0, 38, query);
                buffer.AsSpan(0, 16).Fill((byte)query);
                if (query == 2) secondQuery.Set();
                return new(16, 0, query);
            }
            finally { Interlocked.Decrement(ref activeQueries); }
        }, InitialBufferBytes, readAhead: true, block =>
        {
            try
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                if (block.Data.IsEmpty) return;
                byte expected = (byte)(delivered.Count + 1);
                if (expected == 1)
                    Assert.True(secondQuery.Wait(TimeSpan.FromSeconds(5)), "The next query did not overlap the first callback.");
                Assert.All(block.Data.ToArray(), value => Assert.Equal(expected, value));
                delivered.Add(expected);
            }
            finally { Interlocked.Decrement(ref outstanding); }
        }, default);

        Assert.Equal(Enumerable.Range(1, 8).Select(value => (byte)value), delivered);
        Assert.Equal(2, maximumOutstanding); Assert.Equal(0, outstanding); Assert.Equal(0, activeQueries);
        Assert.Equal(9, queries);
    }

    [Fact]
    public void SerialModeQueriesAndConsumesOnTheCallerWithoutReadingAhead()
    {
        int caller = Environment.CurrentManagedThreadId, queries = 0, callbacks = 0;
        bool consuming = false;
        NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId); Assert.False(consuming);
            Assert.Equal(queries, callbacks);
            int query = ++queries;
            if (query == 4) return new(0, 38, 1);
            buffer.AsSpan(0, 16).Fill((byte)query);
            return new(16, 0, 1);
        }, InitialBufferBytes, readAhead: false, block =>
        {
            consuming = true;
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            Assert.Equal(++callbacks, queries);
            if (!block.Data.IsEmpty)
                Assert.All(block.Data.ToArray(), value => Assert.Equal((byte)queries, value));
            consuming = false;
        }, default);
        Assert.Equal(4, queries); Assert.Equal(4, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallBuffersGrowAndEveryAttemptRetainsItsTimingIncludingEndOfFile(bool readAhead)
    {
        int queries = 0;
        var sizes = new List<int>();
        var results = new List<(bool Empty, double Milliseconds)>();
        NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            sizes.Add(size);
            int query = ++queries;
            return query switch
            {
                1 => new(0, 122, 1),
                2 => new(0, 234, 2),
                3 => new(16, 0, 3),
                _ => new(0, 38, 4)
            };
        }, InitialBufferBytes, readAhead, block => results.Add((block.Data.IsEmpty, block.QueryMilliseconds)), default);

        Assert.Equal(new[] { InitialBufferBytes, InitialBufferBytes * 2, InitialBufferBytes * 4, InitialBufferBytes * 4 }, sizes);
        Assert.Equal(new[] { (true, 1d), (true, 2d), (false, 3d), (true, 4d) }, results);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumerFailurePreservesItsExceptionAndStopsAcquisitionBeforeReturning(bool readAhead)
    {
        int queries = 0, active = 0, callbacks = 0;
        var failure = new IOException("Owned consumer failure.");
        var actual = Assert.Throws<IOException>(() => NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            Interlocked.Increment(ref active);
            try { Interlocked.Increment(ref queries); return new(16, 0, 1); }
            finally { Interlocked.Decrement(ref active); }
        }, InitialBufferBytes, readAhead, _ =>
        {
            callbacks++;
            throw failure;
        }, default));

        Assert.Same(failure, actual); Assert.Equal(1, callbacks); Assert.Equal(0, active);
        Assert.InRange(queries, 1, readAhead ? 2 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStopsAcquisitionWithoutPublishingQueuedBatches(bool readAhead)
    {
        using var cancellation = new CancellationTokenSource();
        int queries = 0, active = 0, callbacks = 0;
        var error = Assert.ThrowsAny<OperationCanceledException>(() => NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            Interlocked.Increment(ref active);
            try { Interlocked.Increment(ref queries); return new(16, 0, 1); }
            finally { Interlocked.Decrement(ref active); }
        }, InitialBufferBytes, readAhead, _ =>
        {
            callbacks++;
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, callbacks); Assert.Equal(0, active);
        Assert.InRange(queries, 1, readAhead ? 2 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFailureIsReportedAfterItsEarlierBatchesAndTiming(bool readAhead)
    {
        int queries = 0;
        var callbacks = new List<(bool Empty, double Milliseconds)>();
        var error = Assert.Throws<Win32Exception>(() => NtfsLayoutAcquisition.Acquire((buffer, size) =>
            ++queries == 1 ? new(16, 0, 1.5) : new(0, 5, 2.5), InitialBufferBytes, readAhead,
            block => callbacks.Add((block.Data.IsEmpty, block.QueryMilliseconds)), default));

        Assert.Equal(5, error.NativeErrorCode); Assert.Equal(2, queries);
        Assert.Equal(new[] { (false, 1.5), (true, 2.5) }, callbacks);
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 15)]
    [InlineData(false, InitialBufferBytes + 1)]
    [InlineData(true, InitialBufferBytes + 1)]
    public void InvalidNativeResponseLengthNeverReachesTheConsumer(bool readAhead, int bytes)
    {
        int queries = 0, callbacks = 0;
        Assert.Throws<InvalidDataException>(() => NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            queries++;
            return new(bytes, 0, 1);
        }, InitialBufferBytes, readAhead, _ => callbacks++, default));
        Assert.Equal(1, queries); Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyCancelledAcquisitionDoesNotIssueAQuery(bool readAhead)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int queries = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => NtfsLayoutAcquisition.Acquire((buffer, size) =>
        {
            queries++;
            return new(0, 38, 1);
        }, InitialBufferBytes, readAhead, _ => Assert.Fail("Cancelled acquisition delivered a callback."), cancellation.Token));
        Assert.Equal(0, queries);
    }
}
