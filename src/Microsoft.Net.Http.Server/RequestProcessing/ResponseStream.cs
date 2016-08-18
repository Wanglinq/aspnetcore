// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING
// WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF
// TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR
// NON-INFRINGEMENT.
// See the Apache 2 License for the specific language governing
// permissions and limitations under the License.

// ------------------------------------------------------------------------------
// <copyright file="_HttpResponseStream.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static Microsoft.Net.Http.Server.UnsafeNclNativeMethods;

namespace Microsoft.Net.Http.Server
{
    internal class ResponseStream : Stream
    {
        private RequestContext _requestContext;
        private long _leftToWrite = long.MinValue;
        private bool _closed;
        private bool _inOpaqueMode;

        // The last write needs special handling to cancel.
        private ResponseStreamAsyncResult _lastWrite;

        internal ResponseStream(RequestContext requestContext)
        {
            _requestContext = requestContext;
        }

        internal RequestContext RequestContext
        {
            get { return _requestContext; }
        }

        private SafeHandle RequestQueueHandle => RequestContext.Server.RequestQueue.Handle;

        private ulong RequestId => RequestContext.Request.RequestId;

        private ILogger Logger => RequestContext.Server.Logger;

        public override bool CanSeek
        {
            get
            {
                return false;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return true;
            }
        }

        public override bool CanRead
        {
            get
            {
                return false;
            }
        }

        public override long Length
        {
            get
            {
                throw new NotSupportedException(Resources.Exception_NoSeek);
            }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException(Resources.Exception_NoSeek);
            }
            set
            {
                throw new NotSupportedException(Resources.Exception_NoSeek);
            }
        }

        // Send headers
        public override void Flush()
        {
            if (_closed)
            {
                return;
            }
            FlushInternal(endOfRequest: false);
        }

        // We never expect endOfRequest and data at the same time
        private unsafe void FlushInternal(bool endOfRequest, ArraySegment<byte> data = new ArraySegment<byte>())
        {
            Debug.Assert(!(endOfRequest && data.Count > 0), "Data is not supported at the end of the request.");

            var started = _requestContext.Response.HasStarted;
            if (data.Count == 0 && started && !endOfRequest)
            {
                // Empty flush
                return;
            }

            var flags = ComputeLeftToWrite(endOfRequest);
            if (!_inOpaqueMode && endOfRequest && _leftToWrite > data.Count)
            {
                _requestContext.Abort();
                // This is logged rather than thrown because it is too late for an exception to be visible in user code.
                LogHelper.LogError(Logger, "ResponseStream::Dispose", "Fewer bytes were written than were specified in the Content-Length.");
                return;
            }

            if (endOfRequest && _requestContext.Response.BoundaryType == BoundaryType.Close)
            {
                flags |= HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_DISCONNECT;
            }
            else if (!endOfRequest && _leftToWrite != data.Count)
            {
                flags |= HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_MORE_DATA;
            }

            UpdateWritenCount((uint)data.Count);
            uint statusCode = 0;
            HttpApi.HTTP_DATA_CHUNK[] dataChunks;
            var pinnedBuffers = PinDataBuffers(endOfRequest, data, out dataChunks);
            try
            {
                if (!started)
                {
                    statusCode = _requestContext.Response.SendHeaders(dataChunks, null, flags, false);
                }
                else
                {
                    fixed (HttpApi.HTTP_DATA_CHUNK* pDataChunks = dataChunks)
                    {
                        statusCode = HttpApi.HttpSendResponseEntityBody(
                                RequestQueueHandle,
                                RequestId,
                                (uint)flags,
                                (ushort)dataChunks.Length,
                                pDataChunks,
                                null,
                                SafeLocalFree.Zero,
                                0,
                                SafeNativeOverlapped.Zero,
                                IntPtr.Zero);
                    }

                    if (_requestContext.Server.Settings.IgnoreWriteExceptions)
                    {
                        statusCode = UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS;
                    }
                }
            }
            finally
            {
                FreeDataBuffers(pinnedBuffers);
            }

            if (statusCode != ErrorCodes.ERROR_SUCCESS && statusCode != ErrorCodes.ERROR_HANDLE_EOF
                // Don't throw for disconnects, we were already finished with the response.
                && (!endOfRequest || (statusCode != ErrorCodes.ERROR_CONNECTION_INVALID && statusCode != ErrorCodes.ERROR_INVALID_PARAMETER)))
            {
                Exception exception = new IOException(string.Empty, new WebListenerException((int)statusCode));
                LogHelper.LogException(Logger, "Flush", exception);
                Abort();
                throw exception;
            }
        }

        private List<GCHandle> PinDataBuffers(bool endOfRequest, ArraySegment<byte> data, out HttpApi.HTTP_DATA_CHUNK[] dataChunks)
        {
            var pins = new List<GCHandle>();
            var chunked = _requestContext.Response.BoundaryType == BoundaryType.Chunked;

            var currentChunk = 0;
            // Figure out how many data chunks
            if (chunked && data.Count == 0 && endOfRequest)
            {
                dataChunks = new HttpApi.HTTP_DATA_CHUNK[1];
                SetDataChunk(dataChunks, ref currentChunk, pins, new ArraySegment<byte>(Helpers.ChunkTerminator));
                return pins;
            }
            else if (data.Count == 0)
            {
                // No data
                dataChunks = new HttpApi.HTTP_DATA_CHUNK[0];
                return pins;
            }

            var chunkCount = 1;
            if (chunked)
            {
                // Chunk framing
                chunkCount += 2;

                if (endOfRequest)
                {
                    // Chunk terminator
                    chunkCount += 1;
                }
            }
            dataChunks = new HttpApi.HTTP_DATA_CHUNK[chunkCount];

            if (chunked)
            {
                var chunkHeaderBuffer = Helpers.GetChunkHeader(data.Count);
                SetDataChunk(dataChunks, ref currentChunk, pins, chunkHeaderBuffer);
            }

            SetDataChunk(dataChunks, ref currentChunk, pins, data);

            if (chunked)
            {
                SetDataChunk(dataChunks, ref currentChunk, pins, new ArraySegment<byte>(Helpers.CRLF));

                if (endOfRequest)
                {
                    SetDataChunk(dataChunks, ref currentChunk, pins, new ArraySegment<byte>(Helpers.ChunkTerminator));
                }
            }

            return pins;
        }

        private static void SetDataChunk(HttpApi.HTTP_DATA_CHUNK[] chunks, ref int chunkIndex, List<GCHandle> pins, ArraySegment<byte> buffer)
        {
            var handle = GCHandle.Alloc(buffer.Array, GCHandleType.Pinned);
            pins.Add(handle);
            chunks[chunkIndex].DataChunkType = HttpApi.HTTP_DATA_CHUNK_TYPE.HttpDataChunkFromMemory;
            chunks[chunkIndex].fromMemory.pBuffer = handle.AddrOfPinnedObject() + buffer.Offset;
            chunks[chunkIndex].fromMemory.BufferLength = (uint)buffer.Count;
            chunkIndex++;
        }

        private void FreeDataBuffers(List<GCHandle> pinnedBuffers)
        {
            foreach (var pin in pinnedBuffers)
            {
                if (pin.IsAllocated)
                {
                    pin.Free();
                }
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_closed)
            {
                return Helpers.CompletedTask();
            }
            return FlushInternalAsync(new ArraySegment<byte>(), cancellationToken);
        }

        // Simpler than Flush because it will never be called at the end of the request from Dispose.
        private unsafe Task FlushInternalAsync(ArraySegment<byte> data, CancellationToken cancellationToken)
        {
            var started = _requestContext.Response.HasStarted;
            if (data.Count == 0 && started)
            {
                // Empty flush
                return Helpers.CompletedTask();
            }

            var cancellationRegistration = default(CancellationTokenRegistration);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = RequestContext.RegisterForCancellation(cancellationToken);
            }

            var flags = ComputeLeftToWrite();
            if (_leftToWrite != data.Count)
            {
                flags |= HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_MORE_DATA;
            }

            UpdateWritenCount((uint)data.Count);
            uint statusCode = 0;
            var chunked = _requestContext.Response.BoundaryType == BoundaryType.Chunked;
            var asyncResult = new ResponseStreamAsyncResult(this, data, chunked, cancellationRegistration);
            uint bytesSent = 0;
            try
            {
                if (!started)
                {
                    statusCode = _requestContext.Response.SendHeaders(null, asyncResult, flags, false);
                    bytesSent = asyncResult.BytesSent;
                }
                else
                {
                    statusCode = HttpApi.HttpSendResponseEntityBody(
                        RequestQueueHandle,
                        RequestId,
                        (uint)flags,
                        asyncResult.DataChunkCount,
                        asyncResult.DataChunks,
                        &bytesSent,
                        SafeLocalFree.Zero,
                        0,
                        asyncResult.NativeOverlapped,
                        IntPtr.Zero);
                }
            }
            catch (Exception e)
            {
                LogHelper.LogException(Logger, "FlushAsync", e);
                asyncResult.Dispose();
                Abort();
                throw;
            }

            if (statusCode != ErrorCodes.ERROR_SUCCESS && statusCode != ErrorCodes.ERROR_IO_PENDING)
            {
                asyncResult.Dispose();
                if (_requestContext.Server.Settings.IgnoreWriteExceptions && started)
                {
                    asyncResult.Complete();
                }
                else
                {
                    Exception exception = new IOException(string.Empty, new WebListenerException((int)statusCode));
                    LogHelper.LogException(Logger, "FlushAsync", exception);
                    Abort();
                    throw exception;
                }
            }

            if (statusCode == ErrorCodes.ERROR_SUCCESS && WebListener.SkipIOCPCallbackOnSuccess)
            {
                // IO operation completed synchronously - callback won't be called to signal completion.
                asyncResult.IOCompleted(statusCode, bytesSent);
            }

            // Last write, cache it for special cancellation handling.
            if ((flags & HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_MORE_DATA) == 0)
            {
                _lastWrite = asyncResult;
            }

            return asyncResult.Task;
        }

        #region NotSupported Read/Seek

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException(Resources.Exception_NoSeek);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException(Resources.Exception_NoSeek);
        }

        public override int Read([In, Out] byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException(Resources.Exception_WriteOnlyStream);
        }

#if !NETSTANDARD1_3
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            throw new InvalidOperationException(Resources.Exception_WriteOnlyStream);
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            throw new InvalidOperationException(Resources.Exception_WriteOnlyStream);
        }
#endif

        #endregion

        internal void Abort()
        {
            _closed = true;
            _requestContext.Abort();
        }

        private HttpApi.HTTP_FLAGS ComputeLeftToWrite(bool endOfRequest = false)
        {
            var flags = HttpApi.HTTP_FLAGS.NONE;
            if (!_requestContext.Response.HasComputedHeaders)
            {
                flags = _requestContext.Response.ComputeHeaders(endOfRequest);
            }
            if (_leftToWrite == long.MinValue)
            {
                if (_requestContext.Request.IsHeadMethod)
                {
                    _leftToWrite = 0;
                }
                else if (_requestContext.Response.BoundaryType == BoundaryType.ContentLength)
                {
                    _leftToWrite = _requestContext.Response.ExpectedBodyLength;
                }
                else
                {
                    _leftToWrite = -1; // unlimited
                }
            }
            return flags;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Validates for null and bounds. Allows count == 0.
            var data = new ArraySegment<byte>(buffer, offset, count);
            CheckDisposed();
            // TODO: Verbose log parameters

            var contentLength = _requestContext.Response.ContentLength;
            if (contentLength.HasValue && !_requestContext.Response.HasComputedHeaders && contentLength.Value <= data.Count)
            {
                if (contentLength.Value < data.Count)
                {
                    throw new InvalidOperationException("More bytes written than specified in the Content-Length header.");
                }
            }
            // The last write in a response that has already started, flush immediately
            else if (_requestContext.Response.HasComputedHeaders && _leftToWrite >= 0 && _leftToWrite <= data.Count)
            {
                if (_leftToWrite < data.Count)
                {
                    throw new InvalidOperationException("More bytes written than specified in the Content-Length header.");
                }
            }

            FlushInternal(endOfRequest: false, data: data);
        }

#if NETSTANDARD1_3
        public IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
#else
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
#endif
        {
            return WriteAsync(buffer, offset, count).ToIAsyncResult(callback, state);
        }
#if NETSTANDARD1_3
        public void EndWrite(IAsyncResult asyncResult)
#else
        public override void EndWrite(IAsyncResult asyncResult)
#endif
        {
            if (asyncResult == null)
            {
                throw new ArgumentNullException(nameof(asyncResult));
            }
            ((Task)asyncResult).GetAwaiter().GetResult();
        }

        public override unsafe Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Validates for null and bounds. Allows count == 0.
            var data = new ArraySegment<byte>(buffer, offset, count);
            if (cancellationToken.IsCancellationRequested)
            {
                return Helpers.CanceledTask<int>();
            }
            CheckDisposed();
            // TODO: Verbose log parameters

            var contentLength = _requestContext.Response.ContentLength;
            if (contentLength.HasValue && !_requestContext.Response.HasComputedHeaders && contentLength.Value <= data.Count)
            {
                if (contentLength.Value < data.Count)
                {
                    throw new InvalidOperationException("More bytes written than specified in the Content-Length header.");
                }
            }
            // The last write in a response that has already started, flush immediately
            else if (_requestContext.Response.HasComputedHeaders && _leftToWrite > 0 && _leftToWrite <= data.Count)
            {
                if (_leftToWrite < data.Count)
                {
                    throw new InvalidOperationException("More bytes written than specified in the Content-Length header.");
                }
            }

            return FlushInternalAsync(data, cancellationToken);
        }

        internal async Task SendFileAsync(string fileName, long offset, long? count, CancellationToken cancellationToken)
        {
            // It's too expensive to validate the file attributes before opening the file. Open the file and then check the lengths.
            // This all happens inside of ResponseStreamAsyncResult.
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentNullException("fileName");
            }
            CheckDisposed();

            // We can't mix await and unsafe so separate the unsafe code into another method.
            await SendFileAsyncCore(fileName, offset, count, cancellationToken);
        }

        internal unsafe Task SendFileAsyncCore(string fileName, long offset, long? count, CancellationToken cancellationToken)
        {
            var flags = ComputeLeftToWrite();
            if (count == 0 && _leftToWrite != 0)
            {
                return Helpers.CompletedTask();
            }
            if (_leftToWrite >= 0 && count > _leftToWrite)
            {
                throw new InvalidOperationException(Resources.Exception_TooMuchWritten);
            }
            // TODO: Verbose log

            if (cancellationToken.IsCancellationRequested)
            {
                return Helpers.CanceledTask<int>();
            }

            var cancellationRegistration = default(CancellationTokenRegistration);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = RequestContext.RegisterForCancellation(cancellationToken);
            }

            uint statusCode;
            uint bytesSent = 0;
            var started = _requestContext.Response.HasStarted;
            var chunked = _requestContext.Response.BoundaryType == BoundaryType.Chunked;
            ResponseStreamAsyncResult asyncResult = new ResponseStreamAsyncResult(this, fileName, offset, count, chunked, cancellationRegistration);

            long bytesWritten;
            if (chunked)
            {
                bytesWritten = 0;
            }
            else if (count.HasValue)
            {
                bytesWritten = count.Value;
            }
            else
            {
                bytesWritten = asyncResult.FileLength - offset;
            }
            // Update _leftToWrite now so we can queue up additional calls to SendFileAsync.
            flags |= _leftToWrite == bytesWritten ? HttpApi.HTTP_FLAGS.NONE : HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_MORE_DATA;
            UpdateWritenCount((uint)bytesWritten);

            try
            {
                if (!started)
                {
                    statusCode = _requestContext.Response.SendHeaders(null, asyncResult, flags, false);
                    bytesSent = asyncResult.BytesSent;
                }
                else
                {
                    // TODO: If opaque then include the buffer data flag.
                    statusCode = HttpApi.HttpSendResponseEntityBody(
                            RequestQueueHandle,
                            RequestId,
                            (uint)flags,
                            asyncResult.DataChunkCount,
                            asyncResult.DataChunks,
                            &bytesSent,
                            SafeLocalFree.Zero,
                            0,
                            asyncResult.NativeOverlapped,
                            IntPtr.Zero);
                }
            }
            catch (Exception e)
            {
                LogHelper.LogException(Logger, "SendFileAsync", e);
                asyncResult.Dispose();
                Abort();
                throw;
            }

            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS && statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
            {
                asyncResult.Dispose();
                if (_requestContext.Server.Settings.IgnoreWriteExceptions && started)
                {
                    asyncResult.Complete();
                }
                else
                {
                    Exception exception = new IOException(string.Empty, new WebListenerException((int)statusCode));
                    LogHelper.LogException(Logger, "SendFileAsync", exception);
                    Abort();
                    throw exception;
                }
            }

            if (statusCode == ErrorCodes.ERROR_SUCCESS && WebListener.SkipIOCPCallbackOnSuccess)
            {
                // IO operation completed synchronously - callback won't be called to signal completion.
                asyncResult.IOCompleted(statusCode, bytesSent);
            }

            // Last write, cache it for special cancellation handling.
            if ((flags & HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_MORE_DATA) == 0)
            {
                _lastWrite = asyncResult;
            }

            return asyncResult.Task;
        }

        private void UpdateWritenCount(uint dataWritten)
        {
            if (!_inOpaqueMode)
            {
                if (_leftToWrite > 0)
                {
                    // keep track of the data transferred
                    _leftToWrite -= dataWritten;
                }
                if (_leftToWrite == 0)
                {
                    // in this case we already passed 0 as the flag, so we don't need to call HttpSendResponseEntityBody() when we Close()
                    _closed = true;
                }
            }
        }

        protected override unsafe void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    if (_closed)
                    {
                        return;
                    }
                    _closed = true;
                    FlushInternal(endOfRequest: true);
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        internal void SwitchToOpaqueMode()
        {
            _inOpaqueMode = true;
            _leftToWrite = long.MaxValue;
        }

        // The final Content-Length async write can only be Canceled by CancelIoEx.
        // Sync can only be Canceled by CancelSynchronousIo, but we don't attempt this right now.
        [SuppressMessage("Microsoft.Usage", "CA1806:DoNotIgnoreMethodResults", Justification =
            "It is safe to ignore the return value on a cancel operation because the connection is being closed")]
        internal unsafe void CancelLastWrite()
        {
            ResponseStreamAsyncResult asyncState = _lastWrite;
            if (asyncState != null && !asyncState.IsCompleted)
            {
                UnsafeNclNativeMethods.CancelIoEx(RequestQueueHandle, asyncState.NativeOverlapped);
            }
        }

        private void CheckDisposed()
        {
            if (_closed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }
    }
}
