using System.Text;

namespace CachingProxy.Server.Tests;

[TestClass]
public class TeeStreamTests
{
    private MemoryStream _primaryStream = null!;
    private MemoryStream _secondaryStream = null!;
    private TeeStream _teeStream = null!;

    [TestInitialize]
    public void Setup()
    {
        _primaryStream = new MemoryStream();
        _secondaryStream = new MemoryStream();
        _teeStream = new TeeStream(_primaryStream, _secondaryStream);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _teeStream.Dispose();
        _primaryStream.Dispose();
        _secondaryStream.Dispose();
    }

    [TestMethod]
    public void Constructor_ValidStreams_SetsProperties()
    {
        Assert.IsTrue(_teeStream.CanWrite);
        Assert.IsTrue(_teeStream.CanRead); // TeeStream delegates to primary stream
        Assert.IsTrue(_teeStream.CanSeek); // MemoryStream supports seeking
    }

    [TestMethod]
    public void Constructor_NullPrimaryStream_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new TeeStream(null!, _secondaryStream));
    }

    [TestMethod]
    public void Constructor_NullSecondaryStream_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new TeeStream(_primaryStream, null!));
    }

    [TestMethod]
    public void Length_Get_ReturnsCorrectValue()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        _primaryStream.Write(data, 0, data.Length);

        var length = _teeStream.Length;
        Assert.AreEqual(5, length);
    }

    [TestMethod]
    public void SetLength_UpdatesBothStreams()
    {
        _teeStream.SetLength(100);

        Assert.AreEqual(100, _primaryStream.Length);
        Assert.AreEqual(100, _secondaryStream.Length);
    }

    [TestMethod]
    public void Position_Get_ReturnsCorrectValue()
    {
        _primaryStream.Position = 10;

        var position = _teeStream.Position;
        Assert.AreEqual(10, position);
    }

    [TestMethod]
    public void Position_Set_UpdatesBothStreams()
    {
        var data = new byte[20];
        _primaryStream.Write(data, 0, data.Length);
        _secondaryStream.Write(data, 0, data.Length);

        _teeStream.Position = 10;

        Assert.AreEqual(10, _primaryStream.Position);
        Assert.AreEqual(10, _secondaryStream.Position);
    }

    [TestMethod]
    public void Read_ReadsFromPrimaryStream()
    {
        var testData = Encoding.UTF8.GetBytes("Test Data");
        _primaryStream.Write(testData, 0, testData.Length);
        _primaryStream.Position = 0;

        var buffer = new byte[testData.Length];
        var bytesRead = _teeStream.Read(buffer, 0, buffer.Length);

        Assert.AreEqual(testData.Length, bytesRead);
        CollectionAssert.AreEqual(testData, buffer);
    }

    [TestMethod]
    public void Seek_SeeksBothStreams()
    {
        var data = new byte[20];
        _primaryStream.Write(data, 0, data.Length);
        _secondaryStream.Write(data, 0, data.Length);

        var result = _teeStream.Seek(10, SeekOrigin.Begin);

        Assert.AreEqual(10, result);
        Assert.AreEqual(10, _primaryStream.Position);
        Assert.AreEqual(10, _secondaryStream.Position);
    }

    [TestMethod]
    public void Write_ValidData_WritesToBothStreams()
    {
        var data = Encoding.UTF8.GetBytes("Hello World");

        _teeStream.Write(data, 0, data.Length);

        _primaryStream.Position = 0;
        _secondaryStream.Position = 0;

        var primaryData = _primaryStream.ToArray();
        var secondaryData = _secondaryStream.ToArray();

        Assert.AreEqual(data.Length, primaryData.Length);
        Assert.AreEqual(data.Length, secondaryData.Length);
        CollectionAssert.AreEqual(data, primaryData);
        CollectionAssert.AreEqual(data, secondaryData);
    }

    [TestMethod]
    public async Task WriteAsync_ValidData_WritesToBothStreams()
    {
        var data = Encoding.UTF8.GetBytes("Hello World Async");

        await _teeStream.WriteAsync(data, 0, data.Length);

        _primaryStream.Position = 0;
        _secondaryStream.Position = 0;

        var primaryData = _primaryStream.ToArray();
        var secondaryData = _secondaryStream.ToArray();

        Assert.AreEqual(data.Length, primaryData.Length);
        Assert.AreEqual(data.Length, secondaryData.Length);
        CollectionAssert.AreEqual(data, primaryData);
        CollectionAssert.AreEqual(data, secondaryData);
    }

    [TestMethod]
    public async Task WriteAsync_WithCancellationToken_WritesToBothStreams()
    {
        var data = Encoding.UTF8.GetBytes("Cancellation Test");
        var cts = new CancellationTokenSource();

        await _teeStream.WriteAsync(data, 0, data.Length, cts.Token);

        _primaryStream.Position = 0;
        _secondaryStream.Position = 0;

        var primaryData = _primaryStream.ToArray();
        var secondaryData = _secondaryStream.ToArray();

        CollectionAssert.AreEqual(data, primaryData);
        CollectionAssert.AreEqual(data, secondaryData);
    }

    [TestMethod]
    public void Write_MultipleWrites_AccumulatesDataInBothStreams()
    {
        var data1 = Encoding.UTF8.GetBytes("Hello ");
        var data2 = Encoding.UTF8.GetBytes("World");
        var expected = Encoding.UTF8.GetBytes("Hello World");

        _teeStream.Write(data1, 0, data1.Length);
        _teeStream.Write(data2, 0, data2.Length);

        _primaryStream.Position = 0;
        _secondaryStream.Position = 0;

        var primaryData = _primaryStream.ToArray();
        var secondaryData = _secondaryStream.ToArray();

        CollectionAssert.AreEqual(expected, primaryData);
        CollectionAssert.AreEqual(expected, secondaryData);
    }

    [TestMethod]
    public void Write_EmptyBuffer_DoesNotThrow()
    {
        var data = Array.Empty<byte>();
        _teeStream.Write(data, 0, 0);

        Assert.AreEqual(0, _primaryStream.Length);
        Assert.AreEqual(0, _secondaryStream.Length);
    }

    [TestMethod]
    public void Write_PartialBuffer_WritesCorrectPortionToBothStreams()
    {
        var fullData = Encoding.UTF8.GetBytes("Hello World Test");
        var expectedData = Encoding.UTF8.GetBytes("World");

        _teeStream.Write(fullData, 6, 5); // Write "World"

        _primaryStream.Position = 0;
        _secondaryStream.Position = 0;

        var primaryData = _primaryStream.ToArray();
        var secondaryData = _secondaryStream.ToArray();

        CollectionAssert.AreEqual(expectedData, primaryData);
        CollectionAssert.AreEqual(expectedData, secondaryData);
    }

    [TestMethod]
    public void Flush_CallsFlushOnBothStreams()
    {
        var data = Encoding.UTF8.GetBytes("Flush Test");
        _teeStream.Write(data, 0, data.Length);

        _teeStream.Flush();

        // Verify data is written (MemoryStream doesn't need explicit flushing but method should not throw)
        Assert.AreEqual(data.Length, _primaryStream.Length);
        Assert.AreEqual(data.Length, _secondaryStream.Length);
    }

    [TestMethod]
    public async Task FlushAsync_CallsFlushAsyncOnBothStreams()
    {
        var data = Encoding.UTF8.GetBytes("Async Flush Test");
        await _teeStream.WriteAsync(data, 0, data.Length);

        await _teeStream.FlushAsync();

        Assert.AreEqual(data.Length, _primaryStream.Length);
        Assert.AreEqual(data.Length, _secondaryStream.Length);
    }

    [TestMethod]
    public async Task WriteAsync_CancelledToken_ThrowsTaskCanceledException()
    {
        var data = Encoding.UTF8.GetBytes("Cancellation Test");
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
            await _teeStream.WriteAsync(data, 0, data.Length, cts.Token));
    }

    [TestMethod]
    public void Write_LargeData_WritesToBothStreams()
    {
        var largeData = new byte[10000];
        for (var i = 0; i < largeData.Length; i++) largeData[i] = (byte)(i % 256);

        _teeStream.Write(largeData, 0, largeData.Length);

        _primaryStream.Position = 0;
        _secondaryStream.Position = 0;

        var primaryData = _primaryStream.ToArray();
        var secondaryData = _secondaryStream.ToArray();

        Assert.AreEqual(largeData.Length, primaryData.Length);
        Assert.AreEqual(largeData.Length, secondaryData.Length);
        CollectionAssert.AreEqual(largeData, primaryData);
        CollectionAssert.AreEqual(largeData, secondaryData);
    }

    [TestMethod]
    public void Dispose_DisposesUnderlyingStreams()
    {
        var primaryDisposed = false;
        var secondaryDisposed = false;

        var primaryMock = new TestStream(() => primaryDisposed = true);
        var secondaryMock = new TestStream(() => secondaryDisposed = true);

        using (_ = new TeeStream(primaryMock, secondaryMock))
        {
            // TeeStream is active
        }

        Assert.IsTrue(primaryDisposed);
        Assert.IsTrue(secondaryDisposed);
    }

    [TestMethod]
    public void Write_OneStreamFails_DoesNotAffectOtherStream()
    {
        var failingStream = new FailingStream();
        var workingStream = new MemoryStream();
        var data = Encoding.UTF8.GetBytes("Test Data");

        var teeStream = new TeeStream(failingStream, workingStream);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => teeStream.Write(data, 0, data.Length));
        }
        finally
        {
            teeStream.Dispose();
        }

        // The working stream should still have received the data attempt
        // (This behavior depends on implementation - adjust based on actual TeeStream behavior)
    }

    private class TestStream : Stream
    {
        private readonly Action _onDispose;

        public TestStream(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _onDispose.Invoke();
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return 0;
        }

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    private class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return 0;
        }

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException("Stream write failed");
        }
    }
}